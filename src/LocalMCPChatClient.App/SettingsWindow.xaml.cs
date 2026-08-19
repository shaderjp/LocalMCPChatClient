using System.Diagnostics;
using System.IO;
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

    private async void ImportMcpJson_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "MCP設定JSONを選択",
            Filter = "MCP settings (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var result = await _viewModel.ImportMcpAsync(dialog.FileName);
            var message = $"MCPサーバー設定を{result.ImportedCount}件インポートしました。";
            if (result.ReplacedCount > 0) message += $"\n同名の既存設定{result.ReplacedCount}件を置き換えました。";
            if (result.SecretCount > 0)
                message += $"\n秘密情報{result.SecretCount}件はWindows Credential Managerへ保存しました。元のJSONファイルには値が残るため、取り扱いに注意してください。";
            if (result.Warnings.Count > 0)
                message += "\n\n警告:\n・" + string.Join("\n・", result.Warnings);
            MessageBox.Show(this, message, "MCP設定のインポート", MessageBoxButton.OK,
                result.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "MCP設定をインポートできませんでした。\n\n" + exception.Message,
                "MCP設定のインポート", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

    private void OpenArtifactFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_viewModel.ArtifactDirectory);
        Process.Start(new ProcessStartInfo(_viewModel.ArtifactDirectory) { UseShellExecute = true });
    }

    private async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        const string message = "すべての設定を初期状態へ戻します。\n\n" +
                               "初期化される項目:\n" +
                               "・モデル登録と推論設定\n" +
                               "・MCP接続と保存済みシークレット\n" +
                               "・ツール承認ルール\n\n" +
                               "チャット履歴、取得済みモデル、推論ランタイムは削除されません。\n" +
                               "この操作は元に戻せません。続行しますか？";
        if (MessageBox.Show(this, message, "設定の初期化", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        try
        {
            await _viewModel.ResetSettingsAsync();
            MessageBox.Show(this, "設定を初期化しました。次回起動時に初回セットアップが開きます。",
                "設定の初期化", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "設定を初期化できませんでした。\n\n" + exception.Message,
                "設定の初期化", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowLicenses_Click(object sender, RoutedEventArgs e)
    {
        var window = new LicensesWindow { Owner = this };
        window.ShowDialog();
    }
}
