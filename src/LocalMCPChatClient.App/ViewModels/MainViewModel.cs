using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.App.ViewModels;

public sealed partial class MainViewModel(
    IConversationStore conversationStore,
    ISettingsStore settingsStore,
    IAgentChatService agentChatService,
    IMcpConnectionManager mcpManager,
    IInferenceRuntimeManager runtimeManager) : ObservableObject
{
    private AppSettings _settings = new();
    private CancellationTokenSource? _turnCancellation;

    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];
    public ObservableCollection<ChatItemViewModel> Messages { get; } = [];
    public ObservableCollection<ModelProfile> Models { get; } = [];
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
        foreach (var server in _settings.McpServers.Where(server => server.Enabled)) await mcpManager.ConnectAsync(server);
        await RefreshMcpStatusAsync();
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
        if (SelectedConversation is null || SelectedModel is null || string.IsNullOrWhiteSpace(InputText)) return;
        var text = InputText.Trim();
        InputText = string.Empty;
        await RunTextAsync(text);
    }

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private async Task RegenerateAsync()
    {
        if (SelectedConversation is null) return;
        var conversationId = SelectedConversation.Id;
        var text = await conversationStore.DeleteLastTurnAsync(conversationId);
        if (string.IsNullOrWhiteSpace(text)) return;
        await LoadMessagesAsync(conversationId);
        await RunTextAsync(text);
    }

    private async Task RunTextAsync(string text)
    {
        if (SelectedConversation is null || SelectedModel is null) return;
        IsBusy = true;
        SendCommand.NotifyCanExecuteChanged();
        RegenerateCommand.NotifyCanExecuteChanged();
        _turnCancellation = new CancellationTokenSource();
        var assistant = new ChatItemViewModel(ChatRole.Assistant, string.Empty);
        Messages.Add(new ChatItemViewModel(ChatRole.User, text));
        Messages.Add(assistant);

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
                SelectedConversation.Id, text, profile, SelectedModel, _turnCancellation.Token))
            {
                switch (item.Kind)
                {
                    case AgentEventKind.TextDelta:
                        assistant.Content += item.Text;
                        StatusText = "生成中…";
                        break;
                    case AgentEventKind.ToolApprovalRequired:
                        StatusText = "ツール実行の承認を待っています";
                        break;
                    case AgentEventKind.ToolStarted:
                        StatusText = $"ツール実行中: {item.ToolCall?.Name}";
                        break;
                    case AgentEventKind.ToolCompleted:
                        Messages.Add(new ChatItemViewModel(
                            ChatRole.Tool,
                            FormatToolCard(item.ToolCall?.ArgumentsJson, item.ToolResult?.Content, item.ToolResult?.IsError == true),
                            item.ToolResult?.IsError ?? false,
                            item.ToolCall?.Name));
                        break;
                    case AgentEventKind.Warning:
                        Messages.Add(new ChatItemViewModel(ChatRole.System, item.Text ?? string.Empty, true));
                        break;
                }
            }
            StatusText = $"完了 · {runtimeManager.State.Backend}";
            var selectedId = SelectedConversation.Id;
            await ReloadConversationsAsync(selectedId);
            await LoadMessagesAsync(selectedId);
        }
        catch (OperationCanceledException)
        {
            StatusText = "生成を停止しました";
        }
        catch (Exception exception)
        {
            var retryHint = SelectedInferenceMode is InferenceMode.Cuda or InferenceMode.Vulkan
                ? "\n設定でCPU推論を選択して再試行できます。"
                : string.Empty;
            StatusText = "エラー: " + exception.Message;
            assistant.Content = string.IsNullOrEmpty(assistant.Content)
                ? exception.Message + retryHint
                : assistant.Content + $"\n\nエラー: {exception.Message}{retryHint}";
        }
        finally
        {
            _turnCancellation.Dispose();
            _turnCancellation = null;
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
            RegenerateCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanSend() => !IsBusy && SelectedConversation is not null && SelectedModel is not null && !string.IsNullOrWhiteSpace(InputText);
    private bool CanRegenerate() => !IsBusy && SelectedConversation is not null && Messages.Any(message => message.Role == ChatRole.User);

    [RelayCommand]
    private void Stop() => _turnCancellation?.Cancel();

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnSelectedConversationChanged(ConversationItemViewModel? value)
    {
        if (value is not null) _ = LoadMessagesAsync(value.Id);
    }

    partial void OnSelectedModelChanged(ModelProfile? value)
    {
        if (value is not null && _settings.Models.Count > 0) _ = SaveSelectionAsync(value.Id, SelectedInferenceMode);
    }

    partial void OnSelectedInferenceModeChanged(InferenceMode value)
    {
        if (_settings.Models.Count > 0) _ = SaveSelectionAsync(SelectedModel?.Id, value);
    }

    private async Task SaveSelectionAsync(string? modelId, InferenceMode mode)
        => _settings = await settingsStore.UpdateAsync(settings => settings with { SelectedModelId = modelId, InferenceMode = mode });

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
            Messages.Add(new ChatItemViewModel(message.Role, content, message.IsError, message.ToolName));
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

    private async Task RefreshMcpStatusAsync()
    {
        var connections = await mcpManager.GetConnectionsAsync();
        McpStatusText = connections.Count == 0 ? "MCP: 未接続" : $"MCP: {connections.Count} 接続 / {connections.Sum(item => item.ToolCount)} ツール";
    }
}
