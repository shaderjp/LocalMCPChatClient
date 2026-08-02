using CommunityToolkit.Mvvm.ComponentModel;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.App.ViewModels;

public sealed partial class ChatItemViewModel : ObservableObject
{
    public ChatItemViewModel(ChatRole role, string content, bool isError = false, string? toolName = null)
    {
        Role = role;
        _content = content;
        IsError = isError;
        ToolName = toolName;
    }

    public ChatRole Role { get; }
    public bool IsError { get; }
    public string? ToolName { get; }
    public bool IsUser => Role == ChatRole.User;
    public bool IsTool => Role == ChatRole.Tool;
    public string RoleLabel => Role switch
    {
        ChatRole.User => "あなた",
        ChatRole.Assistant => "Gemma",
        ChatRole.Tool => string.IsNullOrWhiteSpace(ToolName) ? "MCP Tool" : ToolName,
        _ => "System"
    };

    [ObservableProperty] private string _content;
}

public sealed record ConversationItemViewModel(Guid Id, string Title, DateTimeOffset UpdatedAt)
{
    public string UpdatedLabel => UpdatedAt.ToLocalTime().ToString("MM/dd HH:mm");
}
