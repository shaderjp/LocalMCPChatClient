using CommunityToolkit.Mvvm.ComponentModel;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.App.ViewModels;

public sealed partial class ChatItemViewModel : ObservableObject
{
    public ChatItemViewModel(
        ChatRole role,
        string content,
        bool isError = false,
        string? toolName = null,
        IReadOnlyList<McpResourceSnapshot>? resources = null,
        IReadOnlyList<McpContentPart>? contentParts = null,
        IArtifactStore? artifactStore = null)
    {
        Role = role;
        _content = content;
        IsError = isError;
        ToolName = toolName;
        Resources = (resources ?? []).Select(resource => new ResourceAttachmentViewModel(resource)).ToList();
        Artifacts = (contentParts ?? [])
            .Concat((resources ?? []).SelectMany(resource => resource.Parts ?? []))
            .Where(part => part.Artifact is not null)
            .GroupBy(part => part.Artifact!.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ArtifactAttachmentViewModel(group.First(), artifactStore))
            .ToList();
    }

    public ChatRole Role { get; }
    public bool IsError { get; }
    public string? ToolName { get; }
    public IReadOnlyList<ResourceAttachmentViewModel> Resources { get; }
    public bool HasResources => Resources.Count > 0;
    public IReadOnlyList<ArtifactAttachmentViewModel> Artifacts { get; }
    public bool HasArtifacts => Artifacts.Count > 0;
    public string ArtifactSummary => $"Artifact ({Artifacts.Count})";
    public string ResourceSummary => $"添付Resource ({Resources.Count})";
    public bool IsUser => Role == ChatRole.User;
    public bool IsTool => Role == ChatRole.Tool;
    public string RoleLabel => Role switch
    {
        ChatRole.User => "あなた",
        ChatRole.Assistant => "Gemma",
        ChatRole.Tool => string.IsNullOrWhiteSpace(ToolName) ? "MCP Tool" : ToolName,
        _ => "System"
    };

    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isStreaming;
}

public sealed record ArtifactAttachmentViewModel
{
    public ArtifactAttachmentViewModel(McpContentPart part, IArtifactStore? artifactStore)
    {
        Part = part;
        LocalPath = part.Artifact is not null && artifactStore is not null
            ? artifactStore.GetAbsolutePath(part.Artifact)
            : string.Empty;
    }

    public McpContentPart Part { get; }
    public string LocalPath { get; }
    public string Name => Part.Name ?? Part.Artifact?.DisplayName ?? "artifact";
    public string MimeType => Part.MimeType ?? Part.Artifact?.MimeType ?? "application/octet-stream";
    public string Uri => Part.Uri ?? Part.Artifact?.SourceUri ?? string.Empty;
    public string SizeLabel => Part.Artifact is null ? string.Empty : $"{Part.Artifact.ByteCount:N0} bytes";
    public bool IsImage => Part.Kind == McpContentKind.Image || MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

public sealed record ResourceAttachmentViewModel(McpResourceSnapshot Snapshot)
{
    public string Name => Snapshot.Name;
    public string ServerDisplayName => Snapshot.ServerDisplayName;
    public string Uri => Snapshot.Uri;
    public string? MimeType => Snapshot.MimeType;
    public string Content => Snapshot.Content;
    public string Status => string.Join(" / ", new[]
    {
        Snapshot.WasTruncated ? "切り詰めあり" : null,
        Snapshot.SkippedBinaryParts > 0 ? $"バイナリ{Snapshot.SkippedBinaryParts}件省略" : null,
        $"{Snapshot.OriginalByteCount:N0} bytes"
    }.Where(value => value is not null));
}

public sealed record PendingResourceViewModel(McpResourceReference Reference)
{
    public string Name => Reference.Name;
    public string ServerDisplayName => Reference.ServerDisplayName;
    public string ToolTip => $"{Reference.ServerDisplayName}\n{Reference.Uri}";
}

public sealed record ConversationItemViewModel(Guid Id, string Title, DateTimeOffset UpdatedAt)
{
    public string UpdatedLabel => UpdatedAt.ToLocalTime().ToString("MM/dd HH:mm");
}
