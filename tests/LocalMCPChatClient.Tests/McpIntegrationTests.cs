using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LocalMCPChatClient.Core;
using LocalMCPChatClient.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalMCPChatClient.Tests;

public sealed class McpIntegrationTests
{
    [Fact]
    public async Task Stdio_connects_lists_calls_times_out_and_disconnects()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var serverPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "LocalMCPChatClient.TestServer", "bin", configuration, "net9.0", "LocalMCPChatClient.TestServer.exe"));
        Assert.True(File.Exists(serverPath), $"Test MCP server was not built: {serverPath}");

        await using var manager = new McpConnectionManager(
            new MemorySecretStore(), NullLoggerFactory.Instance, NullLogger<McpConnectionManager>.Instance);
        var profile = new McpServerProfile
        {
            Id = "test-server",
            Name = "Test MCP",
            Transport = McpTransportKind.Stdio,
            Command = serverPath,
            TimeoutSeconds = 1
        };

        var connection = await manager.ConnectAsync(profile);
        Assert.Equal(McpConnectionState.Connected, connection.State);
        Assert.Equal(3, connection.ToolCount);
        Assert.True(connection.SupportsResources);

        var tools = await manager.GetToolsAsync();
        var echo = Assert.Single(tools, tool => tool.OriginalName == "echo");
        Assert.Equal("Test MCP", echo.ServerDisplayName);
        var result = await manager.CallToolAsync(new ToolCallRequest("call-1", echo.NamespacedName, "{\"text\":\"hello\"}"));
        Assert.False(result.IsError);
        Assert.Contains("echo:hello", result.Content);

        var wait = Assert.Single(tools, tool => tool.OriginalName == "wait");
        var timeout = await manager.CallToolAsync(new ToolCallRequest("call-2", wait.NamespacedName, "{\"milliseconds\":10000}"));
        Assert.True(timeout.IsError);
        Assert.Contains("タイムアウト", timeout.Content);

        var catalog = Assert.Single(await manager.GetResourceCatalogsAsync());
        Assert.Null(catalog.Error);
        Assert.Equal(7, catalog.Resources.Count);
        var welcome = Assert.Single(catalog.Resources, resource => resource.Uri == "test://documents/welcome");
        var welcomeSnapshot = await manager.ReadResourceAsync(McpResourceReference.FromDefinition(welcome));
        Assert.Contains("こんにちは", welcomeSnapshot.Content);
        Assert.False(welcomeSnapshot.WasTruncated);

        var multipart = Assert.Single(catalog.Resources, resource => resource.Uri == "test://documents/multipart");
        var multipartSnapshot = await manager.ReadResourceAsync(McpResourceReference.FromDefinition(multipart));
        Assert.Contains("part-one", multipartSnapshot.Content);
        Assert.Contains("part-two", multipartSnapshot.Content);

        var mixed = Assert.Single(catalog.Resources, resource => resource.Uri == "test://documents/mixed");
        var mixedSnapshot = await manager.ReadResourceAsync(McpResourceReference.FromDefinition(mixed));
        Assert.Equal("visible-text", mixedSnapshot.Content);
        Assert.Equal(1, mixedSnapshot.SkippedBinaryParts);

        var blob = Assert.Single(catalog.Resources, resource => resource.Uri == "test://documents/blob");
        await Assert.ThrowsAsync<NotSupportedException>(() => manager.ReadResourceAsync(McpResourceReference.FromDefinition(blob)));

        using (var paths = new TestPaths())
        {
            paths.EnsureCreated();
            var artifacts = new ArtifactStore(paths);
            await using var artifactManager = new McpConnectionManager(
                new MemorySecretStore(), NullLoggerFactory.Instance, NullLogger<McpConnectionManager>.Instance, artifacts);
            var artifactProfile = profile with { Id = "artifact-test-server" };
            await artifactManager.ConnectAsync(artifactProfile);
            var artifactCatalog = Assert.Single(await artifactManager.GetResourceCatalogsAsync());
            var pixel = Assert.Single(artifactCatalog.Resources, resource => resource.Uri == "test://documents/pixel.png");
            var pixelSnapshot = await artifactManager.ReadResourceAsync(McpResourceReference.FromDefinition(pixel));
            var imagePart = Assert.Single(pixelSnapshot.Parts!, part => part.Kind == McpContentKind.Image);
            Assert.NotNull(imagePart.Artifact);
            Assert.True(File.Exists(artifacts.GetAbsolutePath(imagePart.Artifact!)));

            var imageTool = Assert.Single(await artifactManager.GetToolsAsync(), tool => tool.OriginalName == "image");
            var imageResult = await artifactManager.CallToolAsync(new ToolCallRequest("image-call", imageTool.NamespacedName, "{}"));
            Assert.Contains(imageResult.Parts!, part => part.Kind == McpContentKind.Image && part.Artifact is not null);

            var templates = await artifactManager.GetResourceTemplatesAsync();
            Assert.Contains(templates, template => template.UriTemplate == "test://templates/{name}");
            var prompts = await artifactManager.GetPromptsAsync();
            var prompt = Assert.Single(prompts, prompt => prompt.Name == "review");
            var expanded = await artifactManager.GetPromptAsync(prompt.ServerId, prompt.Name,
                new Dictionary<string, object?> { ["subject"] = "viewport" });
            Assert.Contains(expanded.Parts, part => part.Text?.Contains("Review this: viewport", StringComparison.Ordinal) == true);
        }

        var large = Assert.Single(catalog.Resources, resource => resource.Uri == "test://documents/large");
        var largeSnapshot = await manager.ReadResourceAsync(McpResourceReference.FromDefinition(large));
        Assert.True(largeSnapshot.WasTruncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(largeSnapshot.Content) <= 256 * 1024);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReadResourceAsync(
            new McpResourceReference(profile.Id, profile.Name, "test://documents/missing", "missing")));
        var slow = Assert.Single(catalog.Resources, resource => resource.Uri == "test://documents/slow");
        await Assert.ThrowsAsync<TimeoutException>(() => manager.ReadResourceAsync(McpResourceReference.FromDefinition(slow)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ReadResourceAsync(
            McpResourceReference.FromDefinition(slow), cancellation.Token));

        await manager.DisconnectAsync(profile.Id);
        Assert.Empty(await manager.GetConnectionsAsync());
        Assert.Empty(await manager.GetToolsAsync());
    }

    [Fact]
    public async Task Remote_plain_http_is_rejected_before_network_access()
    {
        await using var manager = new McpConnectionManager(
            new MemorySecretStore(), NullLoggerFactory.Instance, NullLogger<McpConnectionManager>.Instance);
        var result = await manager.TestAsync(new McpServerProfile
        {
            Id = "unsafe",
            Name = "Unsafe",
            Transport = McpTransportKind.StreamableHttp,
            Url = "http://example.com/mcp"
        });

        Assert.Equal(McpConnectionState.Faulted, result.State);
        Assert.Contains("HTTPS", result.Error);
    }

    [Fact]
    public async Task Missing_bearer_token_environment_variable_is_reported_without_exposing_a_secret()
    {
        var variableName = "LOCAL_MCP_CHAT_MISSING_" + Guid.NewGuid().ToString("N");
        await using var manager = new McpConnectionManager(
            new MemorySecretStore(), NullLoggerFactory.Instance, NullLogger<McpConnectionManager>.Instance);

        var result = await manager.TestAsync(new McpServerProfile
        {
            Id = "missing-bearer-token",
            Name = "Missing bearer token",
            Transport = McpTransportKind.StreamableHttp,
            Url = "http://127.0.0.1:1/mcp",
            BearerTokenEnvironmentVariable = variableName,
            StartupTimeoutSeconds = 1
        });

        Assert.Equal(McpConnectionState.Faulted, result.State);
        Assert.Contains(variableName, result.Error);
        Assert.Contains("環境変数", result.Error);
    }

    [Fact]
    public async Task Empty_http_protocol_version_is_reported_before_network_access()
    {
        await using var manager = new McpConnectionManager(
            new MemorySecretStore(), NullLoggerFactory.Instance, NullLogger<McpConnectionManager>.Instance);

        var result = await manager.TestAsync(new McpServerProfile
        {
            Id = "empty-protocol-version",
            Name = "Empty protocol version",
            Transport = McpTransportKind.StreamableHttp,
            Url = "http://127.0.0.1:1/mcp",
            Headers = [new SecretValue("MCP-Protocol-Version", "   ")],
            StartupTimeoutSeconds = 1
        });

        Assert.Equal(McpConnectionState.Faulted, result.State);
        Assert.Contains("MCP-Protocol-Version", result.Error);
        Assert.Contains("空でない", result.Error);
    }

    [Fact]
    public async Task Streamable_http_connects_lists_and_calls_on_loopback()
    {
        var serverPath = GetServerPath();
        var port = GetFreeTcpPort();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = serverPath,
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithArguments("--http", "--port", port.ToString(), "--require-content-length", "--require-stateless-discover"));
        Assert.NotNull(process);
        try
        {
            await WaitForPortAsync(port, process!);
            await using var manager = new McpConnectionManager(
                new MemorySecretStore(), NullLoggerFactory.Instance, NullLogger<McpConnectionManager>.Instance);
            var connection = await manager.ConnectAsync(new McpServerProfile
            {
                Id = "http-test",
                Name = "HTTP Test MCP",
                Transport = McpTransportKind.StreamableHttp,
                Url = $"http://127.0.0.1:{port}/mcp",
                BufferHttpRequestBody = true,
                TimeoutSeconds = 10
            });

            Assert.Equal(McpConnectionState.Connected, connection.State);
            Assert.True(connection.SupportsResources);
            var echo = Assert.Single(await manager.GetToolsAsync(), tool => tool.OriginalName == "echo");
            var result = await manager.CallToolAsync(new ToolCallRequest("http-call", echo.NamespacedName, "{\"text\":\"http\"}"));
            Assert.False(result.IsError);
            var catalog = Assert.Single(await manager.GetResourceCatalogsAsync());
            var welcome = Assert.Single(catalog.Resources, resource => resource.Uri == "test://documents/welcome");
            var snapshot = await manager.ReadResourceAsync(McpResourceReference.FromDefinition(welcome));
            Assert.Contains("こんにちは", snapshot.Content);
            Assert.Contains("echo:http", result.Content);
        }
        finally
        {
            if (!process!.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Streamable_http_pins_legacy_protocol_without_duplicate_header()
    {
        var serverPath = GetServerPath();
        var port = GetFreeTcpPort();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = serverPath,
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithArguments("--http", "--port", port.ToString(), "--stateful", "--reject-duplicate-protocol-version"));
        Assert.NotNull(process);
        try
        {
            await WaitForPortAsync(port, process!);
            await using var manager = new McpConnectionManager(
                new MemorySecretStore(), NullLoggerFactory.Instance, NullLogger<McpConnectionManager>.Instance);
            var connection = await manager.ConnectAsync(new McpServerProfile
            {
                Id = "legacy-http-test",
                Name = "Legacy HTTP Test MCP",
                Transport = McpTransportKind.StreamableHttp,
                Url = $"http://127.0.0.1:{port}/mcp",
                Headers =
                [
                    new SecretValue("mcp-protocol-version", "unsupported-first-value"),
                    new SecretValue("MCP-Protocol-Version", "2025-11-25")
                ],
                TimeoutSeconds = 10
            });

            Assert.Equal(McpConnectionState.Connected, connection.State);
            Assert.True(connection.SupportsResources);
            var echo = Assert.Single(await manager.GetToolsAsync(), tool => tool.OriginalName == "echo");
            var result = await manager.CallToolAsync(new ToolCallRequest("legacy-http-call", echo.NamespacedName, "{\"text\":\"legacy\"}"));
            Assert.False(result.IsError);
            Assert.Contains("echo:legacy", result.Content);
        }
        finally
        {
            if (!process!.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task External_d3d12lookdevpt_connects_lists_and_calls_with_negotiated_protocol()
    {
        var endpoint = Environment.GetEnvironmentVariable("LOCAL_MCP_CHAT_EXTERNAL_MCP_URL");
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        const string tokenVariable = "LOCAL_MCP_CHAT_EXTERNAL_MCP_TOKEN";
        var token = Environment.GetEnvironmentVariable(tokenVariable);

        using var externalPaths = new TestPaths();
        externalPaths.EnsureCreated();
        var externalArtifacts = new ArtifactStore(externalPaths);
        await using var manager = new McpConnectionManager(
            new MemorySecretStore(), NullLoggerFactory.Instance, NullLogger<McpConnectionManager>.Instance, externalArtifacts);
        var connection = await manager.ConnectAsync(new McpServerProfile
        {
            Id = "external-d3d12lookdevpt",
            Name = "D3D12LookDevPT",
            Transport = McpTransportKind.StreamableHttp,
            Url = endpoint,
            BearerTokenEnvironmentVariable = string.IsNullOrWhiteSpace(token) ? null : tokenVariable,
            EnableStandaloneGetStream = false,
            StartupTimeoutSeconds = 10,
            TimeoutSeconds = 30
        });

        Assert.True(connection.State == McpConnectionState.Connected, connection.Error);
        Assert.Equal("1.0", connection.LookDevContractVersion);
        var stateTool = Assert.Single(await manager.GetToolsAsync(), tool => tool.OriginalName == "lookdevpt.get_state");
        var result = await manager.CallToolAsync(new ToolCallRequest("external-state", stateTool.NamespacedName, "{}"));
        Assert.False(result.IsError, result.Content);
        Assert.Contains("\"ok\":true", result.Content);

        var captureTool = Assert.Single(await manager.GetToolsAsync(), tool => tool.OriginalName == "lookdevpt.capture_viewport");
        var capture = await manager.CallToolAsync(new ToolCallRequest("external-capture", captureTool.NamespacedName, "{}"));
        Assert.False(capture.IsError, capture.Content);
        Assert.Contains(capture.Parts!, part => part.Kind == McpContentKind.Image && part.Artifact is not null);
        Assert.Contains(capture.Parts!, part => part.Kind == McpContentKind.ResourceLink && part.Uri == "lookdevpt://captures/1.png");

        var startReviewTool = Assert.Single(await manager.GetToolsAsync(), tool => tool.OriginalName == "lookdevpt.start_review");
        var started = await manager.CallToolAsync(new ToolCallRequest("external-review-start", startReviewTool.NamespacedName, "{\"preset\":\"quick\"}"));
        Assert.False(started.IsError, started.Content);
        Assert.Contains("\"state\":\"running\"", started.Content);
        var getReviewTool = Assert.Single(await manager.GetToolsAsync(), tool => tool.OriginalName == "lookdevpt.get_review");
        var completed = await manager.CallToolAsync(new ToolCallRequest("external-review-get", getReviewTool.NamespacedName, "{\"reviewId\":1}"));
        Assert.False(completed.IsError, completed.Content);
        Assert.Contains("\"state\":\"completed\"", completed.Content);

        var heatmap = await manager.ReadResourceAsync(new McpResourceReference(
            "external-d3d12lookdevpt", "D3D12LookDevPT", "lookdevpt://comparisons/1/heatmap.png", "comparison heatmap", "image/png"));
        var heatmapImage = Assert.Single(heatmap.Parts!, part => part.Kind == McpContentKind.Image);
        Assert.NotNull(heatmapImage.Artifact);
        Assert.True(File.Exists(externalArtifacts.GetAbsolutePath(heatmapImage.Artifact!)));
    }

    private static string GetServerPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "LocalMCPChatClient.TestServer", "bin", configuration, "net9.0", "LocalMCPChatClient.TestServer.exe"));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task WaitForPortAsync(int port, Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited) throw new InvalidOperationException($"HTTP test server exited with code {process.ExitCode}.");
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return;
            }
            catch (SocketException) { await Task.Delay(100, timeout.Token); }
        }
        throw new TimeoutException("HTTP test server did not start.");
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo info, params string[] arguments)
    {
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
}

internal sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_values.GetValueOrDefault(key));
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }
    public Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values.Clear();
        return Task.CompletedTask;
    }
}
