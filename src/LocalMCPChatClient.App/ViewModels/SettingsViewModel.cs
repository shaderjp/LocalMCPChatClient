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
    IMcpProfileImporter mcpProfileImporter,
    IInferenceRuntimeManager runtimeManager,
    IInferenceBenchmarkService benchmarkService,
    IArtifactInstaller artifactInstaller,
    IAppPaths paths) : ObservableObject
{
    private AppSettings _settings = new();
    private CancellationTokenSource? _benchmarkCancellation;
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
    [ObservableProperty] private bool _preloadModel = true;
    [ObservableProperty] private string _modelDirectory = string.Empty;
    [ObservableProperty] private McpServerEditorViewModel? _selectedMcpServer;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _performanceRecommendation = string.Empty;
    [ObservableProperty] private string _benchmarkResultText = string.Empty;

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
        PreloadModel = _settings.PreloadModel;
        ModelDirectory = _settings.ModelDirectory ?? paths.ModelsDirectory;
        McpServers.Clear();
        foreach (var server in _settings.McpServers) McpServers.Add(new McpServerEditorViewModel(server));
        SelectedMcpServer = McpServers.FirstOrDefault();
        RefreshApprovalRules();
        var hardware = await runtimeManager.DetectHardwareAsync();
        PerformanceRecommendation = InferenceMode != InferenceMode.Auto &&
                                    InferenceMode != ToInferenceMode(hardware.RecommendedBackend)
            ? $"このPCでは{FormatBackend(hardware.RecommendedBackend)}を推奨します。Autoまたは速度診断を利用できます。"
            : hardware.Summary;
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
            PreloadModel = PreloadModel,
            CustomRuntimePath = string.IsNullOrWhiteSpace(CustomRuntimePath) ? null : Path.GetFullPath(CustomRuntimePath),
            ModelDirectory = string.IsNullOrWhiteSpace(ModelDirectory) ? paths.ModelsDirectory : Path.GetFullPath(ModelDirectory)
        });
        StatusText = "推論設定を保存しました。次の送信から反映されます。";
    }

    [RelayCommand]
    private async Task RunBenchmarkAsync()
    {
        if (SelectedModel is null || IsWorking) return;
        IsWorking = true;
        BenchmarkResultText = string.Empty;
        _benchmarkCancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<BenchmarkProgress>(item =>
            {
                StatusText = item.Stage;
                ProgressValue = item.Percent;
            });
            var results = await benchmarkService.RunAsync(SelectedModel, progress, _benchmarkCancellation.Token);
            BenchmarkResultText = string.Join(Environment.NewLine, results
                .OrderByDescending(result => result.PromptTokensPerSecond)
                .Select(result => $"{FormatBackend(result.Backend)}: prompt {result.PromptTokensPerSecond:F0} tok/s · generation {result.GeneratedTokensPerSecond:F1} tok/s"));
            StatusText = "速度診断が完了しました。推奨設定を適用できます。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "速度診断をキャンセルしました。";
        }
        catch (Exception exception)
        {
            StatusText = "速度診断に失敗しました: " + exception.Message;
        }
        finally
        {
            _benchmarkCancellation?.Dispose();
            _benchmarkCancellation = null;
            IsWorking = false;
        }
    }

    [RelayCommand]
    private void CancelBenchmark() => _benchmarkCancellation?.Cancel();

    [RelayCommand]
    private async Task ApplyBenchmarkAsync()
    {
        if (string.IsNullOrWhiteSpace(BenchmarkResultText)) return;
        InferenceMode = InferenceMode.Auto;
        await SaveGeneralAsync();
        StatusText = "速度診断結果をAutoモードへ適用しました。";
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

    public async Task<McpProfileImportSummary> ImportMcpAsync(string filePath)
    {
        if (IsWorking) throw new InvalidOperationException("別の設定処理が実行中です。");
        IsWorking = true;
        McpProfileImportResult? import = null;
        var credentialChanges = new Dictionary<string, string?>(StringComparer.Ordinal);
        var persisted = false;
        try
        {
            import = await mcpProfileImporter.ImportAsync(filePath);
            var importedNames = import.Servers.Select(server => server.Profile.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var replacedEditors = McpServers.Where(editor => importedNames.Contains(editor.Name)).ToList();
            var retainedEditors = McpServers.Except(replacedEditors).ToList();
            var profiles = new List<McpServerProfile>();
            foreach (var editor in retainedEditors) profiles.Add(await editor.ToProfileAsync(secretStore));

            var importedProfiles = new List<McpServerProfile>();
            foreach (var server in import.Servers)
            {
                var existing = replacedEditors.FirstOrDefault(editor =>
                    editor.Name.Equals(server.Profile.Name, StringComparison.OrdinalIgnoreCase));
                var secured = await SecureImportedServerAsync(server, existing?.Id ?? server.Profile.Id, credentialChanges);
                importedProfiles.Add(secured);
                profiles.Add(secured);
            }

            var previousSettings = _settings;
            _settings = await settingsStore.UpdateAsync(settings => settings with { McpServers = profiles });
            persisted = true;

            var warnings = import.Warnings.ToList();
            foreach (var editor in replacedEditors)
            {
                try { await mcpManager.DisconnectAsync(editor.Id); }
                catch (Exception exception) { warnings.Add($"{editor.Name}: 既存接続を切断できませんでした: {exception.Message}"); }
            }

            await DeleteInactiveSecretsAsync(previousSettings.McpServers, profiles, warnings);

            McpServers.Clear();
            foreach (var profile in profiles) McpServers.Add(new McpServerEditorViewModel(profile));
            SelectedMcpServer = importedProfiles.Count == 0
                ? McpServers.FirstOrDefault()
                : McpServers.FirstOrDefault(editor => editor.Id == importedProfiles[^1].Id);

            foreach (var profile in importedProfiles.Where(profile => profile.Enabled))
            {
                try
                {
                    var connection = await mcpManager.ConnectAsync(profile);
                    if (connection.State != McpConnectionState.Connected)
                        warnings.Add($"{profile.Name}: 保存しましたが接続できませんでした: {connection.Error}");
                }
                catch (Exception exception)
                {
                    warnings.Add($"{profile.Name}: 保存しましたが接続できませんでした: {exception.Message}");
                }
            }

            var summary = new McpProfileImportSummary(importedProfiles.Count, replacedEditors.Count, import.SecretCount, warnings);
            StatusText = summary.Warnings.Count == 0
                ? $"MCP設定を{summary.ImportedCount}件インポートしました。"
                : $"MCP設定を{summary.ImportedCount}件インポートしました（警告{summary.Warnings.Count}件）。";
            return summary;
        }
        catch
        {
            if (!persisted) await RollbackCredentialChangesAsync(credentialChanges);
            throw;
        }
        finally
        {
            IsWorking = false;
        }
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

    public async Task ResetSettingsAsync()
    {
        IsWorking = true;
        try
        {
            await runtimeManager.StopAsync();
            var connections = await mcpManager.GetConnectionsAsync();
            foreach (var connection in connections) await mcpManager.DisconnectAsync(connection.ServerId);
            await approvalService.ClearAsync();
            await secretStore.DeleteAllAsync();
            _settings = await settingsStore.ResetAsync();
            await InitializeAsync();
            StatusText = "すべての設定を初期化しました。次回起動時に初回セットアップが開きます。";
        }
        finally
        {
            IsWorking = false;
        }
    }

    private static InferenceMode ToInferenceMode(RuntimeBackend backend) => backend switch
    {
        RuntimeBackend.Cuda => InferenceMode.Cuda,
        RuntimeBackend.Vulkan => InferenceMode.Vulkan,
        _ => InferenceMode.Cpu
    };

    private static string FormatBackend(RuntimeBackend backend) => backend switch
    {
        RuntimeBackend.Cuda => "CUDA",
        RuntimeBackend.Vulkan => "Vulkan",
        _ => "CPU"
    };

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

    private async Task<McpServerProfile> SecureImportedServerAsync(
        ImportedMcpServer imported,
        string serverId,
        Dictionary<string, string?> credentialChanges)
    {
        var environment = imported.Profile.Environment.ToList();
        var headers = imported.Profile.Headers.ToList();
        await AddImportedSecretsAsync(environment, imported.SecretEnvironment, serverId, "env", credentialChanges);
        await AddImportedSecretsAsync(headers, imported.SecretHeaders, serverId, "header", credentialChanges);
        return imported.Profile with { Id = serverId, Environment = environment, Headers = headers };
    }

    private async Task AddImportedSecretsAsync(
        List<SecretValue> target,
        IReadOnlyDictionary<string, string> secrets,
        string serverId,
        string category,
        Dictionary<string, string?> credentialChanges)
    {
        foreach (var secret in secrets)
        {
            var reference = $"mcp:{serverId}:{category}:{secret.Key}";
            if (!credentialChanges.ContainsKey(reference))
                credentialChanges[reference] = await secretStore.GetAsync(reference);
            await secretStore.SetAsync(reference, secret.Value);
            target.RemoveAll(item => item.Name.Equals(secret.Key, StringComparison.OrdinalIgnoreCase));
            target.Add(new SecretValue(secret.Key, SecretRef: reference));
        }
    }

    private async Task RollbackCredentialChangesAsync(Dictionary<string, string?> changes)
    {
        foreach (var change in changes.Reverse())
        {
            if (change.Value is null) await secretStore.DeleteAsync(change.Key);
            else await secretStore.SetAsync(change.Key, change.Value);
        }
    }

    private async Task DeleteInactiveSecretsAsync(
        IEnumerable<McpServerProfile> previousProfiles,
        IEnumerable<McpServerProfile> currentProfiles,
        List<string> warnings)
    {
        var activeReferences = GetSecretReferences(currentProfiles).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in GetSecretReferences(previousProfiles).Where(reference => !activeReferences.Contains(reference)).Distinct(StringComparer.Ordinal))
        {
            try { await secretStore.DeleteAsync(reference); }
            catch (Exception exception) { warnings.Add($"使用されなくなった資格情報を削除できませんでした: {exception.Message}"); }
        }
    }

    private static IEnumerable<string> GetSecretReferences(IEnumerable<McpServerProfile> profiles)
        => profiles.SelectMany(profile => profile.Environment.Concat(profile.Headers))
            .Select(value => value.SecretRef)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference!);
}

public sealed record McpProfileImportSummary(
    int ImportedCount,
    int ReplacedCount,
    int SecretCount,
    IReadOnlyList<string> Warnings);

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
