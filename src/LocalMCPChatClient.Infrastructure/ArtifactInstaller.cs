using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.Infrastructure;

public sealed class ArtifactInstaller(HttpClient httpClient, IAppPaths paths) : IArtifactInstaller
{
    private const int BufferSize = 1024 * 256;

    public bool IsInstalled(ArtifactDescriptor artifact)
    {
        if (artifact.Kind == ArtifactKind.Runtime && artifact.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var runtimeDirectory = GetRuntimeDirectory(artifact);
            var markerExists = File.Exists(Path.Combine(runtimeDirectory, $".{SanitizeSegment(artifact.Id)}.installed"));
            if (artifact.IsRuntimeDependency) return markerExists;
            return markerExists && Directory.Exists(runtimeDirectory) &&
                Directory.EnumerateFiles(runtimeDirectory, "llama-server.exe", SearchOption.AllDirectories).Any();
        }
        var destination = GetDestinationPath(artifact);
        return File.Exists(destination);
    }

    public async Task<string> InstallAsync(
        ArtifactDescriptor artifact,
        IProgress<ArtifactProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        paths.EnsureCreated();

        var destination = GetDestinationPath(artifact);
        if (artifact.Kind == ArtifactKind.Runtime && artifact.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && IsInstalled(artifact))
        {
            var runtimeDirectory = GetRuntimeDirectory(artifact);
            if (artifact.IsRuntimeDependency) return runtimeDirectory;
            return Directory.EnumerateFiles(runtimeDirectory, "llama-server.exe", SearchOption.AllDirectories).First();
        }
        if (File.Exists(destination))
        {
            if (string.IsNullOrWhiteSpace(artifact.Sha256)) return destination;
            progress?.Report(new ArtifactProgress(new FileInfo(destination).Length, artifact.Size, "既存ファイルのSHA-256を検証中"));
            var existingHash = await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
            if (existingHash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) return destination;
            var quarantine = destination + ".bad-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(destination, quarantine, true);
        }

        EnsureFreeSpace(artifact);
        var partialPath = Path.Combine(paths.DownloadsDirectory, artifact.Id + ".partial");
        var total = await DownloadWithRetriesAsync(artifact, partialPath, progress, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(artifact.Sha256))
        {
            progress?.Report(new ArtifactProgress(new FileInfo(partialPath).Length, total, "SHA-256を検証中"));
            var actualHash = await ComputeSha256Async(partialPath, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                var quarantine = partialPath + ".bad-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(partialPath, quarantine, true);
                throw new InvalidDataException($"{artifact.DisplayName} のSHA-256が一致しません。破損ファイルを {quarantine} に隔離しました。");
            }
        }

        if (artifact.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var runtimeDirectory = GetRuntimeDirectory(artifact);
            Directory.CreateDirectory(runtimeDirectory);
            await ExtractZipSafelyAsync(partialPath, runtimeDirectory, cancellationToken).ConfigureAwait(false);
            File.Delete(partialPath);
            await File.WriteAllTextAsync(Path.Combine(runtimeDirectory, $".{SanitizeSegment(artifact.Id)}.installed"), artifact.Sha256 ?? string.Empty, cancellationToken).ConfigureAwait(false);
            if (artifact.IsRuntimeDependency) return runtimeDirectory;
            var executable = artifact.ArchiveEntry is { Length: > 0 }
                ? Path.Combine(runtimeDirectory, artifact.ArchiveEntry.Replace('/', Path.DirectorySeparatorChar))
                : Directory.EnumerateFiles(runtimeDirectory, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (executable is null || !File.Exists(executable))
            {
                throw new InvalidDataException("アーカイブに llama-server.exe が見つかりません。");
            }
            return executable;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(partialPath, destination, true);
        progress?.Report(new ArtifactProgress(new FileInfo(destination).Length, total, "完了"));
        return destination;
    }

    private async Task<long?> DownloadWithRetriesAsync(
        ArtifactDescriptor artifact,
        string partialPath,
        IProgress<ArtifactProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await DownloadOnceAsync(artifact, partialPath, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt < maximumAttempts &&
                !cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or IOException or TaskCanceledException)
            {
                var received = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
                progress?.Report(new ArtifactProgress(received, artifact.Size, $"通信が停止したため再接続中 ({attempt}/{maximumAttempts - 1})"));
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<long?> DownloadOnceAsync(
        ArtifactDescriptor artifact,
        string partialPath,
        IProgress<ArtifactProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0L;
        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.DownloadUri);
        if (existingLength > 0) request.Headers.Range = new RangeHeaderValue(existingLength, null);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (existingLength > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && artifact.Size == existingLength)
            return artifact.Size;
        if (existingLength > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }
        else if (existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent &&
                 response.Content.Headers.ContentRange?.From != existingLength)
        {
            File.Delete(partialPath);
            throw new IOException("サーバーが要求と異なる範囲を返しました。先頭から再試行します。");
        }
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength is { } contentLength ? contentLength + existingLength : artifact.Size;
        progress?.Report(new ArtifactProgress(existingLength, total, "ダウンロード中"));
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(partialPath, FileMode.Append, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[BufferSize];
            var received = existingLength;
            int read;
            using var inactivityTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (true)
            {
                inactivityTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    read = await input.ReadAsync(buffer, inactivityTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("ダウンロードデータを60秒間受信できませんでした。", exception);
                }
                inactivityTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report(new ArtifactProgress(received, total, "ダウンロード中"));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        return total;
    }

    public Task<string> ImportAsync(ArtifactKind kind, string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("指定されたファイルが見つかりません。", fullPath);
        if (kind == ArtifactKind.Model && !fullPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("モデルにはGGUFファイルを指定してください。");
        if (kind == ArtifactKind.Runtime && !Path.GetFileName(fullPath).Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ランタイムにはllama-server.exeを指定してください。");
        return Task.FromResult(fullPath);
    }

    private string GetDestinationPath(ArtifactDescriptor artifact) => artifact.Kind switch
    {
        ArtifactKind.Model => Path.Combine(artifact.DestinationDirectory ?? paths.ModelsDirectory, artifact.FileName),
        ArtifactKind.Runtime when artifact.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) =>
            Path.Combine(GetRuntimeDirectory(artifact), artifact.ArchiveEntry ?? "llama-server.exe"),
        ArtifactKind.Runtime => Path.Combine(paths.RuntimesDirectory, SanitizeSegment(artifact.Id), artifact.FileName),
        _ => throw new ArgumentOutOfRangeException(nameof(artifact))
    };

    private string GetRuntimeDirectory(ArtifactDescriptor artifact) => Path.Combine(
        paths.RuntimesDirectory, SanitizeSegment(artifact.InstallationGroup ?? artifact.Id));

    private void EnsureFreeSpace(ArtifactDescriptor artifact)
    {
        if (artifact.Size is not { } size) return;
        var root = Path.GetPathRoot(paths.DownloadsDirectory) ?? paths.DownloadsDirectory;
        var drive = new DriveInfo(root);
        var required = checked((long)(size * 1.15));
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"空き容量が不足しています。必要: 約 {required / 1024d / 1024d / 1024d:F1} GiB");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task ExtractZipSafelyAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("アーカイブに不正なパスが含まれています。");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
