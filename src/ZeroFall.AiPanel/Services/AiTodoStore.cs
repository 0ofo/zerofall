using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Platform.Events;
using Microsoft.Data.Sqlite;

namespace ZeroFall.AiPanel.Services;

/// <summary>AI 聊天待办 SQLite 持久化（按会话隔离，单条 markdown 文本）。复用项目 .zerofall.db。</summary>
public sealed class AiTodoStore : IAiTodoStore, IDisposable
{
    public const string TodosTable = "ai_chat_todos";

    private readonly IEventBus _eventBus;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<ProjectOpenedEvent> _projectOpenedHandler;

    private string _databasePath = string.Empty;
    private bool _schemaReady;

    public AiTodoStore(IEventBus eventBus)
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

            await using var cmd = connection.CreateCommand();
            // 单行 markdown 文本：session_id 为主键
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS "{TodosTable}" (
                    session_id TEXT PRIMARY KEY,
                    markdown TEXT NOT NULL DEFAULT '',
                    updated_at_utc TEXT NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync();

            _schemaReady = true;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> GetMarkdownAsync(string sessionId)
    {
        if (!await TryEnsureSchemaAsync() || string.IsNullOrEmpty(sessionId))
            return string.Empty;

        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT markdown FROM \"{TodosTable}\" WHERE session_id = @sid";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            var result = await cmd.ExecuteScalarAsync();
            return result is string s ? s : string.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveMarkdownAsync(string sessionId, string markdown)
    {
        if (!await TryEnsureSchemaAsync() || string.IsNullOrEmpty(sessionId))
            return;

        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            var now = DateTime.UtcNow.ToString("O");
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO "{TodosTable}"(session_id, markdown, updated_at_utc)
                VALUES (@sid, @md, @now)
                ON CONFLICT(session_id) DO UPDATE SET markdown = @md, updated_at_utc = @now
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@md", markdown ?? string.Empty);
            cmd.Parameters.AddWithValue("@now", now);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe(_projectOpenedHandler);
        _gate.Dispose();
    }
}
