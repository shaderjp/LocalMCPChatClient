using System.Text;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.Infrastructure;

internal static class ResourceSnapshotBudget
{
    public static IReadOnlyList<McpResourceSnapshot> Apply(
        IReadOnlyList<McpResourceSnapshot> snapshots,
        int contextSize)
    {
        if (snapshots.Count == 0) return snapshots;
        var totalTokenBudget = Math.Max(1, contextSize / 4);
        var bytesPerResource = Math.Max(1, totalTokenBudget * 3 / snapshots.Count);
        return snapshots.Select(snapshot => Apply(snapshot, bytesPerResource)).ToList();
    }

    private static McpResourceSnapshot Apply(McpResourceSnapshot snapshot, int maximumBytes)
    {
        var currentBytes = Encoding.UTF8.GetByteCount(snapshot.Content);
        if (currentBytes <= maximumBytes) return snapshot;
        return snapshot with
        {
            Content = TruncateUtf8(snapshot.Content, maximumBytes),
            OriginalByteCount = Math.Max(snapshot.OriginalByteCount, currentBytes),
            WasTruncated = true
        };
    }

    internal static string TruncateUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes) return value;
        var bytes = new byte[maximumBytes];
        Encoding.UTF8.GetEncoder().Convert(
            value.AsSpan(), bytes.AsSpan(), flush: true,
            out var charactersUsed, out _, out _);
        return value[..charactersUsed];
    }
}
