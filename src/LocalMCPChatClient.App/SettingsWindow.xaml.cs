using System.Windows;
using LocalMCPChatClient.App.ViewModels;
using Microsoft.Win32;

namespace LocalMCPChatClient.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void ImportModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "GGUFモデルを選択", Filter = "GGUF model (*.gguf)|*.gguf" };
        if (dialog.ShowDialog(this) == true) await _viewModel.ImportModelAsync(dialog.FileName);
    }

    private async void ImportRuntime_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "llama-server.exeを選択", Filter = "llama-server (llama-server.exe)|llama-server.exe" };
        if (dialog.ShowDialog(this) == true) await _viewModel.ImportRuntimeAsync(dialog.FileName);
    }

    private void ChooseModelDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "モデル保存先を選択", Multiselect = false };
        if (dialog.ShowDialog(this) == true) _viewModel.SetModelDirectory(dialog.FolderName);
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "すべてのチャット履歴を削除します。この操作は元に戻せません。", "履歴の削除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            await _viewModel.ClearHistoryAsync();
    }
}
