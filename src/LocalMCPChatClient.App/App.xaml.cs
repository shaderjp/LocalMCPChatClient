using System.Windows;
using LocalMCPChatClient.App.ViewModels;
using LocalMCPChatClient.Core;
using LocalMCPChatClient.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalMCPChatClient.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
                logging.AddProvider(new RedactingFileLoggerProvider(new AppPaths().LogsDirectory));
            })
            .ConfigureServices(services =>
            {
                services.AddHttpClient();
                services.AddSingleton<IAppPaths, AppPaths>();
                services.AddSingleton<ISettingsStore, JsonSettingsStore>();
                services.AddSingleton<ISecretStore, WindowsCredentialStore>();
                services.AddSingleton<IConversationStore, SqliteConversationStore>();
                services.AddSingleton<IArtifactStore, ArtifactStore>();
                services.AddSingleton<IConversationExporter, MarkdownConversationExporter>();
                services.AddSingleton<IDiagnosticReportService, DiagnosticReportService>();
                services.AddSingleton<IArtifactInstaller, ArtifactInstaller>();
                services.AddSingleton<IInferenceRuntimeManager, LlamaRuntimeManager>();
                services.AddSingleton<IInferenceService, LlamaInferenceService>();
                services.AddSingleton<IInferenceBenchmarkService, LlamaBenchmarkService>();
                services.AddSingleton<IMcpConnectionManager, McpConnectionManager>();
                services.AddSingleton<ILookDevPairingService, LookDevPairingService>();
                services.AddSingleton<IMcpProfileImporter, McpProfileJsonImporter>();
                services.AddSingleton<IToolApprovalService, ToolApprovalService>();
                services.AddSingleton<IToolApprovalPrompt, WpfToolApprovalPrompt>();
                services.AddSingleton<IAgentChatService, AgentChatService>();
                services.AddSingleton<MainViewModel>();
                services.AddTransient<SetupViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<ResourcePickerViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();
        _host.Services.GetRequiredService<IAppPaths>().EnsureCreated();
        await _host.Services.GetRequiredService<IInferenceRuntimeManager>().RecoverOwnedProcessAsync();
        await _host.Services.GetRequiredService<IConversationStore>().InitializeAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        var settings = await _host.Services.GetRequiredService<ISettingsStore>().LoadAsync();
        var suiteFirstRun = Environment.GetEnvironmentVariable("LOCAL_MCP_CHAT_SUITE_FIRST_RUN") == "1" &&
                            !settings.SetupCompleted;
        if (!settings.SetupCompleted)
        {
            var setup = new SetupWindow(_host.Services.GetRequiredService<SetupViewModel>());
            setup.ShowDialog();
        }

        await _host.Services.GetRequiredService<MainViewModel>().InitializeAsync();
        mainWindow.Show();
        _ = CompleteBackgroundStartupAsync(
            _host.Services.GetRequiredService<MainViewModel>(), suiteFirstRun);
    }

    private static async Task CompleteBackgroundStartupAsync(MainViewModel viewModel, bool runSuiteReview)
    {
        try
        {
            await viewModel.StartBackgroundInitializationAsync();
            if (runSuiteReview) await viewModel.RunSuiteFirstReviewAsync();
        }
        catch (Exception exception)
        {
            viewModel.StatusText = "初回起動の確認に失敗しました: " + exception.Message;
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.Services.GetRequiredService<MainViewModel>().StopActiveWorkflowAsync();
            await _host.StopAsync(TimeSpan.FromSeconds(8));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
