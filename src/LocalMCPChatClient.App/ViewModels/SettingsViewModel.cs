using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalMCPChatClient.Core;
using LocalMCPChatClient.Infrastructure;

namespace LocalMCPChatClient.App.ViewModels;

public sealed partial class SettingsViewModel(
    ISettingsStore settingsStore,
    ISecretStore secretStore,
    IConversationStore conversationStore,
    IToolApprovalService approvalService,
    IMcpConnectionManager mcpManager,
    IArtifactInstaller artifactInstaller,
    IAppPaths paths) : ObservableObject
{
    private AppSettings _settings = new();
    public ObservableCollection<ModelProfile> Models { get; } = [];
    public ObservableCollection<McpServerEditorViewModel> McpServers { get; } = [];
    public ObservableCollection<string> ApprovalRules { get; } = [];
    public IReadOnlyList<InferenceMode> InferenceModes { get; } = Enum.GetValues<InferenceMode>();
    public IReadOnlyList<RuntimeBackend> RuntimeBackends { get; } = Enum.GetValues<RuntimeBackend>();
    public string DataDirectory => paths.DataDirectory;
    public string LogsDirectory => paths.LogsDirectory;

    [ObservableProperty] private ModelProfile? _selectedModel;
    [ObservableProperty] private InferenceMode _inferenceMode;
    [ObservableProperty] private RuntimeBackend _runtimeBackend;
    [ObservableProperty] private int _contextSize = 8192;
    [ObservableProperty] private int _maxOutputTokens = 2048;
    [ObservableProperty] private double _temperature = 0.7;
    [ObservableProperty] private string? _customRuntimePath;
    [ObservableProperty] private string _modelDirectory = string.Empty;
    [ObservableProperty] private McpServerEditorViewModel? _selectedMcpServer;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isWorking;

    public async Task InitializeAsync()
    {
        _settings = await settingsStore.LoadAsync();
        Models.Clear();
        foreach (var model in _settings.Models) Models.Add(model);
        SelectedModel = Models.FirstOrDefault(model => model.Id == _settings.SelectedModelId) ?? Models.FirstOrDefault();
        InferenceMode = _settings.InferenceMode;
        RuntimeBackend = _settings.InferenceMode switch
        {
            InferenceMode.Cuda => RuntimeBackend.Cuda,
            InferenceMode.Vulkan => RuntimeBackend.Vulkan,
            _ => RuntimeBackend.Cpu
        };
        ContextSize = _settings.ContextSize;
        MaxOutputTokens = _settings.MaxOutputTokens;
        Temperature = _settings.Temperature;
        CustomRuntimePath = _settings.CustomRuntimePath;
        ModelDirectory = _settings.ModelDirectory ?? paths.ModelsDirectory;
        McpServers.Clear();
        foreach (var server in _settings.McpServers) McpServers.Add(new McpServerEditorViewModel(server));
        SelectedMcpServer = McpServers.FirstOrDefault();
        RefreshApprovalRules();
        StatusText = "設定を読み込みました。";
    }

    [RelayCommand]
    private async Task SaveGeneralAsync()
    {
        _settings = await settingsStore.UpdateAsync(settings => settings with
        {
            SelectedModelId = SelectedModel?.Id,
            InferenceMode = InferenceMode,
            ContextSize = Math.Clamp(ContextSize, 512, 131072),
            MaxOutputTokens = Math.Clamp(MaxOutputTokens, 64, 32768),
            Temperature = Math.Clamp(Temperature, 0, 2),
            CustomRuntimePath = string.IsNullOrWhiteSpace(CustomRuntimePath) ? null : Path.GetFullPath(CustomRuntimePath),
            ModelDirectory = string.IsNullOrWhiteSpace(ModelDirectory) ? paths.ModelsDirectory : Path.GetFullPath(ModelDirectory)
        });
        StatusText = "推論設定を保存しました。次の送信から反映されます。";
    }

    [RelayCommand]
    private void AddStdio()
    {
        var editor = new McpServerEditorViewModel(new McpServerProfile { Name = "Local MCP", Transport = McpTransportKind.Stdio });
        McpServers.Add(editor);
        SelectedMcpServer = editor;
    }

    [RelayCommand]
    private void AddHttp()
    {
        var editor = new McpServerEditorViewModel(new McpServerProfile { Name = "HTTP MCP", Transport = McpTransportKind.StreamableHttp, Url = "https://" });
        McpServers.Add(editor);
        SelectedMcpServer = editor;
    }

    [RelayCommand]
    private async Task RemoveMcpAsync()
    {
        if (SelectedMcpServer is null) return;
        await mcpManager.DisconnectAsync(SelectedMcpServer.Id);
        McpServers.Remove(SelectedMcpServer);
        SelectedMcpServer = McpServers.FirstOrDefault();
        await SaveMcpAsync();
    }

    [RelayCommand]
    private async Task SaveMcpAsync()
    {
        var profiles = new List<McpServerProfile>();
        foreach (var editor in McpServers) profiles.Add(await editor.ToProfileAsync(secretStore));
        _settings = await settingsStore.UpdateAsync(settings => settings with { McpServers = profiles });
        var currentConnections = await mcpManager.GetConnectionsAsync();
        foreach (var connection in currentConnections.Where(connection => profiles.All(profile => profile.Id != connection.ServerId || !profile.Enabled)))
            await mcpManager.DisconnectAsync(connection.ServerId);
        foreach (var profile in profiles.Where(profile => profile.Enabled))
            await mcpManager.ConnectAsync(profile);
        StatusText = "MCP設定を保存し、接続状態を更新しました。";
    }

    [RelayCommand]
    private async Task TestMcpAsync()
    {
        if (SelectedMcpServer is null) return;
        try
        {
            var profile = await SelectedMcpServer.ToProfileAsync(secretStore);
            var result = await mcpManager.TestAsync(profile);
            StatusText = result.State == McpConnectionState.Connected
                ? $"接続成功: {result.ToolCount}個のツール"
                : "接続失敗: " + result.Error;
        }
        catch (Exception exception) { StatusText = "接続失敗: " + exception.Message; }
    }

    [RelayCommand]
    private async Task ConnectMcpAsync()
    {
        if (SelectedMcpServer is null) return;
        var profile = await SelectedMcpServer.ToProfileAsync(secretStore);
        var result = await mcpManager.ConnectAsync(profile);
        StatusText = result.State == McpConnectionState.Connected
            ? $"接続しました: {result.ToolCount}個のツール"
            : "接続失敗: " + result.Error;
    }

    [RelayCommand]
    private async Task InstallModelAsync()
    {
        if (SelectedModel is null) return;
        var installedPath = await RunInstallAsync(BuiltInArtifacts.CreateModelArtifact(SelectedModel, ModelDirectory));
        if (installedPath is null) return;
        Models[Models.IndexOf(SelectedModel)] = SelectedModel = SelectedModel with { LocalPath = installedPath };
        _settings = await settingsStore.UpdateAsync(settings => settings with
        {
            SelectedModelId = SelectedModel.Id,
            ModelDirectory = ModelDirectory,
            Models = settings.Models.Select(model => model.Id == SelectedModel.Id ? SelectedModel : model).ToList()
        });
    }

    [RelayCommand]
    private async Task InstallRuntimeAsync()
    {
        var artifacts = BuiltInArtifacts.RuntimeArtifacts
            .Where(artifact => artifact.Backend == RuntimeBackend)
            .OrderByDescending(artifact => artifact.IsRuntimeDependency);
        foreach (var artifact in artifacts) await RunInstallAsync(artifact);
    }

    public async Task ImportModelAsync(string path)
    {
        if (SelectedModel is null) return;
        var imported = await artifactInstaller.ImportAsync(ArtifactKind.Model, path);
        Models[Models.IndexOf(SelectedModel)] = SelectedModel = SelectedModel with { LocalPath = imported };
        _settings = await settingsStore.UpdateAsync(settings => settings with
        {
            SelectedModelId = SelectedModel.Id,
            Models = settings.Models.Select(model => model.Id == SelectedModel.Id ? SelectedModel : model).ToList()
        });
        StatusText = "既存GGUFモデルを登録しました。";
    }

    public async Task ImportRuntimeAsync(string path)
    {
        CustomRuntimePath = await artifactInstaller.ImportAsync(ArtifactKind.Runtime, path);
        await SaveGeneralAsync();
        StatusText = "既存llama-serverを登録しました。";
    }

    public void SetModelDirectory(string path) => ModelDirectory = Path.GetFullPath(path);

    public async Task ClearHistoryAsync()
    {
        await conversationStore.DeleteAllAsync();
        StatusText = "チャット履歴をすべて削除しました。";
    }

    [RelayCommand]
    private async Task ClearApprovalsAsync()
    {
        await approvalService.ClearAsync();
        _settings = await settingsStore.LoadAsync();
        RefreshApprovalRules();
        StatusText = "ツール承認ルールを消去しました。";
    }

    private async Task<string?> RunInstallAsync(ArtifactDescriptor artifact)
    {
        IsWorking = true;
        try
        {
            var progress = new Progress<ArtifactProgress>(value =>
            {
                ProgressValue = value.TotalBytes is > 0 ? value.BytesReceived * 100d / value.TotalBytes.Value : 0;
                StatusText = $"{artifact.DisplayName}: {value.Stage} {value.BytesReceived / 1024d / 1024d:F0} MiB";
            });
            var installed = await artifactInstaller.InstallAsync(artifact, progress);
            ProgressValue = 100;
            StatusText = artifact.DisplayName + " の準備が完了しました。";
            return installed;
        }
        catch (Exception exception) { StatusText = "インストール失敗: " + exception.Message; return null; }
        finally { IsWorking = false; }
    }

    private void RefreshApprovalRules()
    {
        ApprovalRules.Clear();
        foreach (var rule in _settings.ApprovalRules)
            ApprovalRules.Add($"{rule.ServerId} / {rule.ToolName}: {rule.Decision}");
    }
}

