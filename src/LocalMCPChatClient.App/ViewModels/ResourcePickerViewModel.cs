using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.App.ViewModels;

public sealed partial class ResourcePickerViewModel : ObservableObject
{
    private readonly IMcpConnectionManager _mcpManager;
    public ObservableCollection<ResourceItemViewModel> Resources { get; } = [];
    public ObservableCollection<string> Notices { get; } = [];
    public ObservableCollection<McpResourceTemplateDefinition> Templates { get; } = [];
    public ObservableCollection<McpPromptDefinition> Prompts { get; } = [];
    public ICollectionView ResourcesView { get; }
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ResourceItemViewModel? _selectedResource;
    [ObservableProperty] private string _previewText = "Resourceを選択して「プレビュー」を押してください。";
    [ObservableProperty] private string _statusText = "未読込";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private McpResourceTemplateDefinition? _selectedTemplate;
    [ObservableProperty] private string _templateUri = string.Empty;
    [ObservableProperty] private McpPromptDefinition? _selectedPrompt;
    [ObservableProperty] private string _promptArgumentsJson = "{}";
    [ObservableProperty] private string _promptPreview = "Promptを選択して展開結果をプレビューしてください。";
    public string? PromptTextToInsert { get; private set; }

    public ResourcePickerViewModel(IMcpConnectionManager mcpManager)
    {
        _mcpManager = mcpManager;
        ResourcesView = CollectionViewSource.GetDefaultView(Resources);
        ResourcesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ResourceItemViewModel.ServerDisplayName)));
        ResourcesView.Filter = FilterResource;
    }

    public IReadOnlyList<McpResourceDefinition> SelectedDefinitions => Resources
        .Where(item => item.IsSelected)
        .Select(item => item.Definition)
        .ToList();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        RefreshCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        var selected = Resources.Where(item => item.IsSelected)
            .Select(item => (item.Definition.ServerId, item.Definition.Uri))
            .ToHashSet();
        try
        {
            StatusText = "Resource一覧を取得中…";
            var catalogs = await _mcpManager.GetResourceCatalogsAsync();
            var templates = await _mcpManager.GetResourceTemplatesAsync();
            var prompts = await _mcpManager.GetPromptsAsync();
            Resources.Clear();
            Templates.Clear();
            Prompts.Clear();
            Notices.Clear();
            if (catalogs.Count == 0) Notices.Add("接続中のMCPサーバーがありません。");
            foreach (var catalog in catalogs)
            {
                if (!string.IsNullOrWhiteSpace(catalog.Error))
                    Notices.Add($"{catalog.ServerDisplayName}: {catalog.Error}");
                else if (catalog.Resources.Count == 0)
                    Notices.Add($"{catalog.ServerDisplayName}: 利用可能な静的Resourceはありません。");
                foreach (var definition in catalog.Resources)
                {
                    Resources.Add(new ResourceItemViewModel(definition)
                    {
                        IsSelected = selected.Contains((definition.ServerId, definition.Uri))
                    });
                }
            }
            foreach (var template in templates) Templates.Add(template);
            foreach (var prompt in prompts) Prompts.Add(prompt);
            ResourcesView.Refresh();
            StatusText = $"{Resources.Count}件のResource / {Templates.Count}件のTemplate / {Prompts.Count}件のPrompt";
        }
        catch (Exception exception)
        {
            StatusText = "一覧を取得できませんでした: " + exception.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshCommand.NotifyCanExecuteChanged();
            PreviewCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        if (SelectedResource is null) return;
        IsBusy = true;
        RefreshCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        try
        {
            StatusText = $"{SelectedResource.Name} を読取中…";
            var snapshot = await _mcpManager.ReadResourceAsync(McpResourceReference.FromDefinition(SelectedResource.Definition));
            var notes = new List<string>();
            if (snapshot.WasTruncated) notes.Add("256 KiBで切り詰めました");
            if (snapshot.SkippedBinaryParts > 0) notes.Add($"バイナリ{snapshot.SkippedBinaryParts}件を省略しました");
            PreviewText = $"名前: {snapshot.Name}\nサーバー: {snapshot.ServerDisplayName}\nURI: {snapshot.Uri}\nMIME: {snapshot.MimeType ?? "(不明)"}\n読取: {snapshot.ReadAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{string.Join(" / ", notes)}\n\n{snapshot.Content}";
            StatusText = "プレビューを更新しました";
        }
        catch (Exception exception)
        {
            PreviewText = "プレビューできませんでした。\n\n" + exception.Message;
            StatusText = "プレビュー失敗";
        }
        finally
        {
            IsBusy = false;
            RefreshCommand.NotifyCanExecuteChanged();
            PreviewCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanPreview() => !IsBusy && SelectedResource is not null;

    [RelayCommand]
    private async Task PreviewPromptAsync()
    {
        if (SelectedPrompt is null || IsBusy) return;
        IsBusy = true;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(PromptArgumentsJson) ? "{}" : PromptArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Prompt引数はJSON objectで入力してください。");
            var arguments = document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)JsonSerializer.Deserialize<object>(property.Value.GetRawText()));
            var result = await _mcpManager.GetPromptAsync(SelectedPrompt.ServerId, SelectedPrompt.Name, arguments);
            PromptTextToInsert = string.Join(Environment.NewLine + Environment.NewLine,
                result.Parts.Where(part => !string.IsNullOrWhiteSpace(part.Text)).Select(part => part.Text));
            PromptPreview = string.IsNullOrWhiteSpace(PromptTextToInsert) ? "(Promptはテキストを返しませんでした)" : PromptTextToInsert;
            StatusText = "Promptの展開結果をプレビューしました。";
        }
        catch (Exception exception)
        {
            PromptTextToInsert = null;
            PromptPreview = "Promptを展開できませんでした。\n\n" + exception.Message;
            StatusText = "Promptプレビュー失敗";
        }
        finally { IsBusy = false; }
    }

    public McpResourceDefinition? CreateTemplateResource()
    {
        if (SelectedTemplate is null || !Uri.TryCreate(TemplateUri, UriKind.Absolute, out _)) return null;
        return new McpResourceDefinition(
            SelectedTemplate.ServerId, SelectedTemplate.ServerDisplayName, TemplateUri,
            SelectedTemplate.Name, SelectedTemplate.Description, SelectedTemplate.MimeType);
    }

    partial void OnSearchTextChanged(string value) => ResourcesView.Refresh();
    partial void OnSelectedResourceChanged(ResourceItemViewModel? value) => PreviewCommand.NotifyCanExecuteChanged();
    partial void OnSelectedTemplateChanged(McpResourceTemplateDefinition? value) => TemplateUri = value?.UriTemplate ?? string.Empty;
    partial void OnSelectedPromptChanged(McpPromptDefinition? value)
    {
        PromptTextToInsert = null;
        PromptPreview = value is null
            ? "Promptを選択してください。"
            : $"{value.Description}\n\n引数: " + (value.Arguments.Count == 0 ? "なし" : string.Join(", ", value.Arguments.Select(argument => argument.Required ? argument.Name + " (必須)" : argument.Name)));
    }

    private bool FilterResource(object item)
    {
        if (item is not ResourceItemViewModel resource || string.IsNullOrWhiteSpace(SearchText)) return true;
        var query = SearchText.Trim();
        return resource.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               resource.ServerDisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               resource.Uri.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (resource.Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
               (resource.MimeType?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}

public sealed partial class ResourceItemViewModel(McpResourceDefinition definition) : ObservableObject
{
    public McpResourceDefinition Definition { get; } = definition;
    public string ServerDisplayName => Definition.ServerDisplayName;
    public string Name => Definition.Name;
    public string Uri => Definition.Uri;
    public string? Description => Definition.Description;
    public string? MimeType => Definition.MimeType;
    public string SizeLabel => Definition.Size is { } size ? $"{size:N0} bytes" : "サイズ不明";
    [ObservableProperty] private bool _isSelected;
}
