using System.Text.Json;
using LocalMCPChatClient.Core;
using Microsoft.Data.Sqlite;

namespace LocalMCPChatClient.Infrastructure;

public sealed class SqliteConversationStore(IAppPaths paths) : IConversationStore
{
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            paths.EnsureCreated();
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS conversations (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    model_id TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS messages (
                    id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    role INTEGER NOT NULL,
                    content TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    tool_call_id TEXT NULL,
                    tool_name TEXT NULL,
                    tool_calls_json TEXT NULL,
                    is_error INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_messages_conversation_created
                ON messages(conversation_id, created_at);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var columns = connection.CreateCommand();
            columns.CommandText = "PRAGMA table_info(messages)";
            var hasResourceSnapshots = false;
            await using (var reader = await columns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    hasResourceSnapshots |= reader.GetString(1).Equals("resource_snapshots_json", StringComparison.OrdinalIgnoreCase);
            }
            if (!hasResourceSnapshots)
            {
                var migration = connection.CreateCommand();
                migration.CommandText = "ALTER TABLE messages ADD COLUMN resource_snapshots_json TEXT NULL";
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<Conversation>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, created_at, updated_at, model_id FROM conversations ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadConversation(reader));
        }
        return result;
    }

    public async Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, created_at, updated_at, model_id FROM conversations WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadConversation(reader) : null;
    }

    public async Task<Conversation> CreateAsync(string title, string? modelId = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation(Guid.NewGuid(), string.IsNullOrWhiteSpace(title) ? "新しいチャット" : title.Trim(), now, now, modelId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO conversations(id,title,created_at,updated_at,model_id) VALUES($id,$title,$created,$updated,$model)";
        command.Parameters.AddWithValue("$id", conversation.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$created", conversation.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", conversation.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$model", (object?)modelId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return conversation;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ChatMessage>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, conversation_id, role, content, created_at, tool_call_id, tool_name, tool_calls_json, is_error, resource_snapshots_json
            FROM messages WHERE conversation_id = $conversation ORDER BY created_at, rowid
            """;
        command.Parameters.AddWithValue("$conversation", conversationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ChatMessage(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), (ChatRole)reader.GetInt32(2),
                reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8) != 0,
                reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<List<McpResourceSnapshot>>(reader.GetString(9))));
        }
        return result;
    }

    public async Task AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO messages(id,conversation_id,role,content,created_at,tool_call_id,tool_name,tool_calls_json,is_error,resource_snapshots_json)
            VALUES($id,$conversation,$role,$content,$created,$call,$tool,$calls,$error,$resources);
            UPDATE conversations SET updated_at=$updated WHERE id=$conversation;
            """;
        command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversation", message.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$role", (int)message.Role);
        command.Parameters.AddWithValue("$content", message.Content);
        command.Parameters.AddWithValue("$created", message.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$call", (object?)message.ToolCallId ?? DBNull.Value);
        command.Parameters.AddWithValue("$tool", (object?)message.ToolName ?? DBNull.Value);
        command.Parameters.AddWithValue("$calls", (object?)message.ToolCallsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", message.IsError ? 1 : 0);
        command.Parameters.AddWithValue("$resources", message.ResourceSnapshots is { Count: > 0 }
            ? JsonSerializer.Serialize(message.ResourceSnapshots)
            : DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserTurnInput?> DeleteLastTurnAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var find = connection.CreateCommand();
        find.Transaction = (SqliteTransaction)transaction;
        find.CommandText = "SELECT rowid, content, resource_snapshots_json FROM messages WHERE conversation_id=$id AND role=$role ORDER BY rowid DESC LIMIT 1";
        find.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        find.Parameters.AddWithValue("$role", (int)ChatRole.User);
        long rowId;
        string content;
        IReadOnlyList<McpResourceSnapshot> snapshots;
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            rowId = reader.GetInt64(0);
            content = reader.GetString(1);
            snapshots = reader.IsDBNull(2)
                ? []
                : JsonSerializer.Deserialize<List<McpResourceSnapshot>>(reader.GetString(2)) ?? [];
        }

        var delete = connection.CreateCommand();
        delete.Transaction = (SqliteTransaction)transaction;
        delete.CommandText = """
            DELETE FROM messages WHERE conversation_id=$id AND rowid >= $rowid;
            UPDATE conversations SET updated_at=$updated WHERE id=$id;
            """;
        delete.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        delete.Parameters.AddWithValue("$rowid", rowId);
        delete.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UserTurnInput { Text = content, ResourceSnapshots = snapshots };
    }

    public async Task RenameAsync(Guid conversationId, string title, CancellationToken cancellationToken = default)
        => await ExecuteAsync("UPDATE conversations SET title=$value, updated_at=$updated WHERE id=$id", conversationId, title.Trim(), cancellationToken).ConfigureAwait(false);

    public async Task DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => await ExecuteAsync("DELETE FROM conversations WHERE id=$id", conversationId, null, cancellationToken).ConfigureAwait(false);

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM conversations";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql, Guid id, string? value, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        if (sql.Contains("$updated", StringComparison.Ordinal)) command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteConnection CreateConnection() => new($"Data Source={paths.DatabasePath};Mode=ReadWriteCreate;Cache=Shared;Foreign Keys=True");

    private static Conversation ReadConversation(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)),
        DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4));
}
