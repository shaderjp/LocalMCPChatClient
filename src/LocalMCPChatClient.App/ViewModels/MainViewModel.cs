using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.App.ViewModels;

public sealed partial class MainViewModel(
    IConversationStore conversationStore,
    IConversationExporter conversationExporter,
    ISettingsStore settingsStore,
    IAgentChatService agentChatService,
    IInferenceService inferenceService,
    IMcpConnectionManager mcpManager,
    IInferenceRuntimeManager runtimeManager,
    IArtifactStore artifactStore) : ObservableObject
{
    private AppSettings _settings = new();
    private CancellationTokenSource? _turnCancellation;
    private CancellationTokenSource? _preloadCancellation;
    private bool _isInitializing;

    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];
    public ObservableCollection<ChatItemViewModel> Messages { get; } = [];
    public ObservableCollection<ModelProfile> Models { get; } = [];
    public ObservableCollection<PendingResourceViewModel> PendingResources { get; } = [];
    public IReadOnlyList<InferenceMode> InferenceModes { get; } = Enum.GetValues<InferenceMode>();
    public IReadOnlyList<string> ReviewPresets { get; } = ["quick", "material", "lighting", "temporal"];
    public event EventHandler? SettingsRequested;

    [ObservableProperty] private ConversationItemViewModel? _selectedConversation;
    [ObservableProperty] private ModelProfile? _selectedModel;
    [ObservableProperty] private InferenceMode _selectedInferenceMode = InferenceMode.Auto;
    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _statusText = "初期化中…";
    [ObservableProperty] private string _mcpStatusText = "MCP: 未接続";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLookDevAvailable;
    [ObservableProperty] private string _selectedReviewPreset = "quick";

    public async Task InitializeAsync()
    {
        _isInitializing = true;
        _settings = await settingsStore.LoadAsync();
        Models.Clear();
        foreach (var model in _settings.Models) Models.Add(model);
        SelectedModel = Models.FirstOrDefault(model => model.Id == _settings.SelectedModelId) ?? Models.FirstOrDefault();
        SelectedInferenceMode = _settings.InferenceMode;
        await ReloadConversationsAsync();
        if (Conversations.Count == 0) await NewChatAsync();
        else SelectedConversation = Conversations[0];

        var hardware = await runtimeManager.DetectHardwareAsync();
        StatusText = _settings.SetupCompleted ? hardware.Summary : "初回設定が完了していません。設定を開いてください。";
        _isInitializing = false;
    }

    public async Task StartBackgroundInitializationAsync()
    {
        _preloadCancellation?.Cancel();
        _preloadCancellation?.Dispose();
        _preloadCancellation = new CancellationTokenSource();
        var token = _preloadCancellation.Token;
        var connections = _settings.McpServers.Where(server => server.Enabled)
            .Select(server => mcpManager.ConnectAsync(server, token))
            .ToArray();
        var preload = PreloadSelectedModelAsync(token);
        try
        {
            await Task.WhenAll(connections.Cast<Task>().Append(preload));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusText = "バックグラウンド初期化の一部に失敗しました: " + exception.Message;
        }
        finally
        {
            await RefreshMcpStatusAsync();
        }
    }

    [RelayCommand]
    private async Task NewChatAsync()
    {
        var conversation = await conversationStore.CreateAsync("新しいチャット", SelectedModel?.Id);
        await ReloadConversationsAsync();
        SelectedConversation = Conversations.First(item => item.Id == conversation.Id);
    }

    [RelayCommand]
    private async Task DeleteChatAsync()
    {
        if (SelectedConversation is null) return;
        if (MessageBox.Show("このチャットを削除しますか？", "Local MCP Chat", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await conversationStore.DeleteAsync(SelectedConversation.Id);
        await ReloadConversationsAsync();
        if (Conversations.Count == 0) await NewChatAsync();
        else SelectedConversation = Conversations[0];
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (SelectedConversation is null || SelectedModel is null ||
            (string.IsNullOrWhiteSpace(InputText) && PendingResources.Count == 0)) return;
        var input = new UserTurnInput
        {
            Text = InputText.Trim(),
            ResourceReferences = PendingResources.Select(item => item.Reference).ToList()
        };
        await RunInputAsync(input);
    }

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private async Task RegenerateAsync()
    {
        if (SelectedConversation is null) return;
        var conversationId = SelectedConversation.Id;
        var input = await conversationStore.DeleteLastTurnAsync(conversationId);
        if (input is null) return;
        await LoadMessagesAsync(conversationId);
        await RunInputAsync(input);
    }

    private async Task RunInputAsync(UserTurnInput input)
    {
        if (SelectedConversation is null || SelectedModel is null) return;
        IsBusy = true;
        SendCommand.NotifyCanExecuteChanged();
        RegenerateCommand.NotifyCanExecuteChanged();
        _turnCancellation = new CancellationTokenSource();
        ChatItemViewModel? assistant = null;
        var userMessageStored = false;
        var assistantBuffer = new StreamingTextBuffer();
        var renderTimer = Stopwatch.StartNew();
        TurnPerformance? performance = null;
        StatusText = input.ResourceReferences.Count > 0 ? "Resourceを読み取り中…" : "送信中…";

        try
        {
            var profile = new InferenceProfile
            {
                ModelId = SelectedModel.Id,
                Mode = SelectedInferenceMode,
                ContextSize = _settings.ContextSize,
                MaxOutputTokens = _settings.MaxOutputTokens,
                Temperature = _settings.Temperature,
                CustomRuntimePath = _settings.CustomRuntimePath,
                EnableVision = _settings.EnableVision,
                ImageTokenBudget = _settings.ImageTokenBudget
            };
            await foreach (var item in agentChatService.RunTurnAsync(
                SelectedConversation.Id, input, profile, SelectedModel, _turnCancellation.Token))
            {
                switch (item.Kind)
                {
                    case AgentEventKind.UserMessageStored:
                        userMessageStored = true;
                        InputText = string.Empty;
                        PendingResources.Clear();
                        SendCommand.NotifyCanExecuteChanged();
                        Messages.Add(new ChatItemViewModel(ChatRole.User, input.Text, resources: item.ResourceSnapshots, artifactStore: artifactStore));
                        assistant = new ChatItemViewModel(ChatRole.Assistant, string.Empty) { IsStreaming = true };
                        Messages.Add(assistant);
                        StatusText = "生成を開始しています…";
                        break;
                    case AgentEventKind.TextDelta:
                        if (assistantBuffer.Append(item.Text, renderTimer.ElapsedMilliseconds))
                        {
                            if (assistant is not null) assistant.Content = assistantBuffer.Content;
                            renderTimer.Restart();
                        }
                        StatusText = "生成中…";
                        break;
                    case AgentEventKind.ToolApprovalRequired:
                        FlushAssistant();
                        StatusText = "ツール実行の承認を待っています";
                        break;
                    case AgentEventKind.ToolStarted:
                        FlushAssistant();
                        StatusText = $"ツール実行中: {item.ToolCall?.Name}";
                        break;
                    case AgentEventKind.ToolCompleted:
                        FlushAssistant();
                        Messages.Add(new ChatItemViewModel(
                            ChatRole.Tool,
                            FormatToolCard(item.ToolCall?.ArgumentsJson, item.ToolResult?.Content, item.ToolResult?.IsError == true),
                            item.ToolResult?.IsError ?? false,
                            item.ToolCall?.Name,
                            contentParts: item.ToolResult?.Parts,
                            artifactStore: artifactStore));
                        break;
                    case AgentEventKind.Warning:
                        Messages.Add(new ChatItemViewModel(ChatRole.System, item.Text ?? string.Empty, true));
                        break;
                    case AgentEventKind.Completed:
                        performance = item.Performance;
                        break;
                }
            }
            if (assistant is not null)
            {
                assistant.Content = assistantBuffer.Content;
                assistant.IsStreaming = false;
            }
            StatusText = performance is null
                ? $"完了 · {FormatBackend(runtimeManager.State.Backend)}"
                : $"完了 · {FormatBackend(performance.Backend)} · 初回 {performance.TimeToFirstTokenMilliseconds / 1000d:F2}秒 · {performance.GeneratedTokensPerSecond:F1} tok/s";
            var selectedId = SelectedConversation.Id;
            await ReloadConversationsAsync(selectedId);
            await LoadMessagesAsync(selectedId);
        }
        catch (OperationCanceledException)
        {
            if (assistant is not null)
            {
                assistant.Content = assistantBuffer.Content;
                assistant.IsStreaming = false;
            }
            StatusText = "生成を停止しました";
        }
        catch (Exception exception)
        {
            var retryHint = SelectedInferenceMode is InferenceMode.Cuda or InferenceMode.Vulkan
                ? "\n設定でCPU推論を選択して再試行できます。"
                : string.Empty;
            StatusText = "エラー: " + exception.Message;
            if (userMessageStored && assistant is not null)
            {
                if (assistantBuffer.Length > 0) assistantBuffer.AppendLine().AppendLine();
                assistantBuffer.Append("エラー: ").Append(exception.Message).Append(retryHint);
                assistant.Content = assistantBuffer.Content;
                assistant.IsStreaming = false;
            }
        }
        finally
        {
            _turnCancellation.Dispose();
            _turnCancellation = null;
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
            RegenerateCommand.NotifyCanExecuteChanged();
        }

        void FlushAssistant()
        {
            if (assistant is not null && assistant.Content != assistantBuffer.Content) assistant.Content = assistantBuffer.Content;
            renderTimer.Restart();
        }
    }

    private bool CanSend() => !IsBusy && SelectedConversation is not null && SelectedModel is not null &&
        (!string.IsNullOrWhiteSpace(InputText) || PendingResources.Count > 0);
    private bool CanRegenerate() => !IsBusy && SelectedConversation is not null && Messages.Any(message => message.Role == ChatRole.User);

    [RelayCommand]
    private void Stop() => _turnCancellation?.Cancel();

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanReviewScene))]
    private async Task ReviewSceneAsync()
    {
        if (!CanReviewScene()) return;
        IsBusy = true;
        SendCommand.NotifyCanExecuteChanged();
        ReviewSceneCommand.NotifyCanExecuteChanged();
        _turnCancellation = new CancellationTokenSource();
        UserTurnInput? reviewInput = null;
        try
        {
            var token = _turnCancellation.Token;
            StatusText = "LookDev監査を実行中…";
            var audit = await CallLookDevToolAsync("lookdevpt.audit_scene", "{}", token);
            StatusText = "現在のviewportをcapture中…";
            var initialCapture = await CallLookDevToolAsync("lookdevpt.capture_viewport", "{\"label\":\"LocalMCPChatClient baseline\"}", token);
            StatusText = $"{SelectedReviewPreset}レビューを開始中…";
            var started = await CallLookDevToolAsync("lookdevpt.start_review",
                JsonSerializer.Serialize(new { preset = SelectedReviewPreset }), token);
            var reviewId = FindJsonString(started, "reviewId")
                ?? throw new InvalidOperationException("start_reviewがreviewIdを返しませんでした。");

            var connection = (await mcpManager.GetConnectionsAsync(token))
                .First(item => !string.IsNullOrWhiteSpace(item.LookDevContractVersion));
            var reviewReference = new McpResourceReference(connection.ServerId, connection.DisplayName,
                $"lookdevpt://reviews/{reviewId}", $"LookDev review {reviewId}", "application/json");
            IAsyncDisposable? subscription = null;
            if (connection.SupportsSubscriptions)
            {
                try { subscription = await mcpManager.SubscribeToResourceAsync(reviewReference, token); }
                catch (NotSupportedException) { }
            }

            McpToolResult review;
            try
            {
                while (true)
                {
                    review = await CallLookDevToolAsync("lookdevpt.get_review",
                        JsonSerializer.Serialize(new { reviewId }), token);
                    var state = FindJsonString(review, "state") ?? "running";
                    var progress = FindJsonNumber(review, "progress");
                    StatusText = $"LookDevレビュー: {state} {progress * 100:F0}%";
                    if (state is "completed" or "failed" or "cancelled") break;
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
            finally
            {
                if (subscription is not null) await subscription.DisposeAsync();
            }

            var snapshots = new List<McpResourceSnapshot>();
            var resourceUris = ExtractResourceUris(initialCapture)
                .Concat(ExtractResourceUris(review))
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(uri => uri.EndsWith("heatmap.png", StringComparison.OrdinalIgnoreCase))
                .ThenBy(uri => uri, StringComparer.Ordinal)
                .ToList();
            var comparison = resourceUris.FirstOrDefault(uri => uri.StartsWith("lookdevpt://comparisons/", StringComparison.Ordinal) && !uri.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            if (comparison is not null) resourceUris.Insert(0, comparison + "/heatmap.png");
            foreach (var uri in resourceUris.Distinct(StringComparer.Ordinal).Take(12))
            {
                try
                {
                    var mimeType = uri.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "application/json";
                    var snapshot = await mcpManager.ReadResourceAsync(new McpResourceReference(
                        connection.ServerId, connection.DisplayName, uri, uri[(uri.LastIndexOf('/') + 1)..], mimeType), token);
                    snapshots.Add(snapshot);
                    if (snapshots.SelectMany(item => item.Parts ?? []).Count(part => part.Kind == McpContentKind.Image) >= 4) break;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Messages.Add(new ChatItemViewModel(ChatRole.System, $"Resource取得を省略しました: {uri}\n{exception.Message}", true));
                }
            }

            var visionNotice = _settings.EnableVision && SelectedModel?.VisionProjectorPath is not null
                ? "主要画像も見て、画像とmetricの両方を根拠にしてください。"
                : "画像入力は無効です。モデルは画像を見ていないため、監査・比較metricだけを根拠にし、その制約を明記してください。";
            reviewInput = new UserTurnInput
            {
                Text = $"""
                    D3D12 LookDevの非破壊レビューが完了しました。追加のツール呼び出しや設定変更はせず、以下の結果と添付artifactを解析してください。
                    {visionNotice}

                    出力は「概要」「重大度別の問題」「画像またはmetricによる根拠」「改善候補」「不確実点」の順にしてください。

                    audit_scene:
                    {audit.Content}

                    start_review:
                    {started.Content}

                    get_review:
                    {review.Content}
                    """,
                ResourceSnapshots = snapshots
            };
        }
        catch (OperationCanceledException)
        {
            StatusText = "LookDevレビューを停止しました";
        }
        catch (Exception exception)
        {
            StatusText = "LookDevレビューに失敗しました: " + exception.Message;
            Messages.Add(new ChatItemViewModel(ChatRole.System, StatusText, true));
        }
        finally
        {
            _turnCancellation?.Dispose();
            _turnCancellation = null;
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
            ReviewSceneCommand.NotifyCanExecuteChanged();
        }
        if (reviewInput is not null) await RunInputAsync(reviewInput);
    }

    private bool CanReviewScene() => !IsBusy && IsLookDevAvailable && SelectedConversation is not null && SelectedModel is not null;

    [RelayCommand(CanExecute = nameof(CanSafeApply))]
    private async Task SafeApplyAsync()
    {
        if (!CanSafeApply()) return;
        IsBusy = true;
        NotifyWorkflowCommands();
        _turnCancellation = new CancellationTokenSource();
        string? checkpointId = null;
        var applied = false;
        UserTurnInput? resultInput = null;
        try
        {
            using var document = JsonDocument.Parse(InputText);
            var actions = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.Clone()
                : document.RootElement.TryGetProperty("actions", out var configuredActions) && configuredActions.ValueKind == JsonValueKind.Array
                    ? configuredActions.Clone()
                    : throw new InvalidOperationException("入力欄へactions配列、または {\"actions\":[...]} を指定してください。");
            if (actions.GetArrayLength() is < 1 or > 16)
                throw new InvalidOperationException("安全な変更セッションは1～16 actionsです。");

            var token = _turnCancellation.Token;
            StatusText = "変更前のviewportをcapture中…";
            var before = await CallLookDevToolAsync("lookdevpt.capture_viewport",
                "{\"label\":\"safe-change baseline\"}", token);
            var beforeId = FindJsonId(before, "captureId")
                ?? throw new InvalidOperationException("baseline capture idが返されませんでした。");
            var checkpoint = await CallLookDevToolAsync("lookdevpt.create_checkpoint",
                "{\"label\":\"LocalMCPChatClient safe change\"}", token);
            checkpointId = FindJsonId(checkpoint, "checkpointId")
                ?? throw new InvalidOperationException("checkpoint idが返されませんでした。");

            StatusText = "actionsを非破壊検証中…";
            var validationPayload = JsonSerializer.Serialize(new { actions, validateOnly = true, stopOnError = true });
            var validation = await CallLookDevToolAsync("lookdevpt.run_actions", validationPayload, token);
            if (MessageBox.Show(
                    $"全actionsの検証に成功しました。\n\n{validation.Content}\n\n一括適用しますか？",
                    "LookDev 安全な変更", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                await CallLookDevToolAsync("lookdevpt.delete_checkpoint",
                    JsonSerializer.Serialize(new { checkpointId }), token);
                checkpointId = null;
                StatusText = "変更は適用されませんでした";
                return;
            }

            StatusText = "承認済みactionsを一括適用中…";
            var applyPayload = JsonSerializer.Serialize(new { actions, validateOnly = false, stopOnError = true });
            var application = await CallLookDevToolAsync("lookdevpt.run_actions", applyPayload, token);
            applied = true;
            StatusText = "変更後のviewportをcapture中…";
            var after = await CallLookDevToolAsync("lookdevpt.capture_viewport",
                "{\"label\":\"safe-change after\"}", token);
            var afterId = FindJsonId(after, "captureId")
                ?? throw new InvalidOperationException("after capture idが返されませんでした。");
            var comparison = await CallLookDevToolAsync("lookdevpt.compare_captures",
                JsonSerializer.Serialize(new { beforeCaptureId = beforeId, afterCaptureId = afterId }), token);
            var comparisonId = FindJsonId(comparison, "id");

            var connection = (await mcpManager.GetConnectionsAsync(token))
                .First(item => !string.IsNullOrWhiteSpace(item.LookDevContractVersion));
            var snapshots = new List<McpResourceSnapshot>();
            var resourceUris = new List<string>
            {
                $"lookdevpt://captures/{beforeId}.png",
                $"lookdevpt://captures/{afterId}.png"
            };
            if (comparisonId is not null)
            {
                resourceUris.Add($"lookdevpt://comparisons/{comparisonId}");
                resourceUris.Add($"lookdevpt://comparisons/{comparisonId}/heatmap.png");
            }
            foreach (var uri in resourceUris)
            {
                var mimeType = uri.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "application/json";
                snapshots.Add(await mcpManager.ReadResourceAsync(new McpResourceReference(
                    connection.ServerId, connection.DisplayName, uri, uri[(uri.LastIndexOf('/') + 1)..], mimeType), token));
            }

            var adopted = MessageBox.Show(
                "変更前後のcaptureとheatmapを取得しました。変更を採用しますか？\n\n「いいえ」でcheckpointへ直ちに復元します。",
                "LookDev 変更結果", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!adopted)
            {
                StatusText = "checkpointから復元中…";
                await CallLookDevToolAsync("lookdevpt.restore_checkpoint", JsonSerializer.Serialize(new { checkpointId }), token);
            }
            await CallLookDevToolAsync("lookdevpt.delete_checkpoint", JsonSerializer.Serialize(new { checkpointId }), token);
            checkpointId = null;
            InputText = string.Empty;
            resultInput = new UserTurnInput
            {
                Text = $"""
                    D3D12 LookDevの安全な変更セッションを解析してください。変更は{(adopted ? "採用されました" : "比較後にcheckpointへ復元されました")}。
                    変更前後画像、heatmap、comparison metricを根拠に、設定差分の効果、悪化の有無、次の改善候補、不確実点を簡潔にまとめてください。追加の変更ツールは呼ばないでください。

                    validation:
                    {validation.Content}

                    application:
                    {application.Content}

                    comparison:
                    {comparison.Content}
                    """,
                ResourceSnapshots = snapshots
            };
        }
        catch (OperationCanceledException)
        {
            StatusText = "安全な変更セッションを停止しました";
        }
        catch (Exception exception)
        {
            StatusText = "安全な変更セッションに失敗しました: " + exception.Message;
            Messages.Add(new ChatItemViewModel(ChatRole.System, StatusText, true));
        }
        finally
        {
            if (checkpointId is not null)
            {
                try
                {
                    if (applied)
                        await CallLookDevToolAsync("lookdevpt.restore_checkpoint",
                            JsonSerializer.Serialize(new { checkpointId }), CancellationToken.None);
                    await CallLookDevToolAsync("lookdevpt.delete_checkpoint",
                        JsonSerializer.Serialize(new { checkpointId }), CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    Messages.Add(new ChatItemViewModel(ChatRole.System,
                        "checkpointの自動復元／削除を完了できませんでした: " + cleanupException.Message, true));
                }
            }
            _turnCancellation?.Dispose();
            _turnCancellation = null;
            IsBusy = false;
            NotifyWorkflowCommands();
        }
        if (resultInput is not null) await RunInputAsync(resultInput);
    }

    private bool CanSafeApply() => CanReviewScene() && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanRunBenchmark))]
    private async Task RunBenchmarkAsync()
    {
        if (!CanRunBenchmark()) return;
        IsBusy = true;
        NotifyWorkflowCommands();
        _turnCancellation = new CancellationTokenSource();
        string? benchmarkId = null;
        var benchmarkTerminal = false;
        IAsyncDisposable? subscription = null;
        UserTurnInput? resultInput = null;
        try
        {
            using var document = JsonDocument.Parse(InputText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("benchmark設定をJSON objectで入力してください。");
            var configuration = document.RootElement.Clone();
            var compareWith = configuration.TryGetProperty("compareWithBenchmarkId", out var compareValue)
                ? compareValue.ValueKind == JsonValueKind.String ? compareValue.GetString() : compareValue.GetRawText()
                : null;
            var requestProperties = configuration.EnumerateObject()
                .Where(property => property.Name != "compareWithBenchmarkId")
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            var token = _turnCancellation.Token;
            StatusText = "非同期benchmarkを開始中…";
            var started = await CallLookDevToolAsync("lookdevpt.start_benchmark",
                JsonSerializer.Serialize(requestProperties), token);
            benchmarkId = FindJsonId(started, "benchmarkId")
                ?? throw new InvalidOperationException("benchmark idが返されませんでした。");
            var connection = (await mcpManager.GetConnectionsAsync(token))
                .First(item => !string.IsNullOrWhiteSpace(item.LookDevContractVersion));
            var reference = new McpResourceReference(connection.ServerId, connection.DisplayName,
                $"lookdevpt://benchmarks/{benchmarkId}", $"Benchmark {benchmarkId}", "application/json");
            if (connection.SupportsSubscriptions)
            {
                try { subscription = await mcpManager.SubscribeToResourceAsync(reference, token); }
                catch (NotSupportedException) { }
            }

            McpToolResult status;
            while (true)
            {
                status = await CallLookDevToolAsync("lookdevpt.get_benchmark",
                    JsonSerializer.Serialize(new { benchmarkId }), token);
                var state = FindJsonString(status, "state") ?? "running";
                StatusText = $"Benchmark {benchmarkId}: {state} {FindJsonNumber(status, "progress") * 100:F0}%";
                if (state is "completed" or "failed" or "cancelled")
                {
                    benchmarkTerminal = true;
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            if (subscription is not null) await subscription.DisposeAsync();
            subscription = null;
            var terminalState = FindJsonString(status, "state") ?? "unknown";
            if (terminalState != "completed")
                throw new InvalidOperationException($"Benchmarkは{terminalState}で終了しました: {status.Content}");

            var snapshots = new List<McpResourceSnapshot>();
            var uris = ExtractResourceUris(status).Distinct(StringComparer.Ordinal).ToList();
            if (!string.IsNullOrWhiteSpace(compareWith))
            {
                uris.Add($"lookdevpt://benchmarks/{compareWith}");
                uris.Add($"lookdevpt://benchmarks/{compareWith}/summary.json");
                uris.Add($"lookdevpt://benchmarks/{compareWith}/quality_analysis.json");
            }
            foreach (var uri in uris.Distinct(StringComparer.Ordinal).Take(10))
            {
                var mimeType = uri.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "text/csv" : "application/json";
                snapshots.Add(await mcpManager.ReadResourceAsync(new McpResourceReference(
                    connection.ServerId, connection.DisplayName, uri, uri[(uri.LastIndexOf('/') + 1)..], mimeType), token));
            }
            InputText = string.Empty;
            resultInput = new UserTurnInput
            {
                Text = $"""
                    D3D12 LookDev benchmark {benchmarkId} の結果を解析してください。
                    GPU timing、CPU timing、品質metric、設定／backend evidenceをまとめ、ボトルネックと次の測定案を示してください。
                    {(string.IsNullOrWhiteSpace(compareWith) ? "単独runとして評価してください。" : $"benchmark {compareWith} と比較し、改善率・悪化率と設定差分を明示してください。")}
                    推測と測定事実を分離してください。

                    terminal status:
                    {status.Content}
                    """,
                ResourceSnapshots = snapshots
            };
            benchmarkId = null;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Benchmarkを中止しています…";
        }
        catch (Exception exception)
        {
            StatusText = "Benchmarkに失敗しました: " + exception.Message;
            Messages.Add(new ChatItemViewModel(ChatRole.System, StatusText, true));
        }
        finally
        {
            if (subscription is not null) await subscription.DisposeAsync();
            if (benchmarkId is not null && !benchmarkTerminal)
            {
                try
                {
                    await CallLookDevToolAsync("lookdevpt.cancel_benchmark",
                        JsonSerializer.Serialize(new { benchmarkId }), CancellationToken.None);
                }
                catch (Exception cancelException)
                {
                    Messages.Add(new ChatItemViewModel(ChatRole.System,
                        "Benchmarkの中止要求を送信できませんでした: " + cancelException.Message, true));
                }
            }
            _turnCancellation?.Dispose();
            _turnCancellation = null;
            IsBusy = false;
            NotifyWorkflowCommands();
        }
        if (resultInput is not null) await RunInputAsync(resultInput);
    }

    private bool CanRunBenchmark() => CanReviewScene() && !string.IsNullOrWhiteSpace(InputText);

    private void NotifyWorkflowCommands()
    {
        SendCommand.NotifyCanExecuteChanged();
        RegenerateCommand.NotifyCanExecuteChanged();
        ReviewSceneCommand.NotifyCanExecuteChanged();
        SafeApplyCommand.NotifyCanExecuteChanged();
        RunBenchmarkCommand.NotifyCanExecuteChanged();
    }

    private async Task<McpToolResult> CallLookDevToolAsync(string originalName, string argumentsJson, CancellationToken cancellationToken)
    {
        var tool = (await mcpManager.GetToolsAsync(cancellationToken)).FirstOrDefault(item => item.OriginalName == originalName)
            ?? throw new InvalidOperationException($"{originalName}が接続中のLookDevサーバーにありません。");
        var result = await mcpManager.CallToolAsync(new ToolCallRequest(Guid.NewGuid().ToString("N"), tool.NamespacedName, argumentsJson), cancellationToken);
        if (result.IsError) throw new InvalidOperationException($"{originalName}: {result.Content}");
        return result;
    }

    private static JsonElement? StructuredJson(McpToolResult result)
    {
        var json = result.Parts?.FirstOrDefault(part => part.Kind == McpContentKind.StructuredJson)?.Text;
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static string? FindJsonString(McpToolResult result, string property)
        => StructuredJson(result) is { } root && TryFindProperty(root, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? FindJsonId(McpToolResult result, string property)
    {
        if (StructuredJson(result) is not { } root || !TryFindProperty(root, property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static double FindJsonNumber(McpToolResult result, string property)
        => StructuredJson(result) is { } root && TryFindProperty(root, property, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private static IEnumerable<string> ExtractResourceUris(McpToolResult result)
    {
        foreach (var part in result.Parts ?? [])
            if (part.Kind == McpContentKind.ResourceLink && part.Uri?.StartsWith("lookdevpt://", StringComparison.Ordinal) == true) yield return part.Uri;
        if (StructuredJson(result) is not { } root) yield break;
        foreach (var uri in FindUris(root)) yield return uri;
    }

    private static IEnumerable<string> FindUris(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } stringValue && stringValue.StartsWith("lookdevpt://", StringComparison.Ordinal)) yield return stringValue;
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) foreach (var uri in FindUris(child)) yield return uri;
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject()) foreach (var uri in FindUris(property.Value)) yield return uri;
    }

    private static bool TryFindProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value)) return true;
            foreach (var property in element.EnumerateObject()) if (TryFindProperty(property.Value, name, out value)) return true;
        }
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) if (TryFindProperty(child, name, out value)) return true;
        value = default;
        return false;
    }

    [RelayCommand]
    private void RemovePendingResource(PendingResourceViewModel? resource)
    {
        if (resource is null) return;
        PendingResources.Remove(resource);
        SendCommand.NotifyCanExecuteChanged();
    }

    public void AddPendingResources(IEnumerable<McpResourceDefinition> resources)
    {
        foreach (var definition in resources)
        {
            if (PendingResources.Any(item => item.Reference.ServerId == definition.ServerId && item.Reference.Uri == definition.Uri)) continue;
            PendingResources.Add(new PendingResourceViewModel(McpResourceReference.FromDefinition(definition)));
        }
        SendCommand.NotifyCanExecuteChanged();
    }

    public async Task ExportSelectedConversationAsync(string destinationPath)
    {
        if (SelectedConversation is null) throw new InvalidOperationException("保存するチャットを選択してください。");
        await conversationExporter.ExportMarkdownAsync(SelectedConversation.Id, destinationPath);
        StatusText = "Markdownを保存しました: " + destinationPath;
    }

    partial void OnInputTextChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SafeApplyCommand.NotifyCanExecuteChanged();
        RunBenchmarkCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedConversationChanged(ConversationItemViewModel? value)
    {
        if (value is not null) _ = LoadMessagesAsync(value.Id);
    }

    partial void OnSelectedModelChanged(ModelProfile? value)
    {
        if (!_isInitializing && value is not null && _settings.Models.Count > 0)
            _ = SaveSelectionAndPreloadAsync(value.Id, SelectedInferenceMode);
    }

    partial void OnSelectedInferenceModeChanged(InferenceMode value)
    {
        if (!_isInitializing && _settings.Models.Count > 0)
            _ = SaveSelectionAndPreloadAsync(SelectedModel?.Id, value);
    }

    private async Task SaveSelectionAsync(string? modelId, InferenceMode mode)
        => _settings = await settingsStore.UpdateAsync(settings => settings with { SelectedModelId = modelId, InferenceMode = mode });

    private async Task SaveSelectionAndPreloadAsync(string? modelId, InferenceMode mode)
    {
        await SaveSelectionAsync(modelId, mode);
        _preloadCancellation?.Cancel();
        _preloadCancellation?.Dispose();
        _preloadCancellation = new CancellationTokenSource();
        await PreloadSelectedModelAsync(_preloadCancellation.Token);
    }

    private async Task PreloadSelectedModelAsync(CancellationToken cancellationToken)
    {
        if (!_settings.SetupCompleted || !_settings.PreloadModel || SelectedModel is null) return;
        try
        {
            StatusText = "モデルを準備中…";
            await inferenceService.StartAsync(CreateProfile(), SelectedModel, cancellationToken);
            StatusText = $"準備完了 · {FormatBackend(inferenceService.State.Backend)}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusText = "モデルの事前準備に失敗しました: " + exception.Message;
        }
    }

    private InferenceProfile CreateProfile() => new()
    {
        ModelId = SelectedModel?.Id ?? string.Empty,
        Mode = SelectedInferenceMode,
        ContextSize = _settings.ContextSize,
        MaxOutputTokens = _settings.MaxOutputTokens,
        Temperature = _settings.Temperature,
        CustomRuntimePath = _settings.CustomRuntimePath,
        EnableVision = _settings.EnableVision,
        ImageTokenBudget = _settings.ImageTokenBudget
    };

    private async Task ReloadConversationsAsync(Guid? keepSelected = null)
    {
        var items = await conversationStore.ListAsync();
        Conversations.Clear();
        foreach (var item in items) Conversations.Add(new ConversationItemViewModel(item.Id, item.Title, item.UpdatedAt));
        if (keepSelected is { } id) SelectedConversation = Conversations.FirstOrDefault(item => item.Id == id);
    }

    private async Task LoadMessagesAsync(Guid conversationId)
    {
        var messages = await conversationStore.GetMessagesAsync(conversationId);
        if (SelectedConversation?.Id != conversationId) return;
        var argumentsByCallId = ReadToolArguments(messages);
        Messages.Clear();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System ||
                (message.Role == ChatRole.Assistant && string.IsNullOrWhiteSpace(message.Content) && !string.IsNullOrWhiteSpace(message.ToolCallsJson)))
                continue;
            var content = message.Role == ChatRole.Tool
                ? FormatToolCard(argumentsByCallId.GetValueOrDefault(message.ToolCallId ?? string.Empty), message.Content, message.IsError)
                : message.Content;
            Messages.Add(new ChatItemViewModel(message.Role, content, message.IsError, message.ToolName,
                message.ResourceSnapshots, message.ContentParts, artifactStore));
        }
        RegenerateCommand.NotifyCanExecuteChanged();
    }

    private static Dictionary<string, string> ReadToolArguments(IEnumerable<ChatMessage> messages)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages.Where(message => !string.IsNullOrWhiteSpace(message.ToolCallsJson)))
        {
            try
            {
                using var document = JsonDocument.Parse(message.ToolCallsJson!);
                foreach (var call in document.RootElement.EnumerateArray())
                {
                    if (!call.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                        !call.TryGetProperty("function", out var function) ||
                        !function.TryGetProperty("arguments", out var arguments)) continue;
                    result[id.GetString()!] = arguments.ValueKind == JsonValueKind.String ? arguments.GetString() ?? "{}" : arguments.GetRawText();
                }
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static string FormatToolCard(string? arguments, string? result, bool isError)
    {
        var safeArguments = (arguments ?? "{}").Replace("```", "` ` `", StringComparison.Ordinal);
        return $"**状態:** {(isError ? "失敗" : "完了")}\n\n**引数**\n```json\n{safeArguments}\n```\n\n**結果**\n\n{result}";
    }

    private static string FormatBackend(RuntimeBackend? backend) => backend switch
    {
        RuntimeBackend.Cuda => "CUDA",
        RuntimeBackend.Vulkan => "Vulkan",
        RuntimeBackend.Cpu => "CPU",
        _ => "不明"
    };

    private async Task RefreshMcpStatusAsync()
    {
        var connections = await mcpManager.GetConnectionsAsync();
        IsLookDevAvailable = connections.Any(item => !string.IsNullOrWhiteSpace(item.LookDevContractVersion));
        NotifyWorkflowCommands();
        McpStatusText = connections.Count == 0
            ? "MCP: 未接続"
            : $"MCP: {connections.Count} 接続 / {connections.Sum(item => item.ToolCount)} ツール / {connections.Count(item => item.SupportsResources)} Resource対応";
    }
}
