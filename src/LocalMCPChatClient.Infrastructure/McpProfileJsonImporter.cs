using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.Infrastructure;

public sealed class McpProfileJsonImporter : IMcpProfileImporter
{
    private const long MaximumFileSize = 2 * 1024 * 1024;
    private static readonly Regex EnvironmentReferencePattern = new(
        @"^(?:Bearer\s+)?\$\{(?:env:)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<McpProfileImportResult> ImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("インポートするJSONファイルを指定してください。", nameof(filePath));

        var fullPath = Path.GetFullPath(filePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("MCP設定JSONが見つかりません。", fullPath);
        if (file.Length > MaximumFileSize) throw new InvalidDataException("MCP設定JSONは2 MiB以下にしてください。");

        var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return ParseJson(json);
    }

    public McpProfileImportResult ParseJson(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("有効なMCP設定JSONではありません。", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("MCP設定JSONのルートはオブジェクトである必要があります。");

            if (!TryGetProperty(document.RootElement, out var serversElement, "servers", "mcpServers") ||
                serversElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("ルートにserversまたはmcpServersオブジェクトがありません。");

            var servers = new List<ImportedMcpServer>();
            var warnings = new List<string>();
            var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var displayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in serversElement.EnumerateObject())
            {
                var sourceName = property.Name.Trim();
                if (string.IsNullOrWhiteSpace(sourceName))
                {
                    warnings.Add("名前が空のサーバー設定をスキップしました。");
                    continue;
                }
                if (!sourceNames.Add(sourceName))
                {
                    warnings.Add($"{sourceName}: 同名のサーバー設定をスキップしました。");
                    continue;
                }

                try
                {
                    var server = ParseServer(sourceName, property.Value, warnings);
                    if (!displayNames.Add(server.Profile.Name))
                    {
                        warnings.Add($"{sourceName}: 表示名'{server.Profile.Name}'が重複するためスキップしました。");
                        continue;
                    }
                    servers.Add(server);
                }
                catch (InvalidDataException exception)
                {
                    warnings.Add($"{sourceName}: {exception.Message}");
                }
            }

            if (servers.Count == 0)
                throw new InvalidDataException(warnings.Count == 0
                    ? "インポートできるMCPサーバー設定がありません。"
                    : "インポートできるMCPサーバー設定がありません。" + Environment.NewLine + string.Join(Environment.NewLine, warnings));

            return new McpProfileImportResult(servers, warnings);
        }
    }

    private static ImportedMcpServer ParseServer(string sourceName, JsonElement element, List<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("設定はオブジェクトである必要があります。");

        var displayName = ReadOptionalString(element, "name", "displayName")?.Trim();
        if (string.IsNullOrWhiteSpace(displayName)) displayName = sourceName;
        var type = ReadOptionalString(element, "type", "transport")?.Trim();
        var command = ReadOptionalString(element, "command")?.Trim();
        var url = ReadOptionalString(element, "url")?.Trim();
        var transport = ResolveTransport(type, command, url);
        var enabled = ReadOptionalBoolean(element, true, "enabled");
        if (TryGetProperty(element, out var disabledElement, "disabled") && TryReadBoolean(disabledElement, out var disabled))
            enabled = !disabled;

        var publicEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secretEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadPairs(element, publicEnvironment, secretEnvironment, false, warnings, displayName, "env", "environment");

        var publicHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secretHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bearerEnvironment = ReadOptionalString(element,
            "bearerTokenEnvironmentVariable", "bearerTokenEnvVar", "bearer_token_env_var")?.Trim();
        string? authorizationEnvironmentReference = null;
        ReadHeaders(element, publicHeaders, secretHeaders, ref authorizationEnvironmentReference, warnings, displayName);

        if (!string.IsNullOrWhiteSpace(authorizationEnvironmentReference))
        {
            if (secretEnvironment.Remove(authorizationEnvironmentReference, out var secretToken) ||
                publicEnvironment.Remove(authorizationEnvironmentReference, out secretToken))
            {
                secretHeaders["Authorization"] = NormalizeBearerToken(secretToken);
            }
            else if (string.IsNullOrWhiteSpace(bearerEnvironment))
            {
                bearerEnvironment = authorizationEnvironmentReference;
            }
        }

        var bearerToken = ReadOptionalString(element, "bearerToken", "bearer_token");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            if (string.IsNullOrWhiteSpace(bearerEnvironment))
                secretHeaders["Authorization"] = NormalizeBearerToken(bearerToken);
            else
                warnings.Add($"{displayName}: bearerTokenはbearerTokenEnvironmentVariableが指定されているため無視しました。");
        }

        if (!string.IsNullOrWhiteSpace(bearerEnvironment))
        {
            publicHeaders.Remove("Authorization");
            secretHeaders.Remove("Authorization");
        }

        var profile = new McpServerProfile
        {
            Name = displayName,
            Enabled = enabled,
            Transport = transport,
            Command = transport == McpTransportKind.Stdio ? RequireCommand(command) : null,
            Arguments = ReadStringArray(element, "args", "arguments"),
            WorkingDirectory = ReadOptionalString(element, "cwd", "workingDirectory", "working_directory")?.Trim(),
            Environment = publicEnvironment.Select(pair => new SecretValue(pair.Key, pair.Value)).ToList(),
            Url = transport == McpTransportKind.StreamableHttp ? RequireHttpUrl(url) : null,
            Headers = publicHeaders.Select(pair => new SecretValue(pair.Key, pair.Value)).ToList(),
            BearerTokenEnvironmentVariable = string.IsNullOrWhiteSpace(bearerEnvironment) ? null : bearerEnvironment,
            EnableStandaloneGetStream = ReadOptionalBoolean(element, false,
                "enableStandaloneGetStream", "standaloneGet", "enable_standalone_get_stream"),
            BufferHttpRequestBody = ReadOptionalBoolean(element, false,
                "bufferHttpRequestBody", "bufferRequestBody", "buffer_http_request_body"),
            StartupTimeoutSeconds = ReadTimeout(element, 10, warnings, displayName,
                "startupTimeoutSeconds", "startupTimeoutSec", "startup_timeout_sec"),
            TimeoutSeconds = ReadTimeout(element, 60, warnings, displayName,
                "timeoutSeconds", "toolTimeoutSeconds", "toolTimeoutSec", "tool_timeout_sec", "timeout")
        };

        return new ImportedMcpServer(profile, secretEnvironment, secretHeaders);
    }

