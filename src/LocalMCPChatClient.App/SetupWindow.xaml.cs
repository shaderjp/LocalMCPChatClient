using System.Windows;
using LocalMCPChatClient.App.ViewModels;
using Microsoft.Win32;

namespace LocalMCPChatClient.App;

public partial class SetupWindow : Window
{
    private readonly SetupViewModel _viewModel;

    public SetupWindow(SetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        _viewModel.Completed += (_, _) => { DialogResult = true; Close(); };
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private async void ImportExisting_Click(object sender, RoutedEventArgs e)
    {
        var modelDialog = new OpenFileDialog { Title = "GGUFモデルを選択", Filter = "GGUF model (*.gguf)|*.gguf" };
        if (modelDialog.ShowDialog(this) != true) return;
        var runtimeDialog = new OpenFileDialog { Title = "llama-server.exeを選択", Filter = "llama-server (llama-server.exe)|llama-server.exe" };
        if (runtimeDialog.ShowDialog(this) != true) return;
        try { await _viewModel.ImportExistingAsync(modelDialog.FileName, runtimeDialog.FileName); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "インポートエラー", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
