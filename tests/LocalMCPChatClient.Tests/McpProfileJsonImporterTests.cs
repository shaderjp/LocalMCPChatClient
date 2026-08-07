using LocalMCPChatClient.Core;
using LocalMCPChatClient.Infrastructure;

namespace LocalMCPChatClient.Tests;

public sealed class McpProfileJsonImporterTests
{
    private readonly McpProfileJsonImporter _importer = new();

    [Fact]
    public void Imports_servers_http_format_and_separates_authorization_secret()
    {
        const string json = """
            {
              "servers": {
                "lookdev": {
                  "type": "http",
                  "url": "http://127.0.0.1:8777/mcp",
                  "headers": {
                    "Authorization": "Bearer example-token",
                    "MCP-Protocol-Version": "2025-11-25"
                  }
                }
              }
            }
            """;

        var result = _importer.ParseJson(json);

        var imported = Assert.Single(result.Servers);
        Assert.Equal("lookdev", imported.Profile.Name);
        Assert.Equal(McpTransportKind.StreamableHttp, imported.Profile.Transport);
        Assert.Equal("http://127.0.0.1:8777/mcp", imported.Profile.Url);
        Assert.False(imported.Profile.EnableStandaloneGetStream);
        var protocol = Assert.Single(imported.Profile.Headers);
        Assert.Equal("MCP-Protocol-Version", protocol.Name);
        Assert.Equal("2025-11-25", protocol.Value);
        Assert.Equal("Bearer example-token", imported.SecretHeaders["Authorization"]);
        Assert.Equal(1, result.SecretCount);
    }

    [Fact]
    public void Imports_common_mcp_servers_stdio_format()
    {
        const string json = """
            {
              "mcpServers": {
                "filesystem": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-filesystem", "D:\\Work"],
                  "env": {
                    "LOG_LEVEL": "debug",
                    "API_TOKEN": "example-secret"
                  },
                  "disabled": true
                }
              }
            }
            """;

        var imported = Assert.Single(_importer.ParseJson(json).Servers);

        Assert.Equal(McpTransportKind.Stdio, imported.Profile.Transport);
        Assert.Equal("npx", imported.Profile.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "D:\\Work"], imported.Profile.Arguments);
        Assert.False(imported.Profile.Enabled);
        Assert.Equal("debug", Assert.Single(imported.Profile.Environment).Value);
        Assert.Equal("example-secret", imported.SecretEnvironment["API_TOKEN"]);
    }

    [Fact]
    public void Converts_authorization_environment_reference_to_supported_setting()
    {
        const string json = """
            {
              "servers": {
                "remote": {
                  "type": "streamable-http",
                  "url": "https://example.test/mcp",
                  "headers": {
                    "Authorization": "Bearer ${env:MCP_ACCESS_TOKEN}"
                  }
                }
              }
            }
            """;

        var imported = Assert.Single(_importer.ParseJson(json).Servers);

        Assert.Equal("MCP_ACCESS_TOKEN", imported.Profile.BearerTokenEnvironmentVariable);
        Assert.Empty(imported.Profile.Headers);
        Assert.Empty(imported.SecretHeaders);
    }

    [Fact]
    public void Imports_d3d12lookdevpt_profile_without_pinning_the_protocol()
    {
        const string json = """
            {
              "servers": {
                "d3d12LookDevPT": {
                  "name": "D3D12LookDevPT",
                  "type": "http",
                  "url": "http://127.0.0.1:8777/mcp",
                  "headers": {
                    "Authorization": "Bearer ${env:D3D12LOOKDEVPT_MCP_TOKEN}"
                  },
                  "enableStandaloneGetStream": false,
                  "startupTimeoutSeconds": 10,
                  "toolTimeoutSeconds": 120
                }
              }
            }
            """;

        var imported = Assert.Single(_importer.ParseJson(json).Servers);

        Assert.Equal("D3D12LookDevPT", imported.Profile.Name);
        Assert.Equal("http://127.0.0.1:8777/mcp", imported.Profile.Url);
        Assert.Equal("D3D12LOOKDEVPT_MCP_TOKEN", imported.Profile.BearerTokenEnvironmentVariable);
        Assert.False(imported.Profile.EnableStandaloneGetStream);
        Assert.Equal(10, imported.Profile.StartupTimeoutSeconds);
        Assert.Equal(120, imported.Profile.TimeoutSeconds);
        Assert.Empty(imported.Profile.Headers);
        Assert.Empty(imported.SecretHeaders);
    }

    [Fact]
    public void Keeps_valid_servers_and_reports_unsupported_entries()
    {
        const string json = """
            {
              "servers": {
                "legacy": {
                  "type": "sse",
                  "url": "https://example.test/sse"
                },
                "valid": {
                  "type": "http",
                  "url": "https://example.test/mcp",
                  "tool_timeout_sec": 120
                }
              }
            }
            """;

        var result = _importer.ParseJson(json);

        Assert.Equal("valid", Assert.Single(result.Servers).Profile.Name);
        Assert.Equal(120, result.Servers[0].Profile.TimeoutSeconds);
        Assert.Contains(result.Warnings, warning => warning.Contains("旧SSE", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_json_without_a_supported_server_container()
    {
        var exception = Assert.Throws<InvalidDataException>(() => _importer.ParseJson("{\"profiles\":{}}"));

        Assert.Contains("servers", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Imports_the_file_selected_by_the_ui()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LocalMCPChatClient-mcp-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "servers": {
                    "local": {
                      "type": "http",
                      "url": "http://localhost:3001/mcp"
                    }
                  }
                }
                """);

            var result = await _importer.ImportAsync(path);

            Assert.Equal("local", Assert.Single(result.Servers).Profile.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
