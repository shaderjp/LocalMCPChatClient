using System.Text.Json;

namespace LocalMCPChatClient.Core;

public enum ChatRole { System, User, Assistant, Tool }
public enum InferenceMode { Auto, Cpu, Cuda, Vulkan }
public enum RuntimeBackend { Cpu, Cuda, Vulkan }
public enum RuntimeStatus { Stopped, Starting, WarmingUp, Ready, Faulted }
public enum McpTransportKind { Stdio, StreamableHttp }
public enum McpConnectionState { Disconnected, Connecting, Connected, Faulted }
public enum ApprovalDecision { Ask, Allow, Deny }
public enum ApprovalResponse { AllowOnce, AlwaysAllow, Deny }
public enum ArtifactKind { Runtime, Model }
public enum AgentEventKind { TextDelta, ToolApprovalRequired, ToolStarted, ToolCompleted, Warning, Completed }

public sealed record Conversation(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ModelId = null);

public sealed record ChatMessage(
    Guid Id,
    Guid ConversationId,
    ChatRole Role,
    string Content,
    DateTimeOffset CreatedAt,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ToolCallsJson = null,
    bool IsError = false);

public sealed record ToolDefinition(
    string NamespacedName,
    string ServerId,
    string OriginalName,
    string Description,
    JsonElement InputSchema,
    string? ServerDisplayName = null);

public sealed record ToolCallRequest(string Id, string Name, string ArgumentsJson);

public sealed record McpToolResult(
    string ToolCallId,
    string ToolName,
    string Content,
    bool IsError = false,
    bool WasTruncated = false);

public sealed record InferenceUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed record InferenceTiming(
    double RequestMilliseconds,
    double TimeToFirstTokenMilliseconds,
    double PromptTokensPerSecond,
    double GeneratedTokensPerSecond);

public sealed record InferenceUpdate(
    string? TextDelta = null,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null,
    bool IsCompleted = false,
    InferenceUsage? Usage = null,
    InferenceTiming? Timing = null);

public sealed record InferenceRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    double Temperature = 0.7,
    int MaxTokens = 2048);

public sealed record RuntimeState(
    RuntimeStatus Status,
    RuntimeBackend? Backend = null,
    Uri? Endpoint = null,
    string? ModelPath = null,
    string? Error = null,
    string? AuthenticationToken = null)
{
    public static RuntimeState Stopped { get; } = new(RuntimeStatus.Stopped);
}

public sealed record ModelProfile
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Repository { get; init; } = string.Empty;
    public string Revision { get; init; } = "main";
    public string FileName { get; init; } = string.Empty;
    public string? Sha256 { get; init; }
    public long? Size { get; init; }
    public string LicenseUrl { get; init; } = string.Empty;
    public string? LocalPath { get; init; }
}

public sealed record InferenceProfile
{
    public string ModelId { get; init; } = string.Empty;
    public InferenceMode Mode { get; init; } = InferenceMode.Auto;
    public int ContextSize { get; init; } = 8192;
    public int MaxOutputTokens { get; init; } = 2048;
    public double Temperature { get; init; } = 0.7;
    public string? CustomRuntimePath { get; init; }
}

public sealed record SecretValue(string Name, string? Value = null, string? SecretRef = null);

public sealed record McpServerProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "MCP Server";
    public bool Enabled { get; init; } = true;
    public McpTransportKind Transport { get; init; } = McpTransportKind.Stdio;
    public string? Command { get; init; }
    public List<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public List<SecretValue> Environment { get; init; } = [];
    public string? Url { get; init; }
    public List<SecretValue> Headers { get; init; } = [];
    public string? BearerTokenEnvironmentVariable { get; init; }
    public bool EnableStandaloneGetStream { get; init; } = true;
    public bool BufferHttpRequestBody { get; init; }
    public int StartupTimeoutSeconds { get; init; } = 10;
    public int TimeoutSeconds { get; init; } = 60;
}

public sealed record McpConnectionInfo(
    string ServerId,
    string DisplayName,
    McpConnectionState State,
    string? Error = null,
    int ToolCount = 0);

public sealed record ToolApprovalRule(string ServerId, string ToolName, ApprovalDecision Decision);

public sealed record ToolApprovalRequest(
    string ServerId,
    string ServerName,
    string ToolName,
    string ArgumentsJson);

public sealed record ArtifactDescriptor
{
    public string Id { get; init; } = string.Empty;
    public ArtifactKind Kind { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public Uri DownloadUri { get; init; } = new("https://localhost/");
    public string FileName { get; init; } = string.Empty;
    public string? Sha256 { get; init; }
    public long? Size { get; init; }
    public string? ArchiveEntry { get; init; }
    public RuntimeBackend? Backend { get; init; }
    public string? InstallationGroup { get; init; }
    public bool IsRuntimeDependency { get; init; }
    public string? DestinationDirectory { get; init; }
}

public sealed record ArtifactProgress(long BytesReceived, long? TotalBytes, string Stage);

public sealed record GpuCapability(
    string Name,
    string Vendor,
    long DedicatedMemoryBytes,
    string? DriverVersion,
    bool SupportsCuda,
    bool SupportsVulkan);

public sealed record HardwareCapabilities(
    bool HasNvidiaGpu,
    bool HasVulkanGpu,
    string Summary,
    string CpuName = "",
    int PhysicalCoreCount = 1,
    int LogicalProcessorCount = 1,
    IReadOnlyList<GpuCapability>? Gpus = null,
    RuntimeBackend RecommendedBackend = RuntimeBackend.Cpu);

public sealed record BenchmarkProgress(string Stage, RuntimeBackend? Backend = null, double Percent = 0);

public sealed record InferenceBenchmarkResult
{
    public string Fingerprint { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public RuntimeBackend Backend { get; init; }
    public string RuntimeBuild { get; init; } = string.Empty;
    public double PromptTokensPerSecond { get; init; }
    public double GeneratedTokensPerSecond { get; init; }
    public DateTimeOffset MeasuredAt { get; init; }
}

public sealed record TurnPerformance(
    double PreparationMilliseconds,
    double TimeToFirstTokenMilliseconds,
    double TotalMilliseconds,
    int PromptTokens,
    int CompletionTokens,
    double GeneratedTokensPerSecond,
    RuntimeBackend? Backend);

public sealed record AgentEvent(
    AgentEventKind Kind,
    string? Text = null,
    ToolCallRequest? ToolCall = null,
    McpToolResult? ToolResult = null,
    TurnPerformance? Performance = null);

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 2;
    public bool SetupCompleted { get; init; }
    public string? SelectedModelId { get; init; }
    public InferenceMode InferenceMode { get; init; } = InferenceMode.Auto;
    public RuntimeBackend? LastSuccessfulAutoBackend { get; init; }
    public int ContextSize { get; init; } = 8192;
    public int MaxOutputTokens { get; init; } = 2048;
    public double Temperature { get; init; } = 0.7;
    public string? ModelDirectory { get; init; }
    public string? CustomRuntimePath { get; init; }
    public bool PreloadModel { get; init; } = true;
    public List<InferenceBenchmarkResult> InferenceBenchmarks { get; init; } = [];
    public List<ModelProfile> Models { get; init; } = [];
    public List<McpServerProfile> McpServers { get; init; } = [];
    public List<ToolApprovalRule> ApprovalRules { get; init; } = [];
}
