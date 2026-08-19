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
    ILogger<McpConnectionManager> logger,
    IArtifactStore? artifactStore = null) : IMcpConnectionManager
{
    private const string ProtocolVersionHeaderName = "MCP-Protocol-Version";
    private const int MaxToolResultCharacters = 256 * 1024;
    private const int MaxResourceBytes = 256 * 1024;
    private const int MaxImagesPerToolCall = 8;
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RegisteredTool> _tools = new(StringComparer.Ordinal);
    public event EventHandler<McpConnectionInfo>? ConnectionChanged;
    public event EventHandler<McpResourceReference>? ResourceChanged;

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
            var info = CreateConnectionInfo(connection);
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
            return CreateConnectionInfo(connection);
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
            .Select(CreateConnectionInfo)
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Task.FromResult(result);
    }

    public async Task<IReadOnlyList<McpResourceCatalog>> GetResourceCatalogsAsync(CancellationToken cancellationToken = default)
    {
        var catalogs = new List<McpResourceCatalog>();
        foreach (var connection in _connections.Values.OrderBy(item => item.Profile.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!connection.SupportsResources)
            {
                catalogs.Add(new McpResourceCatalog(connection.Profile.Id, connection.Profile.Name, []));
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.Profile.TimeoutSeconds, 1, 3600)));
            try
            {
                var resources = await connection.Client.ListResourcesAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
                catalogs.Add(new McpResourceCatalog(
                    connection.Profile.Id,
                    connection.Profile.Name,
                    resources.Select(resource => new McpResourceDefinition(
                            connection.Profile.Id,
                            connection.Profile.Name,
                            resource.Uri,
                            resource.Title ?? resource.Name,
                            resource.Description,
                            resource.MimeType,
                            resource.ProtocolResource.Size))
                        .OrderBy(resource => resource.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(resource => resource.Uri, StringComparer.Ordinal)
                        .ToList()));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                catalogs.Add(new McpResourceCatalog(connection.Profile.Id, connection.Profile.Name, [], "Resource一覧の取得がタイムアウトしました。"));
            }
            catch (Exception exception)
            {
                logger.LogWarning("Failed to list MCP resources for server {ServerId}: {ErrorType}",
                    connection.Profile.Id, exception.GetType().Name);
                catalogs.Add(new McpResourceCatalog(connection.Profile.Id, connection.Profile.Name, [], "Resource一覧を取得できませんでした: " + exception.Message));
            }
        }
        return catalogs;
    }

    public async Task<IReadOnlyList<McpResourceTemplateDefinition>> GetResourceTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<McpResourceTemplateDefinition>();
        foreach (var connection in _connections.Values.OrderBy(item => item.Profile.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!connection.SupportsResources) continue;
            using var timeout = CreateTimeout(connection, cancellationToken);
            var templates = await connection.Client.ListResourceTemplatesAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            result.AddRange(templates.Select(template => new McpResourceTemplateDefinition(
                connection.Profile.Id,
                connection.Profile.Name,
                template.UriTemplate,
                template.Title ?? template.Name,
                template.Description,
                template.MimeType)));
        }
        return result;
    }

    public async Task<IReadOnlyList<McpPromptDefinition>> GetPromptsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<McpPromptDefinition>();
        foreach (var connection in _connections.Values.OrderBy(item => item.Profile.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!connection.SupportsPrompts) continue;
            using var timeout = CreateTimeout(connection, cancellationToken);
            var prompts = await connection.Client.ListPromptsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            result.AddRange(prompts.Select(prompt => new McpPromptDefinition(
                connection.Profile.Id,
                connection.Profile.Name,
                prompt.Name,
                prompt.Title,
                prompt.Description,
                (prompt.ProtocolPrompt.Arguments ?? []).Select(argument => new McpPromptArgument(
                    argument.Name, argument.Description, argument.Required == true)).ToList())));
        }
        return result;
    }

    public async Task<McpPromptResult> GetPromptAsync(
        string serverId,
        string promptName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(serverId, out var connection))
            throw new InvalidOperationException("MCPサーバーは切断されています。");
        if (!connection.SupportsPrompts) throw new InvalidOperationException("このMCPサーバーはPromptに対応していません。");
        using var timeout = CreateTimeout(connection, cancellationToken);
        var result = await connection.Client.GetPromptAsync(promptName, arguments, cancellationToken: timeout.Token).ConfigureAwait(false);
        var parts = new List<McpContentPart>();
        foreach (var message in result.Messages)
        {
            parts.Add(new McpContentPart(McpContentKind.Text, $"[{message.Role}]"));
            parts.AddRange(await ConvertContentBlockAsync(message.Content, connection.Profile.Name, timeout.Token).ConfigureAwait(false));
        }
        return new McpPromptResult(serverId, connection.Profile.Name, promptName, result.Description, parts);
    }

    public async Task<McpResourceSnapshot> ReadResourceAsync(McpResourceReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!_connections.TryGetValue(reference.ServerId, out var connection))
            throw new InvalidOperationException($"MCPサーバー '{reference.ServerDisplayName}' は切断されています。");
        if (!connection.SupportsResources)
            throw new InvalidOperationException($"MCPサーバー '{reference.ServerDisplayName}' はResourceに対応していません。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.Profile.TimeoutSeconds, 1, 3600)));
        try
        {
            var result = await connection.Client.ReadResourceAsync(reference.Uri, cancellationToken: timeout.Token).ConfigureAwait(false);
            var parts = new List<McpContentPart>();
            var textParts = new List<string>();
            var skippedBinaryParts = 0;
            var binaryBytes = 0;
            foreach (var item in result.Contents)
            {
                if (item is TextResourceContents text && !string.IsNullOrEmpty(text.Text))
                {
                    textParts.Add(text.Text);
                    parts.Add(new McpContentPart(
                        IsJsonMime(text.MimeType) ? McpContentKind.StructuredJson : McpContentKind.Text,
                        text.Text, text.MimeType, text.Uri));
                }
                else if (item is BlobResourceContents blob)
                {
                    binaryBytes += blob.DecodedData.Length;
                    if (artifactStore is null) { skippedBinaryParts++; continue; }
                    var mimeType = blob.MimeType ?? reference.MimeType ?? "application/octet-stream";
                    var artifact = await artifactStore.StoreAsync(blob.DecodedData, mimeType, reference.Name, blob.Uri, timeout.Token).ConfigureAwait(false);
                    parts.Add(new McpContentPart(
                        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? McpContentKind.Image : McpContentKind.Blob,
                        MimeType: mimeType, Uri: blob.Uri, Name: reference.Name, Artifact: artifact));
                }
            }
            if (parts.Count == 0 && skippedBinaryParts > 0)
                throw new NotSupportedException("バイナリResourceを保存するartifactストアが構成されていません。");
            if (parts.Count == 0) throw new InvalidOperationException("このResourceは利用可能な内容を返しませんでした。");

            var content = string.Join(Environment.NewLine + Environment.NewLine, textParts);
            var originalByteCount = Encoding.UTF8.GetByteCount(content) + binaryBytes;
            var truncated = originalByteCount > MaxResourceBytes;
            if (Encoding.UTF8.GetByteCount(content) > MaxResourceBytes)
                content = ResourceSnapshotBudget.TruncateUtf8(content, MaxResourceBytes);
            if (string.IsNullOrWhiteSpace(content) && parts.Any(part => part.Kind == McpContentKind.Image))
                content = "(画像Resource)";
            return new McpResourceSnapshot(
                reference.ServerId,
                connection.Profile.Name,
                reference.Uri,
                reference.Name,
                reference.MimeType,
                content,
                DateTimeOffset.UtcNow,
                originalByteCount,
                truncated,
                skippedBinaryParts,
                parts);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Resource '{reference.Name}' の読取がタイムアウトしました。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TimeoutException and not NotSupportedException)
        {
            logger.LogWarning("Failed to read MCP resource from server {ServerId}: {ErrorType}",
                reference.ServerId, exception.GetType().Name);
            throw new InvalidOperationException($"Resource '{reference.Name}' を読み取れませんでした: {exception.Message}", exception);
        }
    }

    public async Task<IAsyncDisposable> SubscribeToResourceAsync(
        McpResourceReference reference,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(reference.ServerId, out var connection))
            throw new InvalidOperationException("MCPサーバーは切断されています。");
        if (!connection.SupportsSubscriptions)
            throw new NotSupportedException("このMCPサーバーはResource購読に対応していません。");
        return await connection.Client.SubscribeToResourceAsync(
            reference.Uri,
            (_, _) =>
            {
                ResourceChanged?.Invoke(this, reference);
                return ValueTask.CompletedTask;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
            var parts = await ConvertToolResultAsync(result, registered.Connection.Profile.Name, timeout.Token).ConfigureAwait(false);
            var content = FormatResult(parts);
            var truncated = content.Length > MaxToolResultCharacters;
            if (truncated) content = content[..MaxToolResultCharacters] + "\n…(結果を256 KiBで切り詰めました)";
            return new McpToolResult(request.Id, request.Name, content, result.IsError == true, truncated, parts);
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
            IList<McpClientTool> tools = client.ServerCapabilities.Tools is null
                ? []
                : await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            var lookDevContractVersion = await ResolveLookDevContractVersionAsync(profile, client, timeout.Token).ConfigureAwait(false);
            return new Connection(profile, client, tools, lookDevContractVersion);
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

    private static async Task<string?> ResolveLookDevContractVersionAsync(
        McpServerProfile profile,
        McpClient client,
        CancellationToken cancellationToken)
    {
        var fromInitialize = ReadLookDevContractVersion(client.ServerCapabilities.Experimental);
        if (!string.IsNullOrWhiteSpace(fromInitialize)) return fromInitialize;
        if (profile.Transport != McpTransportKind.StreamableHttp ||
            !Uri.TryCreate(profile.Url, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback)
            return null;

        // Some MCP SDK versions do not preserve experimental capabilities from
        // the 2026 server/discover response. The LookDev well-known document is
        // an explicit, tool-name-independent fallback for this local contract.
        try
        {
            using var clientForDiscovery = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await clientForDiscovery.GetAsync(
                new Uri(endpoint, "/.well-known/lookdevpt/v1"), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("contractVersion", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string? ReadLookDevContractVersion(object? experimental)
    {
        if (experimental is null) return null;
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(experimental));
            if (!document.RootElement.TryGetProperty("lookdevpt", out var lookdev) ||
                !lookdev.TryGetProperty("contractVersion", out var version)) return null;
            return version.GetString();
        }
        catch (JsonException) { return null; }
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

    private async Task<IReadOnlyList<McpContentPart>> ConvertToolResultAsync(
        CallToolResult result,
        string serverName,
        CancellationToken cancellationToken)
    {
        var parts = new List<McpContentPart>();
        foreach (var content in result.Content)
        {
            parts.AddRange(await ConvertContentBlockAsync(content, serverName, cancellationToken).ConfigureAwait(false));
            if (parts.Count(part => part.Kind == McpContentKind.Image) > MaxImagesPerToolCall)
                throw new InvalidDataException($"Tool result exceeds the {MaxImagesPerToolCall}-image limit.");
        }
        if (result.StructuredContent is not null)
            parts.Add(new McpContentPart(McpContentKind.StructuredJson, JsonSerializer.Serialize(result.StructuredContent), "application/json"));
        return parts;
    }

    private async Task<IReadOnlyList<McpContentPart>> ConvertContentBlockAsync(
        ContentBlock content,
        string serverName,
        CancellationToken cancellationToken)
    {
        switch (content)
        {
            case TextContentBlock text:
                return [new McpContentPart(McpContentKind.Text, text.Text)];
            case ImageContentBlock image:
            {
                if (artifactStore is null) return [new McpContentPart(McpContentKind.Image, "(画像は保存できませんでした)", image.MimeType)];
                var artifact = await artifactStore.StoreAsync(
                    image.DecodedData, image.MimeType, serverName + " image", cancellationToken: cancellationToken).ConfigureAwait(false);
                return [new McpContentPart(McpContentKind.Image, MimeType: image.MimeType, Name: artifact.DisplayName, Artifact: artifact)];
            }
            case ResourceLinkBlock link:
                return [new McpContentPart(McpContentKind.ResourceLink, link.Description, link.MimeType, link.Uri, link.Title ?? link.Name)];
            case EmbeddedResourceBlock embedded when embedded.Resource is TextResourceContents textResource:
                return [new McpContentPart(
                    IsJsonMime(textResource.MimeType) ? McpContentKind.StructuredJson : McpContentKind.EmbeddedResource,
                    textResource.Text, textResource.MimeType, textResource.Uri)];
            case EmbeddedResourceBlock embedded when embedded.Resource is BlobResourceContents blob:
            {
                if (artifactStore is null) return [new McpContentPart(McpContentKind.Blob, "(バイナリResourceは保存できませんでした)", blob.MimeType, blob.Uri)];
                var mimeType = blob.MimeType ?? "application/octet-stream";
                var artifact = await artifactStore.StoreAsync(blob.DecodedData, mimeType, serverName + " resource", blob.Uri, cancellationToken).ConfigureAwait(false);
                return [new McpContentPart(
                    mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? McpContentKind.Image : McpContentKind.EmbeddedResource,
                    MimeType: mimeType, Uri: blob.Uri, Name: artifact.DisplayName, Artifact: artifact)];
            }
            default:
                return [new McpContentPart(McpContentKind.Text, JsonSerializer.Serialize(content))];
        }
    }

    private static string FormatResult(IReadOnlyList<McpContentPart> parts)
    {
        var text = parts.Select(part => part.Kind switch
        {
            McpContentKind.Text or McpContentKind.StructuredJson or McpContentKind.EmbeddedResource => part.Text,
            McpContentKind.Image => $"[image: {part.Name ?? part.Artifact?.DisplayName ?? part.MimeType}]",
            McpContentKind.ResourceLink => $"[resource: {part.Name ?? part.Uri}] {part.Uri}",
            McpContentKind.Blob => $"[binary: {part.Name ?? part.MimeType}]",
            _ => part.Text
        }).Where(value => !string.IsNullOrWhiteSpace(value));
        var value = string.Join(Environment.NewLine, text);
        return string.IsNullOrWhiteSpace(value) ? "(ツールは空の結果を返しました)" : value;
    }

    private static bool IsJsonMime(string? mimeType)
        => mimeType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

    private static CancellationTokenSource CreateTimeout(Connection connection, CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.Profile.TimeoutSeconds, 1, 3600)));
        return timeout;
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

    private static McpConnectionInfo CreateConnectionInfo(Connection connection) => new(
        connection.Profile.Id,
        connection.Profile.Name,
        McpConnectionState.Connected,
        ToolCount: connection.Tools.Count,
        SupportsResources: connection.SupportsResources,
        SupportsPrompts: connection.SupportsPrompts,
        SupportsResourceTemplates: connection.SupportsResources,
        SupportsSubscriptions: connection.SupportsSubscriptions,
        LookDevContractVersion: connection.LookDevContractVersion);

    private sealed class Connection(
        McpServerProfile profile,
        McpClient client,
        IList<McpClientTool> tools,
        string? lookDevContractVersion) : IAsyncDisposable
    {
        public McpServerProfile Profile { get; } = profile;
        public McpClient Client { get; } = client;
        public IList<McpClientTool> Tools { get; } = tools;
        public bool SupportsResources => Client.ServerCapabilities.Resources is not null;
        public bool SupportsPrompts => Client.ServerCapabilities.Prompts is not null;
        public bool SupportsSubscriptions => Client.ServerCapabilities.Resources?.Subscribe == true;
        public string? LookDevContractVersion { get; } = lookDevContractVersion;
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
}