    private static McpTransportKind ResolveTransport(string? type, string? command, string? url)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            if (!string.IsNullOrWhiteSpace(command)) return McpTransportKind.Stdio;
            if (!string.IsNullOrWhiteSpace(url)) return McpTransportKind.StreamableHttp;
            throw new InvalidDataException("type、command、urlのいずれからもTransportを判定できません。");
        }

        return type.Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "stdio" => McpTransportKind.Stdio,
            "http" or "streamable-http" or "streamablehttp" => McpTransportKind.StreamableHttp,
            "sse" => throw new InvalidDataException("旧SSE transportは対応していません。"),
            _ => throw new InvalidDataException($"未対応のTransportです: {type}")
        };
    }

    private static string RequireCommand(string? command)
        => string.IsNullOrWhiteSpace(command)
            ? throw new InvalidDataException("stdio設定にcommandがありません。")
            : command;

    private static string RequireHttpUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
            throw new InvalidDataException("HTTP設定に有効なurlがありません。");
        if (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
            throw new InvalidDataException("HTTPはlocalhostだけで許可されます。リモートURLにはHTTPSを使用してください。");
        return endpoint.AbsoluteUri;
    }

    private static void ReadPairs(
        JsonElement server,
        Dictionary<string, string> publicValues,
        Dictionary<string, string> secretValues,
        bool headers,
        List<string> warnings,
        string serverName,
        params string[] propertyNames)
    {
        if (!TryGetProperty(server, out var valuesElement, propertyNames)) return;
        if (valuesElement.ValueKind != JsonValueKind.Object)
        {
            warnings.Add($"{serverName}: {propertyNames[0]}はオブジェクトではないため無視しました。");
            return;
        }

        foreach (var property in valuesElement.EnumerateObject())
        {
            var name = property.Name.Trim();
            if (string.IsNullOrWhiteSpace(name) || !TryReadScalar(property.Value, out var value))
            {
                warnings.Add($"{serverName}: {propertyNames[0]}内の無効な項目を無視しました。");
                continue;
            }

            if (IsSensitiveName(name, headers)) secretValues[name] = value;
            else publicValues[name] = value;
        }
    }

    private static void ReadHeaders(
        JsonElement server,
        Dictionary<string, string> publicHeaders,
        Dictionary<string, string> secretHeaders,
        ref string? authorizationEnvironmentReference,
        List<string> warnings,
        string serverName)
    {
        if (!TryGetProperty(server, out var headersElement, "headers", "httpHeaders", "http_headers")) return;
        if (headersElement.ValueKind != JsonValueKind.Object)
        {
            warnings.Add($"{serverName}: headersはオブジェクトではないため無視しました。");
            return;
        }

        foreach (var property in headersElement.EnumerateObject())
        {
            var name = property.Name.Trim();
            if (string.IsNullOrWhiteSpace(name) || !TryReadScalar(property.Value, out var value))
            {
                warnings.Add($"{serverName}: headers内の無効な項目を無視しました。");
                continue;
            }

            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                var match = EnvironmentReferencePattern.Match(value.Trim());
                if (match.Success)
                {
                    authorizationEnvironmentReference = match.Groups["name"].Value;
                    continue;
                }
            }

            if (value.Contains("${", StringComparison.Ordinal))
                warnings.Add($"{serverName}: {name}の変数展開式はLocalMCPChatClientでは展開されません。");

            if (IsSensitiveName(name, true)) secretHeaders[name] = value;
            else publicHeaders[name] = value;
        }
    }

    private static bool IsSensitiveName(string name, bool header)
    {
        var normalized = name.Replace("-", "_", StringComparison.Ordinal).ToUpperInvariant();
        if (header && normalized is "AUTHORIZATION" or "PROXY_AUTHORIZATION" or "COOKIE" or "SET_COOKIE") return true;
        return normalized.Contains("TOKEN", StringComparison.Ordinal) ||
               normalized.Contains("SECRET", StringComparison.Ordinal) ||
               normalized.Contains("PASSWORD", StringComparison.Ordinal) ||
               normalized.Contains("PASSWD", StringComparison.Ordinal) ||
               normalized.Contains("API_KEY", StringComparison.Ordinal) ||
               normalized.Contains("APIKEY", StringComparison.Ordinal) ||
               normalized.Contains("PRIVATE_KEY", StringComparison.Ordinal) ||
               normalized.Contains("CREDENTIAL", StringComparison.Ordinal);
    }

    private static string NormalizeBearerToken(string value)
        => value.Trim().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value.Trim() : "Bearer " + value.Trim();

    private static List<string> ReadStringArray(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var array, names) || array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray().Select(item => TryReadScalar(item, out var value) ? value : null)
            .Where(value => value is not null).Select(value => value!).ToList();
    }

    private static int ReadTimeout(JsonElement element, int fallback, List<string> warnings, string serverName, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ||
            value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return Math.Clamp(number, 1, 3600);
        warnings.Add($"{serverName}: {names[0]}が整数ではないため既定値{fallback}秒を使用します。");
        return fallback;
    }

    private static string? ReadOptionalString(JsonElement element, params string[] names)
        => TryGetProperty(element, out var value, names) && TryReadScalar(value, out var text) ? text : null;

    private static bool ReadOptionalBoolean(JsonElement element, bool fallback, params string[] names)
        => TryGetProperty(element, out var value, names) && TryReadBoolean(value, out var result) ? result : fallback;

    private static bool TryReadBoolean(JsonElement element, out bool value)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }
        if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out value)) return true;
        value = false;
        return false;
    }

    private static bool TryReadScalar(JsonElement element, out string value)
    {
        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => string.Empty
        };
        return element.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            foreach (var name in names)
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
