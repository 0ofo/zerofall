using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.AiPanel.Models;
using ZeroFall.AiPanel.Serialization;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using Microsoft.Data.Sqlite;

namespace ZeroFall.AiPanel.Services;

/// <summary>AI 聊天会话 SQLite 持久化。订阅 ProjectOpenedEvent 拿 db 路径。</summary>
public sealed class AiChatSessionStore : IAiChatSessionStore, IDisposable
{
    public const string SessionsTable = "ai_chat_sessions";
    public const string MessagesTable = "ai_chat_messages";

    private const int TitleMaxLength = 40;

    private readonly IEventBus _eventBus;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<ProjectOpenedEvent> _projectOpenedHandler;

    private string _databasePath = string.Empty;
    private bool _schemaReady;

    public AiChatSessionStore(IEventBus eventBus)
    {
        _eventBus = eventBus;
        _projectOpenedHandler = OnProjectOpened;
        _eventBus.Subscribe(_projectOpenedHandler);
    }

    private void OnProjectOpened(ProjectOpenedEvent e)
    {
        _databasePath = e.DatabasePath ?? string.Empty;
        _schemaReady = false;
        _ = TryEnsureSchemaAsync();
    }

    private async Task<bool> TryEnsureSchemaAsync()
    {
        if (string.IsNullOrWhiteSpace(_databasePath))
            return false;
        if (_schemaReady)
            return true;

        await _gate.WaitAsync();
        try
        {
            if (_schemaReady)
                return true;

            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            await ConfigureConnectionAsync(connection);

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    CREATE TABLE IF NOT EXISTS "{SessionsTable}" (
                        session_id TEXT PRIMARY KEY,
                        title TEXT NOT NULL DEFAULT '新会话',
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        sort_order INTEGER NOT NULL DEFAULT 0
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            await EnsureColumnAsync(connection, SessionsTable, "token_usage_json", "TEXT");
            await EnsureColumnAsync(connection, SessionsTable, "api_start_message_id", "INTEGER NOT NULL DEFAULT 0");

            if (await TableHasColumnAsync(connection, MessagesTable, "seq"))
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP TABLE IF EXISTS \"{MessagesTable}\"";
                await drop.ExecuteNonQueryAsync();
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    CREATE TABLE IF NOT EXISTS "{MessagesTable}" (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id TEXT NOT NULL,
                        role TEXT NOT NULL,
                        type TEXT NOT NULL,
                        content TEXT NOT NULL,
                        reasoning_content TEXT NOT NULL DEFAULT '',
                        content_html TEXT,
                        context_token_count INTEGER NOT NULL DEFAULT 0,
                        visual INTEGER NOT NULL DEFAULT 0,
                        created_at_utc TEXT NOT NULL
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var idx = connection.CreateCommand())
            {
                idx.CommandText = $"""
                    CREATE INDEX IF NOT EXISTS idx_ai_chat_messages_session_id
                    ON "{MessagesTable}" (session_id, id);
                    """;
                await idx.ExecuteNonQueryAsync();
            }

            _schemaReady = true;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableHasColumnAsync(SqliteConnection connection, string table, string column)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await check.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string type)
    {
        if (await TableHasColumnAsync(connection, table, column))
            return;

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN {column} {type}";
        await alter.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ChatSessionSummary>> ListSessionsAsync()
    {
        if (!await TryEnsureSchemaAsync())
            return [];

        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT session_id, title, updated_at_utc, sort_order
                FROM "{SessionsTable}"
                ORDER BY sort_order ASC, created_at_utc ASC
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            var list = new List<ChatSessionSummary>();
            while (await reader.ReadAsync())
            {
                list.Add(new ChatSessionSummary
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    UpdatedAtUtc = reader.GetString(2),
                    SortOrder = reader.GetInt32(3)
                });
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatSessionData?> LoadSessionAsync(string sessionId)
    {
        if (!await TryEnsureSchemaAsync())
            return null;

        await _gate.WaitAsync();
        try
        {
            var header = await ReadSessionHeaderAsync(sessionId);
            if (header is null)
                return null;

            var messages = await ReadMessageDtosAsync(sessionId, fullContent: true);
            return new ChatSessionData
            {
                Id = sessionId,
                Title = header.Title,
                Messages = messages,
                ApiStartMessageId = header.ApiStartMessageId,
                TokenUsage = header.TokenUsage
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadVisibleShellsAsync(string sessionId)
    {
        if (!await TryEnsureSchemaAsync())
            return [];

        await _gate.WaitAsync();
        try
        {
            var dtos = await ReadMessageDtosAsync(sessionId, fullContent: false);
            return ChatHistoryMapper.BuildVisibleShells(dtos).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetVisibleMessageCountAsync(string sessionId)
    {
        var shells = await LoadVisibleShellsAsync(sessionId).ConfigureAwait(false);
        return shells.Count;
    }

    public async Task<ChatSessionHeader?> LoadSessionHeaderAsync(string sessionId)
    {
        if (!await TryEnsureSchemaAsync())
            return null;

        await _gate.WaitAsync();
        try
        {
            var header = await ReadSessionHeaderAsync(sessionId);
            if (header is null)
                return null;

            return new ChatSessionHeader
            {
                Id = sessionId,
                Title = header.Title,
                ApiStartMessageId = header.ApiStartMessageId,
                TokenUsage = header.TokenUsage
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadVisibleMessagesRangeAsync(
        string sessionId,
        int skipVisible,
        int takeVisible)
    {
        if (!await TryEnsureSchemaAsync() || takeVisible <= 0)
            return [];

        await _gate.WaitAsync();
        try
        {
            var dtos = await ReadVisibleMessageDtosRangeAsync(sessionId, skipVisible, takeVisible, fullContent: true);
            return ChatHistoryMapper.FromSession(new ChatSessionDto { Messages = dtos }).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record SessionHeader(
        string Title,
        long ApiStartMessageId,
        SessionTokenUsageState? TokenUsage);

    private async Task<SessionHeader?> ReadSessionHeaderAsync(string sessionId)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT title, api_start_message_id, token_usage_json
            FROM "{SessionsTable}" WHERE session_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        var title = reader.GetString(0);
        var apiStartMessageId = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        string? tokenUsageJson = reader.IsDBNull(2) ? null : reader.GetString(2);

        SessionTokenUsageState? tokenUsage = null;
        if (!string.IsNullOrWhiteSpace(tokenUsageJson))
        {
            try
            {
                tokenUsage = JsonSerializer.Deserialize(tokenUsageJson, AiPanelJsonContext.Default.SessionTokenUsageState);
            }
            catch
            {
            }
        }

        return new SessionHeader(title, apiStartMessageId, tokenUsage);
    }

    private async Task<List<ChatMessageDto>> ReadVisibleMessageDtosRangeAsync(
        string sessionId,
        int skipVisible,
        int takeVisible,
        bool fullContent)
    {
        if (skipVisible < 0 || takeVisible <= 0)
            return [];

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        var messages = new List<ChatMessageDto>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = fullContent
            ? $"""
                SELECT id, role, type, content, reasoning_content, context_token_count, visual
                FROM "{MessagesTable}"
                WHERE session_id = $id AND COALESCE(visual, 0) = 0
                ORDER BY id ASC
                LIMIT $take OFFSET $skip
                """
            : $"""
                SELECT id, role, type,
                       CASE
                         WHEN type = 'tool' THEN substr(content, 1, 512)
                         ELSE substr(content, 1, 96)
                       END,
                       substr(reasoning_content, 1, 96),
                       context_token_count, visual
                FROM "{MessagesTable}"
                WHERE session_id = $id AND COALESCE(visual, 0) = 0
                ORDER BY id ASC
                LIMIT $take OFFSET $skip
                """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$skip", skipVisible);
        cmd.Parameters.AddWithValue("$take", takeVisible);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var visual = reader.IsDBNull(fullContent ? 6 : 5)
                ? ChatMessageVisual.Visible
                : (ChatMessageVisual)reader.GetInt32(fullContent ? 6 : 5);

            messages.Add(fullContent
                ? new ChatMessageDto
                {
                    Id = reader.GetInt64(0),
                    Role = reader.GetString(1),
                    Type = reader.GetString(2),
                    Content = reader.GetString(3),
                    ReasoningContent = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    ContextTokenCount = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    Visual = visual,
                }
                : new ChatMessageDto
                {
                    Id = reader.GetInt64(0),
                    Role = reader.GetString(1),
                    Type = reader.GetString(2),
                    Content = reader.GetString(3),
                    ReasoningContent = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    ContextTokenCount = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    Visual = visual,
                });
        }

        return messages;
    }

    private async Task<List<ChatMessageDto>> ReadMessageDtosAsync(string sessionId, bool fullContent)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        var messages = new List<ChatMessageDto>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = fullContent
            ? $"""
                SELECT id, role, type, content, reasoning_content, content_html, context_token_count, visual
                FROM "{MessagesTable}"
                WHERE session_id = $id
                ORDER BY id ASC
                """
            : $"""
                SELECT id, role, type,
                       CASE
                         WHEN type = 'tool' THEN substr(content, 1, 512)
                         ELSE substr(content, 1, 96)
                       END,
                       substr(reasoning_content, 1, 96),
                       NULL, context_token_count, visual
                FROM "{MessagesTable}"
                WHERE session_id = $id
                ORDER BY id ASC
                """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var visual = reader.IsDBNull(7)
                ? ChatMessageVisual.Visible
                : (ChatMessageVisual)reader.GetInt32(7);

            messages.Add(new ChatMessageDto
            {
                Id = reader.GetInt64(0),
                Role = reader.GetString(1),
                Type = reader.GetString(2),
                Content = reader.GetString(3),
                ReasoningContent = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                ContentHtml = reader.IsDBNull(5) ? null : reader.GetString(5),
                ContextTokenCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Visual = visual
            });
        }

        return messages;
    }

    public async Task<string> CreateSessionAsync(string? title)
    {
        if (!await TryEnsureSchemaAsync())
            return string.Empty;

        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();

            var id = Guid.NewGuid().ToString("N");
            var nowUtc = DateTime.UtcNow.ToString("O");

            int sortOrder = 0;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT COALESCE(MAX(sort_order), -1) + 1 FROM \"{SessionsTable}\"";
                var v = await cmd.ExecuteScalarAsync();
                sortOrder = Convert.ToInt32(v);
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    INSERT INTO "{SessionsTable}" (session_id, title, created_at_utc, updated_at_utc, sort_order)
                    VALUES ($id, $title, $created, $updated, $sort)
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? "新会话" : title);
                cmd.Parameters.AddWithValue("$created", nowUtc);
                cmd.Parameters.AddWithValue("$updated", nowUtc);
                cmd.Parameters.AddWithValue("$sort", sortOrder);
                await cmd.ExecuteNonQueryAsync();
            }

            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppendMessagesResult> AppendStableMessagesAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        long? apiStartMessageId = null,
        SessionTokenUsageState? tokenUsage = null)
    {
        if (!await TryEnsureSchemaAsync())
            return new AppendMessagesResult();

        var pairs = new List<(ChatMessage Source, ChatMessageDto Dto)>();
        foreach (var message in messages)
        {
            if (!ChatHistoryMapper.TryGetPersistableDtos(message, out var messageDtos))
                continue;
            foreach (var dto in messageDtos)
                pairs.Add((message, dto));
        }

        if (pairs.Count == 0)
            return new AppendMessagesResult();

        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            await ConfigureConnectionAsync(connection);
            await using var tx = await connection.BeginTransactionAsync();

            try
            {
                long firstMessageId = -1;
                long toolMessageId = -1;
                var nowUtc = DateTime.UtcNow.ToString("O");

                foreach (var (source, dto) in pairs)
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"""
                        INSERT INTO "{MessagesTable}"
                            (session_id, role, type, content, reasoning_content, created_at_utc, content_html, context_token_count, visual)
                        VALUES ($sid, $role, $type, $content, $reasoning, $created, $html, $tokens, $visual);
                        SELECT last_insert_rowid();
                        """;
                    cmd.Parameters.AddWithValue("$sid", sessionId);
                    cmd.Parameters.AddWithValue("$role", dto.Role ?? "user");
                    cmd.Parameters.AddWithValue("$type", dto.Type ?? ChatMessageDto.TypeText);
                    cmd.Parameters.AddWithValue("$content", dto.Content ?? string.Empty);
                    cmd.Parameters.AddWithValue("$reasoning", dto.ReasoningContent ?? string.Empty);
                    cmd.Parameters.AddWithValue("$created", nowUtc);
                    cmd.Parameters.AddWithValue("$html", (object?)dto.ContentHtml ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$tokens", dto.ContextTokenCount);
                    cmd.Parameters.AddWithValue("$visual", (int)dto.Visual);

                    var insertedId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                    if (firstMessageId < 0)
                        firstMessageId = insertedId;

                    source.Id = insertedId;
                    dto.Id = insertedId;

                    if (string.Equals(dto.EffectiveType, ChatMessageDto.TypeTool, StringComparison.Ordinal))
                        toolMessageId = insertedId;
                }

                var title = DeriveTitleFromMessages(await ReadAllDtosAsync(connection, sessionId));
                string? tokenUsageJson = null;
                if (tokenUsage is { PromptTokens: > 0 })
                {
                    tokenUsageJson = JsonSerializer.Serialize(
                        tokenUsage,
                        AiPanelJsonContext.Default.SessionTokenUsageState);
                }

                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $"""
                        UPDATE "{SessionsTable}"
                        SET title = $title,
                            updated_at_utc = $updated,
                            api_start_message_id = CASE WHEN $apiStart >= 0 THEN $apiStart ELSE api_start_message_id END,
                            token_usage_json = COALESCE($usage, token_usage_json)
                        WHERE session_id = $id
                        """;
                    cmd.Parameters.AddWithValue("$title", title);
                    cmd.Parameters.AddWithValue("$updated", nowUtc);
                    cmd.Parameters.AddWithValue("$apiStart", apiStartMessageId ?? -1L);
                    cmd.Parameters.AddWithValue("$usage", (object?)tokenUsageJson ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$id", sessionId);
                    await cmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();

                return new AppendMessagesResult
                {
                    FirstMessageId = firstMessageId,
                    MessageCount = pairs.Count,
                    ToolMessageId = toolMessageId
                };
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<ChatMessageDto>> ReadAllDtosAsync(SqliteConnection connection, string sessionId)
    {
        var messages = new List<ChatMessageDto>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, role, type, content, reasoning_content, content_html, context_token_count, visual
            FROM "{MessagesTable}"
            WHERE session_id = $id
            ORDER BY id ASC
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            messages.Add(new ChatMessageDto
            {
                Id = reader.GetInt64(0),
                Role = reader.GetString(1),
                Type = reader.GetString(2),
                Content = reader.GetString(3),
                ReasoningContent = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                ContentHtml = reader.IsDBNull(5) ? null : reader.GetString(5),
                ContextTokenCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Visual = reader.IsDBNull(7) ? ChatMessageVisual.Visible : (ChatMessageVisual)reader.GetInt32(7)
            });
        }

        return messages;
    }

    public async Task UpdateSessionMetadataAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messagesForTitle,
        long apiStartMessageId,
        SessionTokenUsageState? tokenUsage)
    {
        if (!await TryEnsureSchemaAsync())
            return;

        var title = DeriveTitleFromMessages(ChatHistoryMapper.ToSession(messagesForTitle).Messages);
        string? tokenUsageJson = null;
        if (tokenUsage is { PromptTokens: > 0 })
        {
            tokenUsageJson = JsonSerializer.Serialize(
                tokenUsage,
                AiPanelJsonContext.Default.SessionTokenUsageState);
        }

        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{SessionsTable}"
                SET title = $title,
                    updated_at_utc = $updated,
                    api_start_message_id = $apiStart,
                    token_usage_json = $usage
                WHERE session_id = $id
                """;
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$apiStart", apiStartMessageId);
            cmd.Parameters.AddWithValue("$usage", (object?)tokenUsageJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", sessionId);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TruncateSessionMessagesAsync(string sessionId, long fromMessageIdInclusive)
    {
        if (!await TryEnsureSchemaAsync())
            return;
        if (fromMessageIdInclusive <= 0)
            return;

        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            try
            {
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $"""
                        DELETE FROM "{MessagesTable}"
                        WHERE session_id = $sid
                          AND id >= $fromId
                        """;
                    cmd.Parameters.AddWithValue("$sid", sessionId);
                    cmd.Parameters.AddWithValue("$fromId", fromMessageIdInclusive);
                    await cmd.ExecuteNonQueryAsync();
                }

                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $"""
                        UPDATE "{SessionsTable}"
                        SET updated_at_utc = $updated
                        WHERE session_id = $id
                        """;
                    cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("$id", sessionId);
                    await cmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdatePersistedMessageAsync(string sessionId, ChatMessage message)
    {
        if (!await TryEnsureSchemaAsync())
            return;
        if (message.Id <= 0)
            return;
        if (!ChatHistoryMapper.TryGetPersistableDtos(message, out var dtos) || dtos.Count == 0)
            return;

        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            var nowUtc = DateTime.UtcNow.ToString("O");

            foreach (var dto in dtos)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    UPDATE "{MessagesTable}"
                    SET content = $content,
                        reasoning_content = $reasoning,
                        content_html = $html,
                        context_token_count = $tokens,
                        visual = $visual
                    WHERE id = $id
                      AND session_id = $sid
                    """;
                cmd.Parameters.AddWithValue("$content", dto.Content ?? string.Empty);
                cmd.Parameters.AddWithValue("$reasoning", dto.ReasoningContent ?? string.Empty);
                cmd.Parameters.AddWithValue("$html", (object?)dto.ContentHtml ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tokens", dto.ContextTokenCount);
                cmd.Parameters.AddWithValue("$visual", (int)dto.Visual);
                cmd.Parameters.AddWithValue("$id", message.Id);
                cmd.Parameters.AddWithValue("$sid", sessionId);
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    UPDATE "{SessionsTable}"
                    SET updated_at_utc = $updated
                    WHERE session_id = $id
                    """;
                cmd.Parameters.AddWithValue("$updated", nowUtc);
                cmd.Parameters.AddWithValue("$id", sessionId);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameSessionAsync(string sessionId, string title)
    {
        if (!await TryEnsureSchemaAsync())
            return;

        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                UPDATE "{SessionsTable}"
                SET title = $title, updated_at_utc = $updated
                WHERE session_id = $id
                """;
            cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? "新会话" : title);
            cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", sessionId);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        if (!await TryEnsureSchemaAsync())
            return;

        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            try
            {
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $"DELETE FROM \"{MessagesTable}\" WHERE session_id = $id";
                    cmd.Parameters.AddWithValue("$id", sessionId);
                    await cmd.ExecuteNonQueryAsync();
                }
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $"DELETE FROM \"{SessionsTable}\" WHERE session_id = $id";
                    cmd.Parameters.AddWithValue("$id", sessionId);
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string DeriveTitleFromMessages(IReadOnlyList<ChatMessageDto> dtos)
    {
        foreach (var dto in dtos)
        {
            if (!string.Equals(dto.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!dto.Visual.IsVisibleInUi())
                continue;
            var content = dto.Content ?? string.Empty;
            content = content.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (content.Length == 0)
                continue;
            return content.Length > TitleMaxLength
                ? content[..TitleMaxLength] + "…"
                : content;
        }
        return "新会话";
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe(_projectOpenedHandler);
        _gate.Dispose();
    }
}
