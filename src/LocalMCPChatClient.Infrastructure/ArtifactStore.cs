using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using LocalMCPChatClient.Core;

#pragma warning disable CA1416 // LocalMCPChatClient is a Windows-only WPF application; inference derivatives use System.Drawing on Windows.

namespace LocalMCPChatClient.Infrastructure;

public sealed class ArtifactStore(IAppPaths paths) : IArtifactStore
{
    public const long DefaultQuotaBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumArtifactBytes = 16 * 1024 * 1024;
    public const long MaximumDecodedPixels = 64L * 1024 * 1024;

    public async Task<StoredArtifact> StoreAsync(
        ReadOnlyMemory<byte> data,
        string mimeType,
        string displayName,
        string? sourceUri = null,
        CancellationToken cancellationToken = default)
    {
        if (data.Length == 0) throw new InvalidDataException("Artifact is empty.");
        if (data.Length > MaximumArtifactBytes)
            throw new InvalidDataException($"Artifact exceeds the {MaximumArtifactBytes / 1024 / 1024} MiB limit.");

        var (width, height) = ReadImageDimensions(data.Span, mimeType);
        if (width is > 0 && height is > 0 && (long)width.Value * height.Value > MaximumDecodedPixels)
            throw new InvalidDataException("Decoded image exceeds the 64-megapixel safety limit.");

        paths.EnsureCreated();
        var hash = Convert.ToHexString(SHA256.HashData(data.Span)).ToLowerInvariant();
        var extension = ExtensionFor(mimeType);
        var relativePath = Path.Combine(hash[..2], hash + extension);
        var destination = Path.Combine(paths.ArtifactsDirectory, relativePath);
        if (!File.Exists(destination))
        {
            var storedBytes = await GetStoredByteCountAsync(cancellationToken).ConfigureAwait(false);
            if (storedBytes + data.Length > DefaultQuotaBytes)
                throw new IOException("Artifact storage quota (2 GiB) would be exceeded. Open Settings → Approval & Privacy → MCP artifacts to manage the referenced conversations.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, data.ToArray(), cancellationToken).ConfigureAwait(false);
                try { File.Move(temporary, destination, false); }
                catch (IOException) when (File.Exists(destination)) { }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        return new StoredArtifact(hash, hash, mimeType, relativePath, SanitizeDisplayName(displayName), data.Length, width, height, sourceUri);
    }

    public Task<byte[]> ReadAsync(StoredArtifact artifact, CancellationToken cancellationToken = default)
        => File.ReadAllBytesAsync(GetAbsolutePath(artifact), cancellationToken);

    public async Task<(byte[] Data, string MimeType)> ReadForInferenceAsync(StoredArtifact artifact, CancellationToken cancellationToken = default)
    {
        const int maximumEdge = 2048;
        if (artifact.Width is not > maximumEdge && artifact.Height is not > maximumEdge)
            return (await ReadAsync(artifact, cancellationToken).ConfigureAwait(false), artifact.MimeType);
        if (!artifact.MimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
            return (await ReadAsync(artifact, cancellationToken).ConfigureAwait(false), artifact.MimeType);

        var originalPath = GetAbsolutePath(artifact);
        var derivedPath = Path.Combine(Path.GetDirectoryName(originalPath)!, artifact.Sha256 + ".vision-2048.png");
        if (!File.Exists(derivedPath))
        {
            await using var input = File.OpenRead(originalPath);
            using var source = Image.FromStream(input, useEmbeddedColorManagement: true, validateImageData: true);
            var scale = Math.Min(maximumEdge / (double)source.Width, maximumEdge / (double)source.Height);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, 0, 0, width, height);
            }
            var temporary = derivedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                bitmap.Save(temporary, ImageFormat.Png);
                var derivedBytes = new FileInfo(temporary).Length;
                if (await GetStoredByteCountAsync(cancellationToken).ConfigureAwait(false) + derivedBytes > DefaultQuotaBytes)
                    throw new IOException("Artifact storage quota (2 GiB) would be exceeded by the inference derivative. Open Settings → Approval & Privacy → MCP artifacts.");
                try { File.Move(temporary, derivedPath, false); }
                catch (IOException) when (File.Exists(derivedPath)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return (await File.ReadAllBytesAsync(derivedPath, cancellationToken).ConfigureAwait(false), "image/png");
    }

    public string GetAbsolutePath(StoredArtifact artifact)
    {
        var root = Path.GetFullPath(paths.ArtifactsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(paths.ArtifactsDirectory, artifact.RelativePath));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact path escapes the artifact directory.");
        return fullPath;
    }

    public Task<long> GetStoredByteCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(paths.ArtifactsDirectory)) return Task.FromResult(0L);
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(paths.ArtifactsDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) total += new FileInfo(file).Length;
        }
        return Task.FromResult(total);
    }

    public Task CollectGarbageAsync(IReadOnlySet<string> referencedSha256s, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.ArtifactsDirectory)) return Task.CompletedTask;
        foreach (var file in Directory.EnumerateFiles(paths.ArtifactsDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(file);
            var hash = name.Length >= 64 ? name[..64] : string.Empty;
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit) && !referencedSha256s.Contains(hash)) File.Delete(file);
        }
        foreach (var directory in Directory.EnumerateDirectories(paths.ArtifactsDirectory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        return Task.CompletedTask;
    }

    private static string ExtensionFor(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "application/json" => ".json",
        _ => ".bin"
    };

    private static string SanitizeDisplayName(string value)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(value) ? "artifact" : value.Trim());
        return name.Length <= 128 ? name : name[..128];
    }

    private static (int? Width, int? Height) ReadImageDimensions(ReadOnlySpan<byte> data, string mimeType)
    {
        if (!mimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase) || data.Length < 24 ||
            !data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return (null, null);
        var width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG dimensions are invalid.");
        return (width, height);
    }
}
