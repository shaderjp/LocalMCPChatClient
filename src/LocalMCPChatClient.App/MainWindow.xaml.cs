using System.Collections.Specialized;
using System.IO;
using System.Windows;
using LocalMCPChatClient.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace LocalMCPChatClient.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IServiceProvider _services;

    public MainWindow(MainViewModel viewModel, IServiceProvider services)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _services = services;
        _viewModel.Messages.CollectionChanged += OnMessagesChanged;
        _viewModel.SettingsRequested += OnSettingsRequested;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.BeginInvoke(ConversationScroll.ScrollToEnd);

    private async void OnSettingsRequested(object? sender, EventArgs e)
    {
        var window = new SettingsWindow(_services.GetRequiredService<SettingsViewModel>()) { Owner = this };
        window.ShowDialog();
        await _viewModel.InitializeAsync();
    }

    private async void ExportMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedConversation is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "チャット履歴をMarkdownで保存",
            Filter = "Markdown (*.md)|*.md|すべてのファイル (*.*)|*.*",
            DefaultExt = ".md",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = CreateExportFileName(_viewModel.SelectedConversation.Title)
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await _viewModel.ExportSelectedConversationAsync(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Markdownを保存できませんでした。\n\n" + exception.Message,
                "チャット履歴の保存", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string CreateExportFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeTitle = new string(title.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "チャット";
        if (safeTitle.Length > 80) safeTitle = safeTitle[..80];
        return $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.md";
    }
}
