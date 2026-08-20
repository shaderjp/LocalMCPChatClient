using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalMCPChatClient.Core;
using LocalMCPChatClient.Infrastructure;

namespace LocalMCPChatClient.App.ViewModels;

public sealed partial class SetupViewModel(
    ISettingsStore settingsStore,
    IArtifactInstaller artifactInstaller,
    IInferenceRuntimeManager runtimeManager,
    ILookDevPairingService pairingService,
    IAppPaths paths) : ObservableObject
{
    private CancellationTokenSource? _cancellation;
    public ObservableCollection<ModelProfile> Models { get; } = [];
    public IReadOnlyList<RuntimeBackend> Backends { get; } = Enum.GetValues<RuntimeBackend>();
    public event EventHandler? Completed;

    [ObservableProperty] private ModelProfile? _selectedModel;
    [ObservableProperty] private RuntimeBackend _selectedBackend = RuntimeBackend.Cpu;
    [ObservableProperty] private string _hardwareSummary = "ハードウェアを確認中…";
    [ObservableProperty] private string _progressText = "モデルと推論バックエンドを選択してください。";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _acceptLicense;
    [ObservableProperty] private bool _enableVision = true;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private bool _isPairing;
    [ObservableProperty] private bool _pairingCompleted;
    [ObservableProperty] private string _lookDevPairingAddress = "http://127.0.0.1:8777";
    [ObservableProperty] private string _lookDevPairingCode = string.Empty;
    [ObservableProperty] private string _pairingStatus = "D3D12 LookDevを起動し、MCPパネルの8桁コードを入力してください。";
    public bool IsSuiteFirstRun { get; } = Environment.GetEnvironmentVariable("LOCAL_MCP_CHAT_SUITE_FIRST_RUN") == "1";

    public async Task InitializeAsync()
    {
        var suiteAddress = Environment.GetEnvironmentVariable("LOCAL_MCP_CHAT_LOOKDEV_ADDRESS");
        if (!string.IsNullOrWhiteSpace(suiteAddress)) LookDevPairingAddress = suiteAddress;
        var settings = await settingsStore.LoadAsync();
        Models.Clear();
        foreach (var model in settings.Models) Models.Add(model);
        SelectedModel = Models.FirstOrDefault(model => model.Id == settings.SelectedModelId) ?? Models.FirstOrDefault();
        var hardware = await runtimeManager.DetectHardwareAsync();
        HardwareSummary = hardware.Summary;
        SelectedBackend = hardware.HasNvidiaGpu ? RuntimeBackend.Cuda : hardware.HasVulkanGpu ? RuntimeBackend.Vulkan : RuntimeBackend.Cpu;
    }

    [RelayCommand(CanExecute = nameof(CanPairLookDev))]
    private async Task PairLookDevAsync()
    {
        IsPairing = true;
        PairLookDevCommand.NotifyCanExecuteChanged();
        try
        {
            PairingStatus = "LookDev endpointを検出してコードを交換中…";
            var result = await pairingService.PairAsync(LookDevPairingAddress, LookDevPairingCode);
            PairingCompleted = true;
            LookDevPairingCode = string.Empty;
            PairingStatus = $"ペアリング完了: D3D12 LookDev {result.ApplicationVersion} / 契約 {result.ContractVersion}。tokenはWindows Credential Managerへ保存しました。";
            InstallCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception)
        {
            PairingCompleted = false;
            PairingStatus = "ペアリング失敗: " + exception.Message;
        }
        finally
        {
            IsPairing = false;
            PairLookDevCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (SelectedModel is null) return;
        IsInstalling = true;
        InstallCommand.NotifyCanExecuteChanged();
        _cancellation = new CancellationTokenSource();
        try
        {
            var runtimeArtifacts = BuiltInArtifacts.RuntimeArtifacts
                .Where(artifact => artifact.Backend == SelectedBackend)
                .OrderByDescending(artifact => artifact.IsRuntimeDependency)
                .ToList();
            foreach (var artifact in runtimeArtifacts)
            {
                ProgressText = artifact.DisplayName + " を準備中";
                await artifactInstaller.InstallAsync(artifact, CreateProgress(artifact.DisplayName), _cancellation.Token);
            }

            var currentSettings = await settingsStore.LoadAsync(_cancellation.Token);
            var modelArtifact = BuiltInArtifacts.CreateModelArtifact(SelectedModel, currentSettings.ModelDirectory ?? paths.ModelsDirectory);
            ProgressText = SelectedModel.DisplayName + " を準備中";
            var installedModelPath = await artifactInstaller.InstallAsync(modelArtifact, CreateProgress(SelectedModel.DisplayName), _cancellation.Token);
            string? installedProjectorPath = null;
            var projectorArtifact = EnableVision
                ? BuiltInArtifacts.CreateVisionProjectorArtifact(SelectedModel, currentSettings.ModelDirectory ?? paths.ModelsDirectory)
                : null;
            if (projectorArtifact is not null)
            {
                ProgressText = projectorArtifact.DisplayName + " を準備中";
                installedProjectorPath = await artifactInstaller.InstallAsync(projectorArtifact, CreateProgress(projectorArtifact.DisplayName), _cancellation.Token);
            }
            await settingsStore.UpdateAsync(settings => settings with
            {
                SetupCompleted = true,
                SelectedModelId = SelectedModel.Id,
                InferenceMode = InferenceMode.Auto,
                ModelDirectory = currentSettings.ModelDirectory ?? paths.ModelsDirectory,
                EnableVision = EnableVision,
                Models = settings.Models.Select(item => item.Id == SelectedModel.Id
                    ? item with { LocalPath = installedModelPath, VisionProjectorPath = installedProjectorPath }
                    : item).ToList()
            }, _cancellation.Token);
            ProgressValue = 100;
            ProgressText = "セットアップが完了しました。";
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { ProgressText = "セットアップをキャンセルしました。再開できます。"; }
        catch (Exception exception) { ProgressText = "エラー: " + exception.Message; }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            IsInstalling = false;
            InstallCommand.NotifyCanExecuteChanged();
        }
    }

    public async Task ImportExistingAsync(string modelPath, string runtimePath)
    {
        if (SelectedModel is null) return;
        var importedModel = await artifactInstaller.ImportAsync(ArtifactKind.Model, modelPath);
        var importedRuntime = await artifactInstaller.ImportAsync(ArtifactKind.Runtime, runtimePath);
        await settingsStore.UpdateAsync(settings => settings with
        {
            SetupCompleted = true,
            SelectedModelId = SelectedModel.Id,
            CustomRuntimePath = importedRuntime,
            EnableVision = false,
            Models = settings.Models.Select(model => model.Id == SelectedModel.Id ? model with { LocalPath = importedModel } : model).ToList()
        });
        ProgressText = "既存のモデルとllama-serverを登録しました。";
        Completed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    private bool CanInstall() => AcceptLicense && SelectedModel is not null && !IsInstalling &&
                                 (!IsSuiteFirstRun || PairingCompleted);
    private bool CanPairLookDev() => !IsPairing && !IsInstalling && !PairingCompleted &&
                                     LookDevPairingCode.Length == 8 && LookDevPairingCode.All(char.IsAsciiDigit);
    partial void OnAcceptLicenseChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();
    partial void OnSelectedModelChanged(ModelProfile? value) => InstallCommand.NotifyCanExecuteChanged();
    partial void OnLookDevPairingCodeChanged(string value) => PairLookDevCommand.NotifyCanExecuteChanged();

    private IProgress<ArtifactProgress> CreateProgress(string name) => new Progress<ArtifactProgress>(progress =>
    {
        ProgressValue = progress.TotalBytes is > 0 ? progress.BytesReceived * 100d / progress.TotalBytes.Value : 0;
        var received = progress.BytesReceived / 1024d / 1024d;
        var total = progress.TotalBytes is { } size ? $" / {size / 1024d / 1024d:F0} MiB" : string.Empty;
        ProgressText = $"{name}: {progress.Stage} {received:F0} MiB{total}";
    });
}
