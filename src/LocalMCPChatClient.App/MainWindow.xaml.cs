using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
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
    {
        if (e.OldItems is not null)
            foreach (ChatItemViewModel item in e.OldItems) item.PropertyChanged -= OnMessagePropertyChanged;
        if (e.NewItems is not null)
            foreach (ChatItemViewModel item in e.NewItems) item.PropertyChanged += OnMessagePropertyChanged;
        Dispatcher.BeginInvoke(ConversationScroll.ScrollToEnd);
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatItemViewModel.Content))
            Dispatcher.BeginInvoke(ConversationScroll.ScrollToEnd);
    }

    private async void OnSettingsRequested(object? sender, EventArgs e)
    {
        var window = new SettingsWindow(_services.GetRequiredService<SettingsViewModel>()) { Owner = this };
        window.ShowDialog();
        await _viewModel.InitializeAsync();
        _ = _viewModel.StartBackgroundInitializationAsync();
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

    private void AddResource_Click(object sender, RoutedEventArgs e)
    {
        var window = new ResourcePickerWindow(_services.GetRequiredService<ResourcePickerViewModel>()) { Owner = this };
        if (window.ShowDialog() != true) return;
        _viewModel.AddPendingResources(window.SelectedResources.Concat(window.TemplateResource is null ? [] : [window.TemplateResource]));
        if (!string.IsNullOrWhiteSpace(window.PromptText))
            _viewModel.InputText = string.IsNullOrWhiteSpace(_viewModel.InputText)
                ? window.PromptText
                : _viewModel.InputText + Environment.NewLine + Environment.NewLine + window.PromptText;
    }

    private void OpenArtifact_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ArtifactAttachmentViewModel artifact } || !File.Exists(artifact.LocalPath)) return;
        Process.Start(new ProcessStartInfo(artifact.LocalPath) { UseShellExecute = true });
    }

    private void SaveArtifact_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ArtifactAttachmentViewModel artifact } || !File.Exists(artifact.LocalPath)) return;
        var dialog = new SaveFileDialog
        {
            Title = "Artifactを保存",
            FileName = artifact.Name,
            Filter = "すべてのファイル (*.*)|*.*",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) == true) File.Copy(artifact.LocalPath, dialog.FileName, true);
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
