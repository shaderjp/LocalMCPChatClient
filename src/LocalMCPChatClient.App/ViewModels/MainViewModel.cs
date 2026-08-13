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
    IInferenceRuntimeManager runtimeManager) : ObservableObject
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
    public event EventHandler? SettingsRequested;

    [ObservableProperty] private ConversationItemViewModel? _selectedConversation;
    [ObservableProperty] private ModelProfile? _selectedModel;
    [ObservableProperty] private InferenceMode _selectedInferenceMode = InferenceMode.Auto;
    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _statusText = "初期化中…";
    [ObservableProperty] private string _mcpStatusText = "MCP: 未接続";
    [ObservableProperty] private bool _isBusy;

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
                CustomRuntimePath = _settings.CustomRuntimePath
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
                        Messages.Add(new ChatItemViewModel(ChatRole.User, input.Text, resources: item.ResourceSnapshots));
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
                            item.ToolCall?.Name));
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

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

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
        CustomRuntimePath = _settings.CustomRuntimePath
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
            Messages.Add(new ChatItemViewModel(message.Role, content, message.IsError, message.ToolName, message.ResourceSnapshots));
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
        McpStatusText = connections.Count == 0
            ? "MCP: 未接続"
            : $"MCP: {connections.Count} 接続 / {connections.Sum(item => item.ToolCount)} ツール / {connections.Count(item => item.SupportsResources)} Resource対応";
    }
}
