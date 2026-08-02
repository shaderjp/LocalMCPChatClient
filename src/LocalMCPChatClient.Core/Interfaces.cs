namespace LocalMCPChatClient.Core;

public interface IAppPaths
{
    string DataDirectory { get; }
    string SettingsPath { get; }
    string DatabasePath { get; }
    string ModelsDirectory { get; }
    string RuntimesDirectory { get; }
    string DownloadsDirectory { get; }
    string LogsDirectory { get; }
    void EnsureCreated();
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default);
    Task<AppSettings> ResetAsync(CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}

public interface IConversationStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken cancellationToken = default);
    Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Conversation> CreateAsync(string title, string? modelId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);
    Task<string?> DeleteLastTurnAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid conversationId, string title, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}

public interface IConversationExporter
{
    Task ExportMarkdownAsync(Guid conversationId, string destinationPath, CancellationToken cancellationToken = default);
}

public interface IInferenceService : IAsyncDisposable
{
    RuntimeState State { get; }
    Task StartAsync(InferenceProfile profile, ModelProfile model, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<InferenceUpdate> StreamCompletionAsync(InferenceRequest request, CancellationToken cancellationToken = default);
}

public interface IInferenceRuntimeManager : IAsyncDisposable
{
    RuntimeState State { get; }
    event EventHandler<RuntimeState>? StateChanged;
    Task RecoverOwnedProcessAsync(CancellationToken cancellationToken = default);
    Task<RuntimeState> StartAsync(InferenceProfile profile, ModelProfile model, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<HardwareCapabilities> DetectHardwareAsync(CancellationToken cancellationToken = default);
}

public interface IMcpConnectionManager : IAsyncDisposable
{
    event EventHandler<McpConnectionInfo>? ConnectionChanged;
    Task<McpConnectionInfo> ConnectAsync(McpServerProfile profile, CancellationToken cancellationToken = default);
    Task<McpConnectionInfo> TestAsync(McpServerProfile profile, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string serverId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpConnectionInfo>> GetConnectionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default);
    Task<McpToolResult> CallToolAsync(ToolCallRequest request, CancellationToken cancellationToken = default);
}

public interface IToolApprovalService
{
    ApprovalDecision Evaluate(string serverId, string toolName);
    Task RememberAsync(string serverId, string toolName, ApprovalDecision decision, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IToolApprovalPrompt
{
    Task<ApprovalResponse> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default);
}

public interface IArtifactInstaller
{
    Task<string> InstallAsync(ArtifactDescriptor artifact, IProgress<ArtifactProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<string> ImportAsync(ArtifactKind kind, string sourcePath, CancellationToken cancellationToken = default);
    bool IsInstalled(ArtifactDescriptor artifact);
}

public interface IAgentChatService
{
    IAsyncEnumerable<AgentEvent> RunTurnAsync(Guid conversationId, string text, InferenceProfile profile, ModelProfile model, CancellationToken cancellationToken = default);
}
