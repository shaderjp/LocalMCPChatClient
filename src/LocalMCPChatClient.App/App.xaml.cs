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
                services.AddSingleton<IConversationExporter, MarkdownConversationExporter>();
                services.AddSingleton<IArtifactInstaller, ArtifactInstaller>();
                services.AddSingleton<IInferenceRuntimeManager, LlamaRuntimeManager>();
                services.AddSingleton<IInferenceService, LlamaInferenceService>();
                services.AddSingleton<IMcpConnectionManager, McpConnectionManager>();
                services.AddSingleton<IToolApprovalService, ToolApprovalService>();
                services.AddSingleton<IToolApprovalPrompt, WpfToolApprovalPrompt>();
                services.AddSingleton<IAgentChatService, AgentChatService>();
                services.AddSingleton<MainViewModel>();
                services.AddTransient<SetupViewModel>();
                services.AddTransient<SettingsViewModel>();
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
        if (!settings.SetupCompleted)
        {
            var setup = new SetupWindow(_host.Services.GetRequiredService<SetupViewModel>());
            setup.ShowDialog();
        }

        await _host.Services.GetRequiredService<MainViewModel>().InitializeAsync();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(8));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
