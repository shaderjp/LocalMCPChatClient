using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LocalMCPChatClient.Core;
using LocalMCPChatClient.Infrastructure;

namespace LocalMCPChatClient.Tests;

public sealed class LlamaInferenceServiceTests
{
    [Fact]
    public async Task Parses_streamed_text_and_fragmented_tool_call()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"こんにちは"}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"weather__get_","arguments":"{\"city\":"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"forecast","arguments":"\"東京\"}"}}]}}]}

            data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15},"timings":{"prompt_per_second":500.0,"predicted_per_second":125.0}}

            data: [DONE]

            """;
        string? requestBody = null;
        var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("runtime-key", request.Headers.Authorization?.Parameter);
            if (request.RequestUri?.AbsolutePath == "/completion")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"content\":\"x\"}") };
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });
        var runtime = new StubRuntimeManager();
        var service = new LlamaInferenceService(runtime, new StubHttpClientFactory(handler));
        await service.StartAsync(new InferenceProfile(), new ModelProfile { Id = "model" });

        var updates = await CollectAsync(service.StreamCompletionAsync(
            new InferenceRequest("model", [], [], 0.2, 128)));

        Assert.Equal("こんにちは", updates.Single(update => update.TextDelta is not null).TextDelta);
        var call = Assert.Single(updates.Single(update => update.ToolCalls is not null).ToolCalls!);
        Assert.Equal("weather__get_forecast", call.Name);
        Assert.Equal("{\"city\":\"東京\"}", call.ArgumentsJson);
        Assert.Equal(15, updates.Last().Usage?.TotalTokens);
        Assert.True(updates.Last().Timing?.TimeToFirstTokenMilliseconds > 0);
        Assert.Equal(500, updates.Last().Timing?.PromptTokensPerSecond);
        Assert.Equal(125, updates.Last().Timing?.GeneratedTokensPerSecond);
        Assert.Contains("\"cache_prompt\":true", requestBody);
        Assert.Contains("\"reasoning_effort\":\"none\"", requestBody);
        Assert.Contains("\"enable_thinking\":false", requestBody);
    }

    [Fact]
    public async Task Preparation_is_idempotent_for_the_same_profile_and_model()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":\"x\"}")
        });
        var runtime = new StubRuntimeManager();
        var service = new LlamaInferenceService(runtime, new StubHttpClientFactory(handler));
        var profile = new InferenceProfile();
        var model = new ModelProfile { Id = "model" };

        await Task.WhenAll(service.StartAsync(profile, model), service.StartAsync(profile, model));

        Assert.Equal(1, runtime.StartCount);
    }

    [Fact]
    public async Task Preparation_restarts_if_the_runtime_was_stopped_externally()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":\"x\"}")
        });
        var runtime = new StubRuntimeManager();
        var service = new LlamaInferenceService(runtime, new StubHttpClientFactory(handler));
        var profile = new InferenceProfile();
        var model = new ModelProfile { Id = "model" };

        await service.StartAsync(profile, model);
        await runtime.StopAsync();
        await service.StartAsync(profile, model);

        Assert.Equal(2, runtime.StartCount);
    }

    [Fact]
    public async Task Warmup_failure_does_not_block_a_ready_runtime()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("warmup unavailable")
        });
        var service = new LlamaInferenceService(new StubRuntimeManager(), new StubHttpClientFactory(handler));

        await service.StartAsync(new InferenceProfile(), new ModelProfile { Id = "model" });

        Assert.Equal(RuntimeStatus.Ready, service.State.Status);
    }

    [Fact]
    public async Task Missing_server_metrics_uses_usage_and_elapsed_time()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"回答"}}]}

            data: {"choices":[],"usage":{"prompt_tokens":8,"completion_tokens":2,"total_tokens":10}}

            data: [DONE]

            """;
        var handler = new StubHttpHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri?.AbsolutePath == "/completion" ? "{\"content\":\"x\"}" : sse,
                Encoding.UTF8,
                request.RequestUri?.AbsolutePath == "/completion" ? "application/json" : "text/event-stream")
        });
        var service = new LlamaInferenceService(new StubRuntimeManager(), new StubHttpClientFactory(handler));
        await service.StartAsync(new InferenceProfile(), new ModelProfile { Id = "model" });

        var updates = await CollectAsync(service.StreamCompletionAsync(
            new InferenceRequest("model", [], [], 0, 16)));

        Assert.True(updates.Last().Timing?.PromptTokensPerSecond > 0);
        Assert.True(updates.Last().Timing?.GeneratedTokensPerSecond > 0);
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source) result.Add(item);
        return result;
    }
}

public sealed class AgentChatServiceTests : IDisposable
{
    private readonly TestPaths _paths = new();

