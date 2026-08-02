using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using LocalMCPChatClient.Core;
using LocalMCPChatClient.Infrastructure;

namespace LocalMCPChatClient.Tests;

public sealed class ToolArgumentValidatorTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","required":["city"],"properties":{"city":{"type":"string"},"days":{"type":"integer"}}}
        """).RootElement.Clone();

    [Fact]
    public void Accepts_valid_arguments()
        => Assert.Null(ToolArgumentValidator.Validate("{\"city\":\"Tokyo\",\"days\":3}", Schema));

    [Fact]
    public void Rejects_missing_required_argument()
        => Assert.Contains("city", ToolArgumentValidator.Validate("{\"days\":3}", Schema));

    [Fact]
    public void Rejects_wrong_primitive_type()
        => Assert.Contains("integer", ToolArgumentValidator.Validate("{\"city\":\"Tokyo\",\"days\":\"3\"}", Schema));

    [Fact]
    public void Rejects_broken_json()
        => Assert.NotNull(ToolArgumentValidator.Validate("{", Schema));
}

public sealed class McpNamingTests
{
    [Fact]
    public void Names_are_sanitized_namespaced_and_limited()
    {
        var result = McpConnectionManager.CreateNamespacedName("server id/with:punctuation", new string('x', 100));

        Assert.Contains("__", result);
        Assert.True(result.Length <= 64);
        Assert.DoesNotContain('/', result);
    }

    [Fact]
    public void Different_servers_do_not_collide()
        => Assert.NotEqual(
            McpConnectionManager.CreateNamespacedName("alpha", "search"),
            McpConnectionManager.CreateNamespacedName("beta", "search"));

    [Fact]
    public void Truncated_server_and_tool_names_keep_a_stable_collision_suffix()
    {
        var first = McpConnectionManager.CreateNamespacedName(new string('a', 40) + "x", new string('t', 80) + "x");
        var second = McpConnectionManager.CreateNamespacedName(new string('a', 40) + "y", new string('t', 80) + "y");

        Assert.NotEqual(first, second);
        Assert.True(first.Length <= 64);
        Assert.True(second.Length <= 64);
    }
}

public sealed class SettingsAndApprovalTests : IDisposable
{
    private readonly TestPaths _paths = new();

    [Fact]
    public async Task Defaults_include_both_pinned_Gemma_models()
    {
        var settings = await new JsonSettingsStore(_paths).LoadAsync();

        Assert.Equal(2, settings.Models.Count);
        Assert.Equal("fa401b55b07ee70a54c6dae3903c783a6e65064312529ea57175cb5f8dec6634",
            settings.Models.Single(model => model.Id == "gemma-4-e2b-it-q4").Sha256);
        Assert.Equal("676c35070db6dbe52f93e9c864ee0fba4eddea94b9c875d9cb10daff453fbaee",
            settings.Models.Single(model => model.Id == "gemma-4-e4b-it-q4").Sha256);
        Assert.All(settings.Models, model =>
        {
            Assert.NotEmpty(model.Revision);
            Assert.Equal(64, model.Sha256?.Length);
            Assert.True(model.Size > 1_000_000_000);
        });
    }

    [Fact]
    public async Task Secret_values_are_never_serialized_when_a_reference_exists()
    {
        var store = new JsonSettingsStore(_paths);
        var settings = JsonSettingsStore.CreateDefaults() with
        {
            McpServers =
            [
                new McpServerProfile
                {
                    Id = "server",
                    Environment = [new SecretValue("API_TOKEN", "plaintext-do-not-save", "cred-1")],
                    Headers = [new SecretValue("X-Mode", "development")],
                    EnableStandaloneGetStream = false,
                    BufferHttpRequestBody = true
                }
            ]
        };

        await store.SaveAsync(settings);
        var json = await File.ReadAllTextAsync(_paths.SettingsPath);

        Assert.DoesNotContain("plaintext-do-not-save", json);
        Assert.Contains("cred-1", json);
        Assert.Contains("development", json);
        var reloaded = (await store.LoadAsync()).McpServers.Single();
        Assert.False(reloaded.EnableStandaloneGetStream);
        Assert.True(reloaded.BufferHttpRequestBody);
    }

    [Fact]
    public async Task Approval_is_scoped_to_server_and_tool_and_persists()
    {
        var store = new JsonSettingsStore(_paths);
        var service = new ToolApprovalService(store);

        await service.RememberAsync("filesystem", "read_file", ApprovalDecision.Allow);

        Assert.Equal(ApprovalDecision.Allow, service.Evaluate("filesystem", "read_file"));
        Assert.Equal(ApprovalDecision.Ask, service.Evaluate("other", "read_file"));
        Assert.Equal(ApprovalDecision.Ask, service.Evaluate("filesystem", "write_file"));
        Assert.Equal(ApprovalDecision.Allow, new ToolApprovalService(store).Evaluate("filesystem", "read_file"));
    }

    [Fact]
    public async Task Corrupt_settings_are_backed_up_and_replaced_with_defaults()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(_paths.SettingsPath, "{not-json");

        var settings = await new JsonSettingsStore(_paths).LoadAsync();

        Assert.Equal(2, settings.Models.Count);
        Assert.Single(Directory.GetFiles(_paths.DataDirectory, "settings.json.corrupt-*"));
        Assert.NotEmpty(await File.ReadAllTextAsync(_paths.SettingsPath));
    }

    [Fact]
    public async Task Reset_replaces_all_settings_with_defaults()
    {
        var store = new JsonSettingsStore(_paths);
        await store.SaveAsync(JsonSettingsStore.CreateDefaults() with
        {
            SetupCompleted = true,
            InferenceMode = InferenceMode.Cuda,
            ContextSize = 16384,
            CustomRuntimePath = "C:\\custom\\llama-server.exe",
            ModelDirectory = "C:\\custom\\models",
            McpServers = [new McpServerProfile { Id = "server", Name = "Saved MCP" }],
            ApprovalRules = [new ToolApprovalRule("server", "tool", ApprovalDecision.Allow)]
        });

        var reset = await store.ResetAsync();

        Assert.False(reset.SetupCompleted);
        Assert.Equal(InferenceMode.Auto, reset.InferenceMode);
        Assert.Equal(8192, reset.ContextSize);
        Assert.Null(reset.CustomRuntimePath);
        Assert.Null(reset.ModelDirectory);
        Assert.Empty(reset.McpServers);
        Assert.Empty(reset.ApprovalRules);
        Assert.Equal(2, reset.Models.Count);
        Assert.All(reset.Models, model => Assert.Null(model.LocalPath));
        var json = await File.ReadAllTextAsync(_paths.SettingsPath);
        Assert.DoesNotContain("Saved MCP", json);
        Assert.DoesNotContain("C:\\custom", json);
    }

    public void Dispose() => _paths.Dispose();
}

public sealed class ConversationStoreTests : IDisposable
{
    private readonly TestPaths _paths = new();

    [Fact]
    public async Task Conversation_messages_round_trip_and_cascade_delete()
    {
        var store = new SqliteConversationStore(_paths);
        var conversation = await store.CreateAsync("test", "gemma");
        var message = new ChatMessage(Guid.NewGuid(), conversation.Id, ChatRole.Tool, "result", DateTimeOffset.UtcNow,
            "call-1", "server__tool", IsError: true);

        await store.AppendMessageAsync(message);
        var messages = await store.GetMessagesAsync(conversation.Id);
        await store.DeleteAsync(conversation.Id);

        var loaded = Assert.Single(messages);
        Assert.Equal("call-1", loaded.ToolCallId);
        Assert.True(loaded.IsError);
        Assert.Empty(await store.GetMessagesAsync(conversation.Id));
    }

    [Fact]
    public async Task Delete_last_turn_removes_user_and_every_following_tool_message()
    {
        var store = new SqliteConversationStore(_paths);
        var conversation = await store.CreateAsync("test");
        await store.AppendMessageAsync(new ChatMessage(Guid.NewGuid(), conversation.Id, ChatRole.User, "first", DateTimeOffset.UtcNow));
        await store.AppendMessageAsync(new ChatMessage(Guid.NewGuid(), conversation.Id, ChatRole.Assistant, "first answer", DateTimeOffset.UtcNow));
        await store.AppendMessageAsync(new ChatMessage(Guid.NewGuid(), conversation.Id, ChatRole.User, "again", DateTimeOffset.UtcNow));
        await store.AppendMessageAsync(new ChatMessage(Guid.NewGuid(), conversation.Id, ChatRole.Assistant, "", DateTimeOffset.UtcNow, ToolCallsJson: "[]"));
        await store.AppendMessageAsync(new ChatMessage(Guid.NewGuid(), conversation.Id, ChatRole.Tool, "result", DateTimeOffset.UtcNow));

        var text = await store.DeleteLastTurnAsync(conversation.Id);

        Assert.Equal("again", text);
        var remaining = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal([ChatRole.User, ChatRole.Assistant], remaining.Select(item => item.Role));
    }

    public void Dispose() => _paths.Dispose();
}

public sealed class MarkdownConversationExporterTests : IDisposable
{
    private readonly TestPaths _paths = new();

    [Fact]
    public async Task Exports_messages_tool_calls_and_results_as_utf8_markdown()
    {
        var store = new SqliteConversationStore(_paths);
        var conversation = await store.CreateAsync("エクスポート確認", "gemma-4-e2b-it-q4");
        var now = DateTimeOffset.Parse("2026-08-02T12:34:56Z");
        await store.AppendMessageAsync(new ChatMessage(Guid.NewGuid(), conversation.Id, ChatRole.User,
            "## 質問\n東京の天気は？", now));
        await store.AppendMessageAsync(new ChatMessage(Guid.NewGuid(), conversation.Id, ChatRole.Assistant,
            string.Empty, now.AddSeconds(1), ToolCallsJson: "[{\"id\":\"call-1\",\"function\":{\"name\":\"weather__forecast\",\"arguments\":\"{\\\"city\\\":\\\"東京\\\"}\"}}]"));
        await store.AppendMessageAsync(new ChatMessage(Guid.NewGuid(), conversation.Id, ChatRole.Tool,
            "取得に失敗しました。", now.AddSeconds(2), "call-1", "weather__forecast", IsError: true));
        var destination = Path.Combine(_paths.DataDirectory, "exports", "chat.md");

        await new MarkdownConversationExporter(store).ExportMarkdownAsync(conversation.Id, destination);

        var bytes = await File.ReadAllBytesAsync(destination);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        var markdown = await File.ReadAllTextAsync(destination);
        Assert.Contains("# エクスポート確認", markdown);
        Assert.Contains("- モデル: `gemma-4-e2b-it-q4`", markdown);
        Assert.Contains("## あなた", markdown);
        Assert.Contains("## Gemma", markdown);
        Assert.Contains("### Tool Calls", markdown);
        Assert.Contains("weather__forecast", markdown);
        Assert.Contains("## ツール: weather__forecast", markdown);
        Assert.Contains("**状態:** 失敗", markdown);
        Assert.Contains("**Tool Call ID:** `call-1`", markdown);
        Assert.Contains("取得に失敗しました。", markdown);
    }

    [Fact]
    public async Task Rejects_an_unknown_conversation()
    {
        var store = new SqliteConversationStore(_paths);
        var destination = Path.Combine(_paths.DataDirectory, "missing.md");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MarkdownConversationExporter(store).ExportMarkdownAsync(Guid.NewGuid(), destination));

        Assert.Contains("見つかりません", exception.Message);
        Assert.False(File.Exists(destination));
    }

    public void Dispose() => _paths.Dispose();
}

public sealed class DiagnosticRedactionTests
{
    [Fact]
    public void Masks_bearer_json_and_assignment_secrets()
    {
        var input = "Authorization: Bearer abc.def {\"apiKey\":\"secret123\"} password=hunter2 mode=dev";
        var masked = RedactingFileLoggerProvider.MaskSecrets(input);

        Assert.DoesNotContain("abc.def", masked);
        Assert.DoesNotContain("secret123", masked);
        Assert.DoesNotContain("hunter2", masked);
        Assert.Contains("mode=dev", masked);
    }
}

public sealed class ArtifactInstallerTests : IDisposable
{
    private readonly TestPaths _paths = new();

    [Fact]
    public async Task Resumes_download_verifies_hash_and_replaces_a_corrupt_managed_file()
    {
        var content = "local-model-content"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var requestCount = 0;
        var handler = new StubHttpHandler(request =>
        {
            requestCount++;
            var from = request.Headers.Range?.Ranges.Single().From;
            var response = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(from is null ? content : content[(int)from.Value..])
            };
            if (from is not null)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from.Value, content.Length - 1, content.Length);
            return response;
        });
        var artifact = new ArtifactDescriptor
        {
            Id = "test-model",
            Kind = ArtifactKind.Model,
            DisplayName = "test",
            DownloadUri = new Uri("https://example.invalid/model.gguf"),
            FileName = "model.gguf",
            Size = content.Length,
            Sha256 = hash
        };
        _paths.EnsureCreated();
        await File.WriteAllBytesAsync(Path.Combine(_paths.DownloadsDirectory, artifact.Id + ".partial"), content[..5]);
        var installer = new ArtifactInstaller(new HttpClient(handler), _paths);

        var installed = await installer.InstallAsync(artifact);
        Assert.Equal(content, await File.ReadAllBytesAsync(installed));
        Assert.Equal(1, requestCount);

        Assert.Equal(installed, await installer.InstallAsync(artifact));
        Assert.Equal(1, requestCount);

        await File.WriteAllTextAsync(installed, "corrupt");
        Assert.Equal(installed, await installer.InstallAsync(artifact));
        Assert.Equal(2, requestCount);
        Assert.Single(Directory.GetFiles(_paths.ModelsDirectory, "model.gguf.bad-*"));
    }

    [Fact]
    public async Task Retries_a_transient_request_and_keeps_the_partial_file()
    {
        var content = "resumable"u8.ToArray();
        var requestCount = 0;
        var handler = new StubHttpHandler(_ =>
        {
            requestCount++;
            if (requestCount == 1) throw new HttpRequestException("temporary failure");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        });
        var artifact = new ArtifactDescriptor
        {
            Id = "retry-model",
            Kind = ArtifactKind.Model,
            DisplayName = "retry",
            DownloadUri = new Uri("https://example.invalid/retry.gguf"),
            FileName = "retry.gguf",
            Size = content.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()
        };

        var installed = await new ArtifactInstaller(new HttpClient(handler), _paths).InstallAsync(artifact);

        Assert.Equal(2, requestCount);
        Assert.Equal(content, await File.ReadAllBytesAsync(installed));
    }

    public void Dispose() => _paths.Dispose();
}

internal sealed class TestPaths : IAppPaths, IDisposable
{
    public TestPaths()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "LocalMCPChatClient.Tests", Guid.NewGuid().ToString("N"));
    }

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string DatabasePath => Path.Combine(DataDirectory, "history.db");
    public string ModelsDirectory => Path.Combine(DataDirectory, "Models");
    public string RuntimesDirectory => Path.Combine(DataDirectory, "Runtimes");
    public string DownloadsDirectory => Path.Combine(DataDirectory, "Downloads");
    public string LogsDirectory => Path.Combine(DataDirectory, "Logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(RuntimesDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, true);
    }
}