public sealed partial class McpServerEditorViewModel : ObservableObject
{
    private McpServerProfile _original;

    public McpServerEditorViewModel(McpServerProfile profile)
    {
        _original = profile;
        Id = profile.Id;
        _name = profile.Name;
        _enabled = profile.Enabled;
        Transport = profile.Transport;
        _command = profile.Command ?? string.Empty;
        _argumentsText = string.Join(Environment.NewLine, profile.Arguments);
        _workingDirectory = profile.WorkingDirectory ?? string.Empty;
        _url = profile.Url ?? string.Empty;
        _bearerTokenEnvironmentVariable = profile.BearerTokenEnvironmentVariable ?? string.Empty;
        _enableStandaloneGetStream = profile.EnableStandaloneGetStream;
        _bufferHttpRequestBody = profile.BufferHttpRequestBody;
        _startupTimeoutSeconds = profile.StartupTimeoutSeconds;
        _timeoutSeconds = profile.TimeoutSeconds;
        _environmentText = JoinPlain(profile.Environment);
        _headersText = JoinPlain(profile.Headers);
        RefreshSavedSecretsLabel();
    }

    public string Id { get; }
    public McpTransportKind Transport { get; }
    public string TransportLabel => Transport == McpTransportKind.Stdio ? "stdio" : "Streamable HTTP";
    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _command;
    [ObservableProperty] private string _argumentsText;
    [ObservableProperty] private string _workingDirectory;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _bearerTokenEnvironmentVariable;
    [ObservableProperty] private bool _enableStandaloneGetStream = true;
    [ObservableProperty] private bool _bufferHttpRequestBody;
    [ObservableProperty] private int _startupTimeoutSeconds = 10;
    [ObservableProperty] private int _timeoutSeconds = 60;
    [ObservableProperty] private string _environmentText;
    [ObservableProperty] private string _secretEnvironmentText = string.Empty;
    [ObservableProperty] private string _headersText;
    [ObservableProperty] private string _secretHeadersText = string.Empty;
    [ObservableProperty] private string _savedSecretsLabel = string.Empty;

