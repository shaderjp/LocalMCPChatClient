using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalMCPChatClient.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LocalMCPChatClient.Infrastructure;

public sealed class McpConnectionManager(
    ISecretStore secretStore,
    ILoggerFactory loggerFactory,
    ILogger<McpConnectionManager> logger) : IMcpConnectionManager
{
    private const string ProtocolVersionHeaderName = "MCP-Protocol-Version";
    private const int MaxToolResultCharacters = 256 * 1024;
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RegisteredTool> _tools = new(StringComparer.Ordinal);
    public event EventHandler<McpConnectionInfo>? ConnectionChanged;

    public async Task<McpConnectionInfo> ConnectAsync(McpServerProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await DisconnectAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        Publish(new McpConnectionInfo(profile.Id, profile.Name, McpConnectionState.Connecting));
        try
        {
            var connection = await CreateConnectionAsync(profile, cancellationToken).ConfigureAwait(false);
            _connections[profile.Id] = connection;
            RegisterTools(connection);
            var info = new McpConnectionInfo(profile.Id, profile.Name, McpConnectionState.Connected, ToolCount: connection.Tools.Count);
            Publish(info);
            return info;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var info = new McpConnectionInfo(profile.Id, profile.Name, McpConnectionState.Faulted, "MCPサーバーへの接続がタイムアウトしました。");
            Publish(info);
            return info;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to connect to MCP server {ServerId}", profile.Id);
            var info = new McpConnectionInfo(profile.Id, profile.Name, McpConnectionState.Faulted, exception.Message);
            Publish(info);
            return info;
        }
    }

    public async Task<McpConnectionInfo> TestAsync(McpServerProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await CreateConnectionAsync(profile, cancellationToken).ConfigureAwait(false);
            return new McpConnectionInfo(profile.Id, profile.Name, McpConnectionState.Connected, ToolCount: connection.Tools.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new McpConnectionInfo(profile.Id, profile.Name, McpConnectionState.Faulted, "MCPサーバーへの接続がタイムアウトしました。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new McpConnectionInfo(profile.Id, profile.Name, McpConnectionState.Faulted, exception.Message);
        }
    }

    public async Task DisconnectAsync(string serverId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connections.TryRemove(serverId, out var connection))
        {
            foreach (var item in _tools.Where(item => item.Value.Connection.Profile.Id == serverId).ToArray())
                _tools.TryRemove(item.Key, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
            Publish(new McpConnectionInfo(serverId, connection.Profile.Name, McpConnectionState.Disconnected));
        }
    }

    public Task<IReadOnlyList<McpConnectionInfo>> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<McpConnectionInfo> result = _connections.Values
            .Select(connection => new McpConnectionInfo(connection.Profile.Id, connection.Profile.Name, McpConnectionState.Connected, ToolCount: connection.Tools.Count))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ToolDefinition> result = _tools.Values
            .Select(item => item.Definition)
            .OrderBy(item => item.NamespacedName, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    public async Task<McpToolResult> CallToolAsync(ToolCallRequest request, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(request.Name, out var registered))
            return new McpToolResult(request.Id, request.Name, $"未知または未接続のツールです: {request.Name}", true);

        IReadOnlyDictionary<string, object?> arguments;
        try
        {
            using var document = JsonDocument.Parse(request.ArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("ツール引数はJSONオブジェクトである必要があります。");
            arguments = document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            return new McpToolResult(request.Id, request.Name, $"ツール引数を解析できません: {exception.Message}", true);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(registered.Connection.Profile.TimeoutSeconds, 1, 3600)));
        try
        {
            var result = await registered.Tool.CallAsync(arguments, cancellationToken: timeout.Token).ConfigureAwait(false);
            var content = FormatResult(result);
            var truncated = content.Length > MaxToolResultCharacters;
            if (truncated) content = content[..MaxToolResultCharacters] + "\n…(結果を256 KiBで切り詰めました)";
            return new McpToolResult(request.Id, request.Name, content, result.IsError == true, truncated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new McpToolResult(request.Id, request.Name, "MCPツールがタイムアウトしました。", true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "MCP tool {ToolName} failed", request.Name);
            return new McpToolResult(request.Id, request.Name, $"MCPツールの実行に失敗しました: {exception.Message}", true);
        }
    }

    private async Task<Connection> CreateConnectionAsync(McpServerProfile profile, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(profile.StartupTimeoutSeconds, 1, 3600)));
        var protocolVersion = profile.Transport == McpTransportKind.StreamableHttp
            ? await ResolveHttpProtocolVersionAsync(profile, timeout.Token).ConfigureAwait(false)
            : null;
        IClientTransport transport = profile.Transport switch
        {
            McpTransportKind.Stdio => await CreateStdioTransportAsync(profile, timeout.Token).ConfigureAwait(false),
            McpTransportKind.StreamableHttp => await CreateHttpTransportAsync(profile, timeout.Token).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(profile.Transport))
        };

        try
        {
            var clientOptions = protocolVersion is null
                ? null
                : new McpClientOptions { ProtocolVersion = protocolVersion };
            var client = await McpClient.CreateAsync(
                transport,
                clientOptions: clientOptions,
                loggerFactory: loggerFactory,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            return new Connection(profile, client, tools);
        }
        catch
        {
            if (transport is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (transport is IDisposable disposable) disposable.Dispose();
            throw;
        }
    }

    private async Task<IClientTransport> CreateStdioTransportAsync(McpServerProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.Command)) throw new InvalidOperationException("stdio接続にはコマンドが必要です。");
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var item in profile.Environment)
            environment[item.Name] = await ResolveValueAsync(item, cancellationToken).ConfigureAwait(false);
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = profile.Name,
            Command = profile.Command,
            Arguments = profile.Arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory) ? null : profile.WorkingDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = line => logger.LogDebug("MCP {ServerId}: {Line}", profile.Id, line)
        }, loggerFactory);
    }

    private async Task<IClientTransport> CreateHttpTransportAsync(McpServerProfile profile, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(profile.Url, UriKind.Absolute, out var endpoint)) throw new InvalidOperationException("有効なMCP URLを入力してください。");
        if (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
            throw new InvalidOperationException("HTTPはlocalhost接続でのみ許可されます。リモート接続にはHTTPSを使用してください。");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in profile.Headers)
        {
            if (IsProtocolVersionHeader(item.Name)) continue;
            headers[item.Name] = await ResolveValueAsync(item, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(profile.BearerTokenEnvironmentVariable))
        {
            var variableName = profile.BearerTokenEnvironmentVariable.Trim();
            var token = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException($"Bearerトークン用の環境変数 '{variableName}' が設定されていません。");
            headers["Authorization"] = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? token
                : "Bearer " + token;
        }
        HttpMessageHandler httpHandler = new HttpClientHandler();
        if (profile.BufferHttpRequestBody)
            httpHandler = new ContentLengthHandler(httpHandler);
        var httpClient = new HttpClient(httpHandler) { Timeout = Timeout.InfiniteTimeSpan };
        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = profile.Name,
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(Math.Clamp(profile.StartupTimeoutSeconds, 1, 3600)),
            EnableStandaloneGetStream = profile.EnableStandaloneGetStream,
            AdditionalHeaders = headers
        }, httpClient, loggerFactory, ownsHttpClient: true);
    }

    private async Task<string?> ResolveHttpProtocolVersionAsync(McpServerProfile profile, CancellationToken cancellationToken)
    {
        string? protocolVersion = null;
        foreach (var header in profile.Headers)
        {
            if (!IsProtocolVersionHeader(header.Name)) continue;
            protocolVersion = await ResolveValueAsync(header, cancellationToken).ConfigureAwait(false);
        }

        if (protocolVersion is null) return null;
        protocolVersion = protocolVersion.Trim();
        if (protocolVersion.Length == 0)
            throw new InvalidOperationException("MCP-Protocol-Versionには空でないプロトコルバージョンを指定してください。");
        return protocolVersion;
    }

    private static bool IsProtocolVersionHeader(string name)
        => string.Equals(name.Trim(), ProtocolVersionHeaderName, StringComparison.OrdinalIgnoreCase);

    private async Task<string?> ResolveValueAsync(SecretValue value, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(value.SecretRef))
            return await secretStore.GetAsync(value.SecretRef, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"資格情報 '{value.Name}' が見つかりません。");
        return value.Value;
    }

    private void RegisterTools(Connection connection)
    {
        foreach (var tool in connection.Tools)
        {
            var name = CreateNamespacedName(connection.Profile.Id, tool.Name);
            var definition = new ToolDefinition(
                name, connection.Profile.Id, tool.Name,
                tool.Description ?? tool.Title ?? tool.Name,
                tool.JsonSchema.Clone(), connection.Profile.Name);
            _tools[name] = new RegisteredTool(connection, tool, definition);
        }
    }

    public static string CreateNamespacedName(string serverId, string toolName)
    {
        var server = CleanWithStableSuffix(serverId, 24);
        var maxToolLength = Math.Max(1, 64 - server.Length - 2);
        var tool = CleanWithStableSuffix(toolName, maxToolLength);
        return server + "__" + tool;
    }

    private static string CleanWithStableSuffix(string value, int maximumLength)
    {
        var clean = new string(value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        if (clean.Length <= maximumLength && clean.Equals(value, StringComparison.Ordinal)) return clean;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..8];
        if (maximumLength <= hash.Length) return hash[..maximumLength];
        var prefixLength = maximumLength - hash.Length - 1;
        return clean[..Math.Min(clean.Length, prefixLength)] + "_" + hash;
    }

    private static string FormatResult(CallToolResult result)
    {
        var parts = result.Content.Select(content => content switch
        {
            TextContentBlock text => text.Text,
            _ => JsonSerializer.Serialize(content)
        }).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (result.StructuredContent is not null)
            parts.Add(JsonSerializer.Serialize(result.StructuredContent));
        return parts.Count == 0 ? "(ツールは空の結果を返しました)" : string.Join(Environment.NewLine, parts);
    }

    private void Publish(McpConnectionInfo info) => ConnectionChanged?.Invoke(this, info);

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _connections.Keys.ToArray()) await DisconnectAsync(id).ConfigureAwait(false);
    }

    private sealed record RegisteredTool(Connection Connection, McpClientTool Tool, ToolDefinition Definition);

    private sealed class ContentLengthHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is { Headers.ContentLength: null } content)
            {
                var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                content.Headers.ContentLength = body.LongLength;
            }
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Connection(McpServerProfile profile, McpClient client, IList<McpClientTool> tools) : IAsyncDisposable
    {
        public McpServerProfile Profile { get; } = profile;
        public McpClient Client { get; } = client;
        public IList<McpClientTool> Tools { get; } = tools;
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
}
