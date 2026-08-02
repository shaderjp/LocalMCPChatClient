using System.Windows;
using System.Windows.Controls;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.App;

public sealed class WpfToolApprovalPrompt : IToolApprovalPrompt
{
    public async Task<ApprovalResponse> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Application.Current.Dispatcher.InvokeAsync(() => ShowDialog(request, cancellationToken));
    }

    private static ApprovalResponse ShowDialog(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var result = ApprovalResponse.Deny;
        var window = new Window
        {
            Title = "MCPツール実行の確認",
            Owner = Application.Current.MainWindow,
            Width = 620,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["WindowBrush"],
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextBrush"],
            ResizeMode = ResizeMode.CanResize
        };
        var panel = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        void AddButton(string text, ApprovalResponse response)
        {
            var button = new Button { Content = text, MinWidth = 105 };
            button.Click += (_, _) => { result = response; window.DialogResult = response != ApprovalResponse.Deny; };
            buttons.Children.Add(button);
        }
        AddButton("拒否", ApprovalResponse.Deny);
        AddButton("今回のみ許可", ApprovalResponse.AllowOnce);
        AddButton("常に許可", ApprovalResponse.AlwaysAllow);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "ローカルLLMが次のMCPツールを実行しようとしています。", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        content.Children.Add(new TextBlock { Text = $"サーバー: {request.ServerName}\nツール: {request.ToolName}", Margin = new Thickness(0, 0, 0, 10) });
        content.Children.Add(new TextBlock { Text = "引数", Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MutedTextBrush"] });
        content.Children.Add(new TextBox { Text = request.ArgumentsJson, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 260 });
        panel.Children.Add(content);
        window.Content = panel;
        using var registration = cancellationToken.Register(() => window.Dispatcher.BeginInvoke(window.Close));
        window.ShowDialog();
        return result;
    }
}
