using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.Infrastructure;

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalMCPChatClient");
    }

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string DatabasePath => Path.Combine(DataDirectory, "history.db");
    public string ModelsDirectory => Path.Combine(DataDirectory, "Models");
    public string RuntimesDirectory => Path.Combine(DataDirectory, "Runtimes");
    public string DownloadsDirectory => Path.Combine(DataDirectory, "Downloads");
    public string LogsDirectory => Path.Combine(DataDirectory, "Logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(RuntimesDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