    [Fact]
    public async Task Denied_tool_is_recorded_but_never_executed()
    {
        var store = new SqliteConversationStore(_paths);
        var conversation = await store.CreateAsync("new");
        var inference = new ToolThenAnswerInference();
        var mcp = new RecordingMcpManager();
        var agent = new AgentChatService(store, inference, mcp, new AskApprovalService(), new DenyPrompt());

        var events = new List<AgentEvent>();
        await foreach (var item in agent.RunTurnAsync(
            conversation.Id, "天気を確認", new InferenceProfile(), new ModelProfile { Id = "gemma" }))
            events.Add(item);

        Assert.Equal(0, mcp.CallCount);
        Assert.Contains(events, item => item.Kind == AgentEventKind.ToolApprovalRequired);
        var messages = await store.GetMessagesAsync(conversation.Id);
        Assert.Contains(messages, item => item.Role == ChatRole.Tool && item.IsError && item.Content.Contains("拒否"));
        Assert.Contains(messages, item => item.Role == ChatRole.Assistant && item.Content == "確認できませんでした。");
        Assert.NotNull(events.Single(item => item.Kind == AgentEventKind.Completed).Performance);
    }

    [Fact]
    public async Task Cancellation_persists_the_visible_partial_answer()
    {
        var store = new SqliteConversationStore(_paths);
        var conversation = await store.CreateAsync("new");
        var agent = new AgentChatService(store, new CancellableInference(), new RecordingMcpManager(), new AskApprovalService(), new DenyPrompt());
        using var cancellation = new CancellationTokenSource();
        await using var events = agent.RunTurnAsync(
            conversation.Id, "長い回答", new InferenceProfile(), new ModelProfile { Id = "gemma" }, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("途中", events.Current.Text);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await events.MoveNextAsync().AsTask());

        var messages = await store.GetMessagesAsync(conversation.Id);
        Assert.Contains(messages, message => message.Role == ChatRole.Assistant && message.Content == "途中");
    }

    public void Dispose() => _paths.Dispose();
}

internal sealed class StubRuntimeManager : IInferenceRuntimeManager
{
    public int StartCount { get; private set; }
    public RuntimeState State { get; private set; } = RuntimeState.Stopped;
    public event EventHandler<RuntimeState>? StateChanged;

    public Task RecoverOwnedProcessAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<RuntimeState> StartAsync(InferenceProfile profile, ModelProfile model, CancellationToken cancellationToken = default)
    {
        StartCount++;
        State = new RuntimeState(RuntimeStatus.Ready, RuntimeBackend.Cpu, new Uri("http://127.0.0.1:12345/"), "model.gguf", AuthenticationToken: "runtime-key");
        StateChanged?.Invoke(this, State);
        return Task.FromResult(State);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        State = RuntimeState.Stopped;
        return Task.CompletedTask;
    }

    public Task<HardwareCapabilities> DetectHardwareAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new HardwareCapabilities(false, false, "CPU"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class CancellableInference : IInferenceService
{
    public RuntimeState State { get; } = new(RuntimeStatus.Ready, RuntimeBackend.Cpu);
    public Task StartAsync(InferenceProfile profile, ModelProfile model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public async IAsyncEnumerable<InferenceUpdate> StreamCompletionAsync(
        InferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new InferenceUpdate(TextDelta: "途中");
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(handler(request));
}

internal sealed class ToolThenAnswerInference : IInferenceService
{
    private int _round;
    public RuntimeState State { get; } = new(RuntimeStatus.Ready);
    public Task StartAsync(InferenceProfile profile, ModelProfile model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public async IAsyncEnumerable<InferenceUpdate> StreamCompletionAsync(
        InferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        if (_round++ == 0)
            yield return new InferenceUpdate(ToolCalls: [new ToolCallRequest("call-1", "weather__forecast", "{\"city\":\"東京\"}")]);
        else
            yield return new InferenceUpdate(TextDelta: "確認できませんでした。");
        yield return new InferenceUpdate(IsCompleted: true);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingMcpManager : IMcpConnectionManager
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","required":["city"],"properties":{"city":{"type":"string"}}}
        """).RootElement.Clone();

    public int CallCount { get; private set; }
    public event EventHandler<McpConnectionInfo>? ConnectionChanged { add { } remove { } }
    public Task<McpConnectionInfo> ConnectAsync(McpServerProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<McpConnectionInfo> TestAsync(McpServerProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DisconnectAsync(string serverId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<McpConnectionInfo>> GetConnectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<McpConnectionInfo>>([]);
    public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ToolDefinition>>([new("weather__forecast", "weather", "forecast", "forecast", Schema)]);
    public Task<McpToolResult> CallToolAsync(ToolCallRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new McpToolResult(request.Id, request.Name, "should not happen"));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class AskApprovalService : IToolApprovalService
{
    public ApprovalDecision Evaluate(string serverId, string toolName) => ApprovalDecision.Ask;
    public Task RememberAsync(string serverId, string toolName, ApprovalDecision decision, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class DenyPrompt : IToolApprovalPrompt
{
    public Task<ApprovalResponse> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(ApprovalResponse.Deny);
}
