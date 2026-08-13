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

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedDefinitions.Count == 0)
        {
            MessageBox.Show(this, "追加するResourceを1件以上選択してください。", "MCP Resource", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
