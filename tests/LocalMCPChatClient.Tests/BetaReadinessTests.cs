using System.Net;
using System.Text;
using LocalMCPChatClient.Core;
using LocalMCPChatClient.Infrastructure;

namespace LocalMCPChatClient.Tests;

public sealed class BetaReadinessTests
{
    [Fact]
    public async Task LookDev_pairing_persists_only_a_credential_reference()
    {
        using var paths = new TestPaths();
        var settings = new JsonSettingsStore(paths);
        var secrets = new BetaMemorySecretStore();
        var mcp = new BetaMcpManager();
        string? pairRequest = null;
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/.well-known/lookdevpt/v1")
                return Json(HttpStatusCode.OK, """
                    {"name":"D3D12LookDevPTWinUI","version":"0.2.0-beta.1","contractVersion":"1.0","endpoint":"http://127.0.0.1:8777/mcp"}
                    """);
            pairRequest = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """
                {"clientId":"client-1","token":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","contractVersion":"1.0"}
                """);
        });
        var service = new LookDevPairingService(new StubHttpClientFactory(handler), settings, secrets, mcp);

        var result = await service.PairAsync("http://127.0.0.1:8777", "12345678");

        Assert.Equal("0.2.0-beta.1", result.ApplicationVersion);
        Assert.Equal(McpConnectionState.Connected, result.Connection.State);
        Assert.Contains("12345678", pairRequest);
        var persisted = await File.ReadAllTextAsync(paths.SettingsPath);
        Assert.DoesNotContain("12345678", persisted);
        Assert.DoesNotContain(new string('a', 64), persisted);
        var header = Assert.Single((await settings.LoadAsync()).McpServers).Headers.Single();
        Assert.Null(header.Value);
        Assert.NotNull(header.SecretRef);
        Assert.Equal("Bearer " + new string('a', 64), secrets.Values[header.SecretRef!]);
    }

    [Fact]
    public async Task Diagnostic_report_excludes_tokens_endpoints_and_absolute_paths()
    {
        using var paths = new TestPaths();
        var settings = new JsonSettingsStore(paths);
        await settings.UpdateAsync(value => value with
        {
            SelectedModelId = value.Models[0].Id,
            CustomRuntimePath = @"C:\Users\alice\secret\llama-server.exe",
            Models = value.Models.Select((model, index) => index == 0
                ? model with { LocalPath = @"C:\Users\alice\secret\model.gguf", VisionProjectorPath = @"C:\Users\alice\secret\mmproj.gguf" }
                : model).ToList(),
            McpServers = [new McpServerProfile { Name = "D3D12 LookDev (paired)", Url = "http://127.0.0.1:8777/mcp" }]
        });
        var runtime = new DiagnosticRuntimeManager();
        var service = new DiagnosticReportService(paths, settings, runtime, new BetaMcpManager());

        var reportPath = await service.ExportAsync();
        var report = await File.ReadAllTextAsync(reportPath);

        Assert.Contains("0.2.0-beta.1", report);
        Assert.Contains("\"includesCredentials\": false", report);
        Assert.DoesNotContain("runtime-secret", report);
        Assert.DoesNotContain("127.0.0.1:8777", report);
        Assert.DoesNotContain(@"C:\\Users\\alice", report);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };
}

internal sealed class BetaMemorySecretStore : ISecretStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) { Values[key] = value; return Task.CompletedTask; }
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Values.GetValueOrDefault(key));
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { Values.Remove(key); return Task.CompletedTask; }
    public Task DeleteAllAsync(CancellationToken cancellationToken = default) { Values.Clear(); return Task.CompletedTask; }
}

internal sealed class BetaMcpManager : IMcpConnectionManager
{
    private McpServerProfile? _profile;
    public event EventHandler<McpConnectionInfo>? ConnectionChanged { add { } remove { } }
    public event EventHandler<McpResourceReference>? ResourceChanged { add { } remove { } }
    public Task<McpConnectionInfo> ConnectAsync(McpServerProfile profile, CancellationToken cancellationToken = default)
    {
        _profile = profile;
        return Task.FromResult(new McpConnectionInfo(profile.Id, profile.Name, McpConnectionState.Connected,
            ToolCount: 20, SupportsResources: true, SupportsPrompts: true, SupportsResourceTemplates: true,
            SupportsSubscriptions: true, LookDevContractVersion: "1.0"));
    }
    public Task<McpConnectionInfo> TestAsync(McpServerProfile profile, CancellationToken cancellationToken = default) => ConnectAsync(profile, cancellationToken);
    public Task DisconnectAsync(string serverId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<McpConnectionInfo>> GetConnectionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<McpConnectionInfo>>([new(_profile?.Id ?? "lookdev", "D3D12 LookDev", McpConnectionState.Connected,
            Error: @"http://127.0.0.1:8777/mcp C:\Users\alice\secret\server.log Authorization: Bearer diagnostic-secret",
            ToolCount: 20, SupportsResources: true, SupportsPrompts: true,
            SupportsResourceTemplates: true, SupportsSubscriptions: true, LookDevContractVersion: "1.0")]);
    public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolDefinition>>([]);
    public Task<IReadOnlyList<McpResourceCatalog>> GetResourceCatalogsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<McpResourceCatalog>>([]);
    public Task<IReadOnlyList<McpResourceTemplateDefinition>> GetResourceTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<McpResourceTemplateDefinition>>([]);
    public Task<IReadOnlyList<McpPromptDefinition>> GetPromptsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<McpPromptDefinition>>([]);
    public Task<McpPromptResult> GetPromptAsync(string serverId, string promptName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<McpResourceSnapshot> ReadResourceAsync(McpResourceReference reference, CancellationToken cancellationToken = default)
        => Task.FromResult(new McpResourceSnapshot(reference.ServerId, reference.ServerDisplayName, reference.Uri,
            reference.Name, reference.MimeType, reference.Uri.EndsWith("stats", StringComparison.Ordinal)
                ? "{\"api\":\"Direct3D 12 DXR\",\"adapter\":\"Test GPU\",\"dxrTier\":\"1.1\"}"
                : "{\"application\":{\"version\":\"0.2.0-beta.1\"},\"contractVersion\":\"1.0\"}",
            DateTimeOffset.UtcNow, 88));
    public Task<IAsyncDisposable> SubscribeToResourceAsync(McpResourceReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<McpToolResult> CallToolAsync(ToolCallRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class DiagnosticRuntimeManager : IInferenceRuntimeManager
{
    public RuntimeState State { get; } = new(RuntimeStatus.Ready, RuntimeBackend.Cuda,
        new Uri("http://127.0.0.1:12345"), @"C:\Users\alice\secret\model.gguf",
        @"http://127.0.0.1:12345 C:\Users\alice\secret\llama.log Authorization: Bearer runtime-secret", "runtime-secret", true);
    public event EventHandler<RuntimeState>? StateChanged { add { } remove { } }
    public Task RecoverOwnedProcessAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<RuntimeState> StartAsync(InferenceProfile profile, ModelProfile model, CancellationToken cancellationToken = default) => Task.FromResult(State);
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<HardwareCapabilities> DetectHardwareAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new HardwareCapabilities(true, true, "GPU", "CPU", 8, 16,
            [new GpuCapability("Test GPU", "NVIDIA", 8L * 1024 * 1024 * 1024, "1.2.3", true, true)], RuntimeBackend.Cuda));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
