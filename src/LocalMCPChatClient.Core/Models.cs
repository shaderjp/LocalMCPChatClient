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
public enum AgentEventKind { UserMessageStored, TextDelta, ToolApprovalRequired, ToolStarted, ToolCompleted, Warning, Completed }
public enum McpContentKind { Text, StructuredJson, Image, Blob, ResourceLink, EmbeddedResource }
public enum InputModality { Text, Image }

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
    bool IsError = false,
    IReadOnlyList<McpResourceSnapshot>? ResourceSnapshots = null,
    IReadOnlyList<McpContentPart>? ContentParts = null);

public sealed record StoredArtifact(
    string Id,
    string Sha256,
    string MimeType,
    string RelativePath,
    string DisplayName,
    long ByteCount,
    int? Width = null,
    int? Height = null,
    string? SourceUri = null);

public sealed record McpContentPart(
    McpContentKind Kind,
    string? Text = null,
    string? MimeType = null,
    string? Uri = null,
    string? Name = null,
    StoredArtifact? Artifact = null);

public sealed record McpResourceDefinition(
    string ServerId,
    string ServerDisplayName,
    string Uri,
    string Name,
    string? Description = null,
    string? MimeType = null,
    long? Size = null);

public sealed record McpResourceReference(
    string ServerId,
    string ServerDisplayName,
    string Uri,
    string Name,
    string? MimeType = null)
{
    public static McpResourceReference FromDefinition(McpResourceDefinition definition) => new(
        definition.ServerId,
        definition.ServerDisplayName,
        definition.Uri,
        definition.Name,
        definition.MimeType);
}

public sealed record McpResourceSnapshot(
    string ServerId,
    string ServerDisplayName,
    string Uri,
    string Name,
    string? MimeType,
    string Content,
    DateTimeOffset ReadAt,
    int OriginalByteCount,
    bool WasTruncated = false,
    int SkippedBinaryParts = 0,
    IReadOnlyList<McpContentPart>? Parts = null);

public sealed record McpResourceCatalog(
    string ServerId,
    string ServerDisplayName,
    IReadOnlyList<McpResourceDefinition> Resources,
    string? Error = null);

public sealed record UserTurnInput
{
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<McpResourceReference> ResourceReferences { get; init; } = [];
    public IReadOnlyList<McpResourceSnapshot> ResourceSnapshots { get; init; } = [];
}

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
    bool WasTruncated = false,
    IReadOnlyList<McpContentPart>? Parts = null);

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
    int MaxTokens = 2048,
    int ImageTokenBudget = 280);

public sealed record RuntimeState(
    RuntimeStatus Status,
    RuntimeBackend? Backend = null,
    Uri? Endpoint = null,
    string? ModelPath = null,
    string? Error = null,
    string? AuthenticationToken = null,
    bool VisionEnabled = false)
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
    public string LicenseId { get; init; } = string.Empty;
    public string LicenseName { get; init; } = string.Empty;
    public string LicenseUrl { get; init; } = string.Empty;
    public string? LocalPath { get; init; }
    public List<InputModality> Modalities { get; init; } = [InputModality.Text];
    public ArtifactDescriptor? VisionProjector { get; init; }
    public string? VisionProjectorPath { get; init; }
}

public sealed record ModelLicenseAcceptance(
    string ModelId,
    string ModelRevision,
    string LicenseId,
    DateTimeOffset AcceptedAtUtc);

public sealed record InferenceProfile
{
    public string ModelId { get; init; } = string.Empty;
    public InferenceMode Mode { get; init; } = InferenceMode.Auto;
    public int ContextSize { get; init; } = 8192;
    public int MaxOutputTokens { get; init; } = 2048;
    public double Temperature { get; init; } = 0.7;
    public string? CustomRuntimePath { get; init; }
    public bool EnableVision { get; init; } = true;
    public int ImageTokenBudget { get; init; } = 280;
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

public sealed record ImportedMcpServer(
    McpServerProfile Profile,
    IReadOnlyDictionary<string, string> SecretEnvironment,
    IReadOnlyDictionary<string, string> SecretHeaders);

public sealed record McpProfileImportResult(
    IReadOnlyList<ImportedMcpServer> Servers,
    IReadOnlyList<string> Warnings)
{
    public int SecretCount => Servers.Sum(server => server.SecretEnvironment.Count + server.SecretHeaders.Count);
}

public sealed record McpConnectionInfo(
    string ServerId,
    string DisplayName,
    McpConnectionState State,
    string? Error = null,
    int ToolCount = 0,
    bool SupportsResources = false,
    bool SupportsPrompts = false,
    bool SupportsResourceTemplates = false,
    bool SupportsSubscriptions = false,
    string? LookDevContractVersion = null);

public sealed record LookDevPairingResult(
    McpServerProfile Profile,
    string ApplicationVersion,
    string ContractVersion,
    McpConnectionInfo Connection);

public sealed record McpPromptDefinition(
    string ServerId,
    string ServerDisplayName,
    string Name,
    string? Title,
    string? Description,
    IReadOnlyList<McpPromptArgument> Arguments);

public sealed record McpPromptArgument(string Name, string? Description, bool Required = false);

public sealed record McpPromptResult(
    string ServerId,
    string ServerDisplayName,
    string Name,
    string? Description,
    IReadOnlyList<McpContentPart> Parts);

public sealed record McpResourceTemplateDefinition(
    string ServerId,
    string ServerDisplayName,
    string UriTemplate,
    string Name,
    string? Description,
    string? MimeType);

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
    TurnPerformance? Performance = null,
    IReadOnlyList<McpResourceSnapshot>? ResourceSnapshots = null);

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
    public bool EnableVision { get; init; } = true;
    public int ImageTokenBudget { get; init; } = 280;
    public List<InferenceBenchmarkResult> InferenceBenchmarks { get; init; } = [];
    public List<ModelLicenseAcceptance> AcceptedModelLicenses { get; init; } = [];
    public List<ModelProfile> Models { get; init; } = [];
    public List<McpServerProfile> McpServers { get; init; } = [];
    public List<ToolApprovalRule> ApprovalRules { get; init; } = [];
}
