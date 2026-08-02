using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace LocalMCPChatClient.App;

public partial class LicensesWindow : Window
{
    private static readonly LicenseDocument[] Documents =
    [
        new("LocalMCPChatClient MIT License", "LocalMCPChatClient.LicenseTexts.APPLICATION-LICENSE.txt"),
        new("第三者ソフトウェア一覧", "LocalMCPChatClient.LicenseTexts.THIRD-PARTY-NOTICES.txt"),
        new(".NET Library License", "LocalMCPChatClient.LicenseTexts.DOTNET-LIBRARY-LICENSE.txt"),
        new(".NET Runtime MIT License", "LocalMCPChatClient.LicenseTexts.DOTNET-RUNTIME-LICENSE.txt"),
        new(".NET Runtime Third-Party Notices", "LocalMCPChatClient.LicenseTexts.DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt"),
        new("Windows SDK License", "LocalMCPChatClient.LicenseTexts.WINDOWS-SDK-LICENSE.txt"),
        new("MCP C# SDK License", "LocalMCPChatClient.LicenseTexts.MCP-CSHARP-SDK-LICENSE.txt"),
        new("MCP C# SDK Third-Party Notices", "LocalMCPChatClient.LicenseTexts.MCP-CSHARP-SDK-THIRD-PARTY-NOTICES.txt"),
        new("Apache License 2.0", "LocalMCPChatClient.LicenseTexts.APACHE-2.0.txt")
    ];

    public LicensesWindow()
    {
        InitializeComponent();
        DocumentList.ItemsSource = Documents;
        DocumentList.SelectedIndex = 0;
    }

    private void DocumentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DocumentList.SelectedItem is not LicenseDocument document) return;

        DocumentTitle.Text = document.Title;
        DocumentText.Text = ReadEmbeddedText(document.ResourceName);
        DocumentText.ScrollToHome();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(DocumentText.Text)) Clipboard.SetText(DocumentText.Text);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string ReadEmbeddedText(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"ライセンス文書を読み込めません: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record LicenseDocument(string Title, string ResourceName);
}
