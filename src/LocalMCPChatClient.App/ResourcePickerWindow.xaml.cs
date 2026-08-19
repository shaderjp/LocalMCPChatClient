using System.Windows;
using LocalMCPChatClient.App.ViewModels;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.App;

public partial class ResourcePickerWindow : Window
{
    private readonly ResourcePickerViewModel _viewModel;

    public ResourcePickerWindow(ResourcePickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
    }

    public IReadOnlyList<McpResourceDefinition> SelectedResources => _viewModel.SelectedDefinitions;
    public McpResourceDefinition? TemplateResource { get; private set; }
    public string? PromptText => _viewModel.PromptTextToInsert;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedDefinitions.Count == 0)
        {
            MessageBox.Show(this, "追加するResourceを1件以上選択してください。", "MCP Resource", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void AddTemplate_Click(object sender, RoutedEventArgs e)
    {
        TemplateResource = _viewModel.CreateTemplateResource();
        if (TemplateResource is null)
        {
            MessageBox.Show(this, "Templateのプレースホルダーを置換した有効な絶対URIを入力してください。", "MCP Resource Template", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void InsertPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.PromptTextToInsert))
        {
            MessageBox.Show(this, "先にPromptをプレビューしてください。", "MCP Prompt", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
