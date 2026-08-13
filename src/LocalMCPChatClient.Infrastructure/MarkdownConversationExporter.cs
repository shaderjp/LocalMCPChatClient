using System.Text;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.Infrastructure;

public sealed class MarkdownConversationExporter(IConversationStore conversationStore) : IConversationExporter
{
    public async Task ExportMarkdownAsync(
        Guid conversationId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var conversation = await conversationStore.GetAsync(conversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("保存するチャットが見つかりません。");
        var messages = await conversationStore.GetMessagesAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var markdown = Format(conversation, messages);
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("保存先ディレクトリを特定できません。");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fullPath, markdown, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    internal static string Format(Conversation conversation, IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(SingleLine(conversation.Title));
        builder.AppendLine();
        builder.Append("- 会話ID: `").Append(conversation.Id.ToString("D")).AppendLine("`");
        builder.Append("- 作成日時: `").Append(FormatTimestamp(conversation.CreatedAt)).AppendLine("`");
        builder.Append("- 更新日時: `").Append(FormatTimestamp(conversation.UpdatedAt)).AppendLine("`");
        if (!string.IsNullOrWhiteSpace(conversation.ModelId))
            builder.Append("- モデル: `").Append(SingleLine(conversation.ModelId)).AppendLine("`");
        builder.AppendLine();
        builder.AppendLine("---");

        foreach (var message in messages)
        {
            builder.AppendLine();
            builder.Append("## ").AppendLine(RoleLabel(message));
            builder.AppendLine();
            builder.Append('*').Append(FormatTimestamp(message.CreatedAt)).AppendLine("*");
            builder.AppendLine();

            if (message.Role == ChatRole.Tool)
            {
                builder.Append("**状態:** ").AppendLine(message.IsError ? "失敗" : "完了");
                if (!string.IsNullOrWhiteSpace(message.ToolCallId))
                    builder.Append("**Tool Call ID:** `").Append(SingleLine(message.ToolCallId)).AppendLine("`");
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(message.Content)) builder.AppendLine(message.Content.TrimEnd());
            else if (string.IsNullOrWhiteSpace(message.ToolCallsJson)) builder.AppendLine("*(本文なし)*");

            if (message.ResourceSnapshots is { Count: > 0 })
            {
                builder.AppendLine();
                builder.AppendLine("### MCP Resources");
                foreach (var resource in message.ResourceSnapshots)
                {
                    builder.AppendLine();
                    builder.Append("#### ").AppendLine(SingleLine(resource.Name));
                    builder.Append("- サーバー: `").Append(SingleLine(resource.ServerDisplayName)).AppendLine("`");
                    builder.Append("- URI: `").Append(SingleLine(resource.Uri)).AppendLine("`");
                    if (!string.IsNullOrWhiteSpace(resource.MimeType))
                        builder.Append("- MIME type: `").Append(SingleLine(resource.MimeType)).AppendLine("`");
                    builder.Append("- 読取日時: `").Append(FormatTimestamp(resource.ReadAt)).AppendLine("`");
                    builder.Append("- 元のサイズ: `").Append(resource.OriginalByteCount).AppendLine(" bytes`");
                    builder.Append("- 切り詰め: ").AppendLine(resource.WasTruncated ? "あり" : "なし");
                    if (resource.SkippedBinaryParts > 0)
                        builder.Append("- 省略したバイナリ部分: `").Append(resource.SkippedBinaryParts).AppendLine("`");
                    builder.AppendLine();
                    AppendCodeBlock(builder, resource.Content, "text");
                }
            }

            if (!string.IsNullOrWhiteSpace(message.ToolCallsJson))
            {
                builder.AppendLine();
                builder.AppendLine("### Tool Calls");
                builder.AppendLine();
                AppendCodeBlock(builder, message.ToolCallsJson, "json");
            }
        }

        return builder.ToString();
    }

    private static string RoleLabel(ChatMessage message) => message.Role switch
    {
        ChatRole.System => "システム",
        ChatRole.User => "あなた",
        ChatRole.Assistant => "Gemma",
        ChatRole.Tool => "ツール: " + SingleLine(message.ToolName ?? "不明"),
        _ => message.Role.ToString()
    };

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void AppendCodeBlock(StringBuilder builder, string value, string language)
    {
        var longestRun = 0;
        var currentRun = 0;
        foreach (var character in value)
        {
            if (character == '`') longestRun = Math.Max(longestRun, ++currentRun);
            else currentRun = 0;
        }
        var fence = new string('`', Math.Max(3, longestRun + 1));
        builder.Append(fence).AppendLine(language);
        builder.AppendLine(value.TrimEnd());
        builder.AppendLine(fence);
    }
}
