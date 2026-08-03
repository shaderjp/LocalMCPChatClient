using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.Infrastructure;

public sealed class LlamaInferenceService(IInferenceRuntimeManager runtimeManager, IHttpClientFactory httpClientFactory) : IInferenceService
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private RuntimeState _state = RuntimeState.Stopped;
    private InferenceProfile? _preparedProfile;
    private string? _preparedModelId;
    public RuntimeState State => _state;

    public async Task StartAsync(InferenceProfile profile, ModelProfile model, CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Status == RuntimeStatus.Ready &&
                runtimeManager.State.Status == RuntimeStatus.Ready &&
                Equals(_preparedProfile, profile) &&
                _preparedModelId == model.Id)
                return;

            _state = await runtimeManager.StartAsync(profile, model, cancellationToken).ConfigureAwait(false);
            _state = _state with { Status = RuntimeStatus.WarmingUp };
            try { await WarmUpCoreAsync(cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException) { /* Warm-up is an optimization; the chat endpoint may still be usable. */ }
            _state = _state with { Status = RuntimeStatus.Ready };
            _preparedProfile = profile;
            _preparedModelId = model.Id;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await runtimeManager.StopAsync(cancellationToken).ConfigureAwait(false);
            _state = RuntimeState.Stopped;
            _preparedProfile = null;
            _preparedModelId = null;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        if (_state.Status != RuntimeStatus.Ready || _state.Endpoint is null) return false;
        using var client = CreateClient();
        using var response = await client.GetAsync("health", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async IAsyncEnumerable<InferenceUpdate> StreamCompletionAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_state.Status != RuntimeStatus.Ready || _state.Endpoint is null)
            throw new InvalidOperationException("推論ランタイムが起動していません。");

        using var client = CreateClient();
        var requestTimer = Stopwatch.StartNew();
        double firstTokenMilliseconds = 0;
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(CreateRequestBody(request))
        };
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"llama-serverが {(int)response.StatusCode} を返しました: {Limit(error, 16_384)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var toolCalls = new SortedDictionary<int, MutableToolCall>();
        InferenceUsage? usage = null;
        double serverPromptRate = 0;
        double serverGenerationRate = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].TrimStart();
            if (payload == "[DONE]")
            {
                break;
            }
            if (payload.Length == 0) continue;

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = new InferenceUsage(
                    GetInt(usageElement, "prompt_tokens"), GetInt(usageElement, "completion_tokens"), GetInt(usageElement, "total_tokens"));
            }
            if (root.TryGetProperty("timings", out var timingsElement) && timingsElement.ValueKind == JsonValueKind.Object)
            {
                serverPromptRate = GetDouble(timingsElement, "prompt_per_second");
                serverGenerationRate = GetDouble(timingsElement, "predicted_per_second");
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) continue;
            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta)) continue;
                if (delta.TryGetProperty("reasoning_content", out var reasoning) &&
                    reasoning.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(reasoning.GetString()) &&
                    firstTokenMilliseconds == 0)
                    firstTokenMilliseconds = requestTimer.Elapsed.TotalMilliseconds;
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (firstTokenMilliseconds == 0) firstTokenMilliseconds = requestTimer.Elapsed.TotalMilliseconds;
                        yield return new InferenceUpdate(TextDelta: text);
                    }
                }
                if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    if (firstTokenMilliseconds == 0) firstTokenMilliseconds = requestTimer.Elapsed.TotalMilliseconds;
                    foreach (var call in calls.EnumerateArray())
                    {
                        var index = GetInt(call, "index");
                        if (!toolCalls.TryGetValue(index, out var aggregate)) toolCalls[index] = aggregate = new MutableToolCall();
                        if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) aggregate.Id = id.GetString() ?? aggregate.Id;
                        if (call.TryGetProperty("function", out var function))
                        {
                            if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                                aggregate.Name.Append(name.GetString());
                            if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
                                aggregate.Arguments.Append(arguments.GetString());
                        }
                    }
                }
            }
        }

        var completedCalls = toolCalls.Values
            .Select(call => new ToolCallRequest(
                string.IsNullOrWhiteSpace(call.Id) ? "call_" + Guid.NewGuid().ToString("N") : call.Id,
                call.Name.ToString(),
                string.IsNullOrWhiteSpace(call.Arguments.ToString()) ? "{}" : call.Arguments.ToString()))
            .ToList();
        if (completedCalls.Count > 0) yield return new InferenceUpdate(ToolCalls: completedCalls);
        requestTimer.Stop();
        var totalMilliseconds = requestTimer.Elapsed.TotalMilliseconds;
        var generationMilliseconds = Math.Max(1, totalMilliseconds - firstTokenMilliseconds);
        var promptRate = serverPromptRate > 0 ? serverPromptRate : usage is { PromptTokens: > 0 } && firstTokenMilliseconds > 0
            ? usage.PromptTokens * 1000d / firstTokenMilliseconds
            : 0;
        var generationRate = serverGenerationRate > 0 ? serverGenerationRate : usage is { CompletionTokens: > 0 }
            ? usage.CompletionTokens * 1000d / generationMilliseconds
            : 0;
        yield return new InferenceUpdate(
            IsCompleted: true,
            Usage: usage,
            Timing: new InferenceTiming(totalMilliseconds, firstTokenMilliseconds, promptRate, generationRate));
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = _state.Endpoint;
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _state.AuthenticationToken);
        return client;
    }

    private async Task WarmUpCoreAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var response = await client.PostAsJsonAsync("completion", new
        {
            prompt = " ",
            n_predict = 1,
            temperature = 0,
            cache_prompt = true
        }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"llama-serverのウォームアップに失敗しました: {Limit(error, 4096)}");
        }
    }

    private static JsonObject CreateRequestBody(InferenceRequest request)
    {
        var messages = new JsonArray();
        foreach (var item in request.Messages)
        {
            var message = new JsonObject
            {
                ["role"] = item.Role.ToString().ToLowerInvariant(),
                ["content"] = item.Content
            };
            if (item.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(item.ToolCallsJson))
                message["tool_calls"] = JsonNode.Parse(item.ToolCallsJson);
            if (item.Role == ChatRole.Tool)
            {
                message["tool_call_id"] = item.ToolCallId;
                if (!string.IsNullOrWhiteSpace(item.ToolName)) message["name"] = item.ToolName;
            }
            messages.Add(message);
        }

        var tools = new JsonArray();
        foreach (var tool in request.Tools)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.NamespacedName,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.InputSchema.GetRawText())
                }
            });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["parallel_tool_calls"] = false,
            ["cache_prompt"] = true,
            ["reasoning_effort"] = "none",
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false }
        };
        if (tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }
        return body;
    }

    private static int GetInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static double GetDouble(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetDouble(out var result) ? result : 0;

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + "…";

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }

    private sealed class MutableToolCall
    {
        public string Id { get; set; } = string.Empty;
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }
}