    public async Task<McpServerProfile> ToProfileAsync(ISecretStore secretStore)
    {
        var environment = ParsePairs(EnvironmentText).Select(pair => new SecretValue(pair.Key, pair.Value)).ToList();
        var headers = ParsePairs(HeadersText).Select(pair => new SecretValue(pair.Key, pair.Value)).ToList();
        await MergeSecretsAsync(environment, _original.Environment, ParsePairs(SecretEnvironmentText), "env", secretStore);
        await MergeSecretsAsync(headers, _original.Headers, ParsePairs(SecretHeadersText), "header", secretStore);
        var profile = new McpServerProfile
        {
            Id = Id,
            Name = string.IsNullOrWhiteSpace(Name) ? "MCP Server" : Name.Trim(),
            Enabled = Enabled,
            Transport = Transport,
            Command = string.IsNullOrWhiteSpace(Command) ? null : Command.Trim(),
            Arguments = ArgumentsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim(),
            Environment = environment,
            Url = string.IsNullOrWhiteSpace(Url) ? null : Url.Trim(),
            Headers = headers,
            BearerTokenEnvironmentVariable = string.IsNullOrWhiteSpace(BearerTokenEnvironmentVariable)
                ? null
                : BearerTokenEnvironmentVariable.Trim(),
            EnableStandaloneGetStream = EnableStandaloneGetStream,
            BufferHttpRequestBody = BufferHttpRequestBody,
            StartupTimeoutSeconds = Math.Clamp(StartupTimeoutSeconds, 1, 3600),
            TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 3600)
        };
        _original = profile;
        SecretEnvironmentText = string.Empty;
        SecretHeadersText = string.Empty;
        RefreshSavedSecretsLabel();
        return profile;
    }

    private async Task MergeSecretsAsync(List<SecretValue> target, IEnumerable<SecretValue> existing, Dictionary<string, string> replacements, string category, ISecretStore secretStore)
    {
        foreach (var item in existing.Where(item => !string.IsNullOrWhiteSpace(item.SecretRef)))
        {
            if (replacements.ContainsKey(item.Name)) continue;
            if (target.Any(value => value.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                await secretStore.DeleteAsync(item.SecretRef!);
                continue;
            }
            target.Add(item);
        }
        foreach (var pair in replacements)
        {
            target.RemoveAll(item => item.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            var reference = $"mcp:{Id}:{category}:{pair.Key}";
            await secretStore.SetAsync(reference, pair.Value);
            target.Add(new SecretValue(pair.Key, SecretRef: reference));
        }
    }

    private void RefreshSavedSecretsLabel()
    {
        var names = _original.Environment.Concat(_original.Headers).Where(item => item.SecretRef is not null).Select(item => item.Name).ToList();
        SavedSecretsLabel = names.Count == 0 ? "保存済みシークレットなし" : "保存済み: " + string.Join(", ", names);
    }

    private static string JoinPlain(IEnumerable<SecretValue> values) => string.Join(Environment.NewLine,
        values.Where(value => value.SecretRef is null).Select(value => $"{value.Name}={value.Value}"));

    private static Dictionary<string, string> ParsePairs(string text) => text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split('=', 2))
        .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
        .ToDictionary(parts => parts[0].Trim(), parts => parts[1], StringComparer.OrdinalIgnoreCase);
}
