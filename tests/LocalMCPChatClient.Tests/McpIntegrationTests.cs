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
            TimeoutSeconds = 5
        };

        var connection = await manager.ConnectAsync(profile);
        Assert.Equal(McpConnectionState.Connected, connection.State);
        Assert.Equal(2, connection.ToolCount);

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
    public async Task Streamable_http_connects_lists_and_calls_on_loopback()
    {
        var serverPath = GetServerPath();
        var port = GetFreeTcpPort();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = serverPath,
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithArguments("--http", "--port", port.ToString(), "--require-content-length"));
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
            var echo = Assert.Single(await manager.GetToolsAsync(), tool => tool.OriginalName == "echo");
            var result = await manager.CallToolAsync(new ToolCallRequest("http-call", echo.NamespacedName, "{\"text\":\"http\"}"));
            Assert.False(result.IsError);
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
