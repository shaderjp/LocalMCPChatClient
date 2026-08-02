using System.Collections.Specialized;
using System.Windows;
using LocalMCPChatClient.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

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
}
