using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Base.Events;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Traffic.Capture;
using Microsoft.Data.Sqlite;

namespace ZeroFall.Browser.Services;

public sealed class TrafficArchiveService : IDisposable
{
    public const string TableName = "http_traffic_entries";

    private readonly IEventBus _eventBus;
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private readonly object _pendingInsertGate = new();
    private readonly object _pendingBodyGate = new();
    private readonly Dictionary<string, PendingBodyUpdate> _pendingBodyUpdates = new(StringComparer.Ordinal);
    private readonly List<TrafficLogEntryViewModel> _pendingInserts = [];
    private string _databasePath = string.Empty;
    private bool _schemaReady;
    private Task _readyTask = Task.CompletedTask;

    public TrafficArchiveService(IEventBus eventBus)
    {
        _eventBus = eventBus;
        _eventBus.Subscribe<ProjectOpenedEvent>(OnProjectOpened);
    }

    public bool HasDatabase => !string.IsNullOrWhiteSpace(_databasePath);

    public string DatabasePath => _databasePath;

    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) =>
        _readyTask.WaitAsync(cancellationToken);

    private void OnProjectOpened(ProjectOpenedEvent e)
    {
        _databasePath = e.DatabasePath ?? string.Empty;
        _schemaReady = false;
        _readyTask = string.IsNullOrWhiteSpace(_databasePath)
            ? Task.CompletedTask
            : InitializeProjectDatabaseAsync();
    }

    private async Task InitializeProjectDatabaseAsync()
    {
        if (!await EnsureSchemaAsync().ConfigureAwait(false))
            return;

        await FlushPendingInsertsAsync().ConfigureAwait(false);
    }

    public async Task<bool> InsertAsync(TrafficLogEntryViewModel entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_databasePath))
        {
            EnqueuePendingInsert(entry);
            return false;
        }

        if (!await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
        {
            EnqueuePendingInsert(entry);
            return false;
        }

        return await InsertCoreAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> InsertCoreAsync(
        TrafficLogEntryViewModel entry,
        CancellationToken cancellationToken = default)
    {
        ApplyPendingBodyToEntry(entry);
        TrafficEntryMetadataComputer.Apply(entry);

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO "{TableName}" (
                    entry_id, captured_at_utc, time_text, source, tab, browser_tab_id, page_session_id,
                    top_level_url, session_document_host, resource_context, method, url, host, path, extension, status, status_code,
                    mime_category, mime_primary, mime_type,
                    request_headers, request_body, response_headers, response_body,
                    request_body_raw, response_body_raw,
                    request_content_type, response_content_type,
                    has_query, fingerprint_eligible, response_body_len,
                    latency_ms, color, remark
                ) VALUES (
                    $entry_id, $captured_at_utc, $time_text, $source, $tab, $browser_tab_id, $page_session_id,
                    $top_level_url, $session_document_host, $resource_context, $method, $url, $host, $path, $extension, $status, $status_code,
                    $mime_category, $mime_primary, $mime_type,
                    $request_headers, $request_body, $response_headers, $response_body,
                    $request_body_raw, $response_body_raw,
                    $request_content_type, $response_content_type,
                    $has_query, $fingerprint_eligible, $response_body_len,
                    $latency_ms, $color, $remark
                )
                ON CONFLICT(entry_id) DO UPDATE SET
                    captured_at_utc = excluded.captured_at_utc,
                    time_text = excluded.time_text,
                    source = excluded.source,
                    tab = excluded.tab,
                    browser_tab_id = excluded.browser_tab_id,
                    page_session_id = excluded.page_session_id,
                    top_level_url = excluded.top_level_url,
                    session_document_host = excluded.session_document_host,
                    resource_context = excluded.resource_context,
                    method = excluded.method,
                    url = excluded.url,
                    host = excluded.host,
                    path = excluded.path,
                    extension = excluded.extension,
                    status = excluded.status,
                    status_code = excluded.status_code,
                    mime_category = excluded.mime_category,
                    mime_primary = excluded.mime_primary,
                    mime_type = excluded.mime_type,
                    request_headers = excluded.request_headers,
                    request_body = CASE WHEN length(trim(excluded.request_body)) > 0 OR excluded.request_body_raw IS NOT NULL
                        THEN excluded.request_body ELSE "{TableName}".request_body END,
                    response_headers = excluded.response_headers,
                    response_body = CASE WHEN length(trim(excluded.response_body)) > 0 OR excluded.response_body_raw IS NOT NULL
                        THEN excluded.response_body ELSE "{TableName}".response_body END,
                    request_body_raw = COALESCE(excluded.request_body_raw, "{TableName}".request_body_raw),
                    response_body_raw = COALESCE(excluded.response_body_raw, "{TableName}".response_body_raw),
                    request_content_type = excluded.request_content_type,
                    response_content_type = excluded.response_content_type,
                    has_query = excluded.has_query,
                    fingerprint_eligible = excluded.fingerprint_eligible,
                    response_body_len = CASE WHEN excluded.response_body_len > 0
                        THEN excluded.response_body_len ELSE "{TableName}".response_body_len END,
                    latency_ms = excluded.latency_ms,
                    color = "{TableName}".color,
                    remark = "{TableName}".remark
                """;
            BindEntry(command, entry);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await FlushPendingBodyAsync(connection, entry.EntryId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private void EnqueuePendingInsert(TrafficLogEntryViewModel entry)
    {
        lock (_pendingInsertGate)
        {
            var index = _pendingInserts.FindIndex(x => x.EntryId == entry.EntryId);
            if (index >= 0)
                _pendingInserts[index] = entry;
            else
                _pendingInserts.Add(entry);
        }
    }

    private async Task FlushPendingInsertsAsync(CancellationToken cancellationToken = default)
    {
        List<TrafficLogEntryViewModel> snapshot;
        lock (_pendingInsertGate)
        {
            if (_pendingInserts.Count == 0)
                return;

            snapshot = _pendingInserts.ToList();
            _pendingInserts.Clear();
        }

        foreach (var entry in snapshot)
        {
            try
            {
                if (!await InsertAsync(entry, cancellationToken).ConfigureAwait(false))
                    EnqueuePendingInsert(entry);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TrafficArchive] Pending insert failed for {entry.EntryId}: {ex.Message}");
                EnqueuePendingInsert(entry);
            }
        }
    }

    public async Task<bool> DeleteAsync(string entryId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entryId))
            return false;
        if (!await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return false;

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""DELETE FROM "{TableName}" WHERE entry_id = $entry_id""";
            command.Parameters.AddWithValue("$entry_id", entryId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task UpdateBodyAsync(
        string entryId,
        string requestBody,
        string responseBody,
        byte[]? requestBodyRaw = null,
        byte[]? responseBodyRaw = null,
        bool? fingerprintEligible = null,
        int? responseBodyLength = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entryId))
            return;
        if (IsEmptyBodyPayload(requestBody, responseBody, requestBodyRaw, responseBodyRaw))
            return;
        if (!await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
        {
            QueuePendingBodyUpdate(entryId, requestBody, responseBody, requestBodyRaw, responseBodyRaw,
                fingerprintEligible, responseBodyLength);
            return;
        }

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            if (fingerprintEligible is not null || responseBodyLength is not null)
            {
                command.CommandText = $"""
                    UPDATE "{TableName}"
                    SET request_body = $request_body,
                        response_body = $response_body,
                        request_body_raw = COALESCE($request_body_raw, request_body_raw),
                        response_body_raw = COALESCE($response_body_raw, response_body_raw),
                        fingerprint_eligible = COALESCE($fingerprint_eligible, fingerprint_eligible),
                        response_body_len = COALESCE($response_body_len, response_body_len)
                    WHERE entry_id = $entry_id
                    """;
                command.Parameters.AddWithValue("$fingerprint_eligible",
                    fingerprintEligible is true ? 1 : fingerprintEligible is false ? 0 : DBNull.Value);
                command.Parameters.AddWithValue("$response_body_len",
                    responseBodyLength is int len ? len : DBNull.Value);
            }
            else
            {
                command.CommandText = $"""
                    UPDATE "{TableName}"
                    SET request_body = $request_body,
                        response_body = $response_body,
                        request_body_raw = COALESCE($request_body_raw, request_body_raw),
                        response_body_raw = COALESCE($response_body_raw, response_body_raw)
                    WHERE entry_id = $entry_id
                    """;
            }

            command.Parameters.AddWithValue("$request_body", requestBody ?? string.Empty);
            command.Parameters.AddWithValue("$response_body", responseBody ?? string.Empty);
            command.Parameters.AddWithValue("$request_body_raw", (object?)requestBodyRaw ?? DBNull.Value);
            command.Parameters.AddWithValue("$response_body_raw", (object?)responseBodyRaw ?? DBNull.Value);
            command.Parameters.AddWithValue("$entry_id", entryId);
            var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (rows == 0)
            {
                QueuePendingBodyUpdate(entryId, requestBody ?? string.Empty, responseBody ?? string.Empty,
                    requestBodyRaw, responseBodyRaw, fingerprintEligible, responseBodyLength);
            }
            else
            {
                RemovePendingBodyUpdate(entryId);
            }
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task UpdateAnnotationAsync(
        string entryId,
        TrafficHighlightColor color,
        string remark,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entryId))
            return;
        if (!await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return;

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE "{TableName}"
                SET color = $color,
                    remark = $remark
                WHERE entry_id = $entry_id
                """;
            command.Parameters.AddWithValue("$color", TrafficLogEntryViewModel.ToStorageValue(color));
            command.Parameters.AddWithValue("$remark", remark ?? string.Empty);
            command.Parameters.AddWithValue("$entry_id", entryId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<IReadOnlyList<TrafficLogEntryViewModel>> QueryAsync(
        TrafficFilterSpec spec,
        string lastBrowserTabId,
        bool onlyLastBrowserTab,
        int limit,
        TrafficArchiveProjection projection = TrafficArchiveProjection.Full,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return [];

        var query = TrafficFilterSqlBuilder.Build(spec, lastBrowserTabId, onlyLastBrowserTab, limit, projection);

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = query.Sql;
            foreach (var parameter in query.Parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);

            var entries = new List<TrafficLogEntryViewModel>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                entries.Add(ReadEntry(reader));

            return entries;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<TrafficLogEntryViewModel?> FindByIdAsync(string entryId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entryId))
            return null;
        if (!await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return null;

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT * FROM "{TableName}"
                WHERE entry_id = $entry_id
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$entry_id", entryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEntry(reader) : null;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return;

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM \"{TableName}\"";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<bool> EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_databasePath))
            return false;
        if (_schemaReady)
            return true;

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaReady)
                return true;

            EnsureDatabaseFileReady();

            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS "{TableName}" (
                    entry_id TEXT PRIMARY KEY,
                    captured_at_utc TEXT NOT NULL,
                    time_text TEXT NOT NULL DEFAULT '',
                    source TEXT NOT NULL DEFAULT '',
                    tab TEXT NOT NULL DEFAULT '',
                    browser_tab_id TEXT NOT NULL DEFAULT '',
                    page_session_id INTEGER NOT NULL DEFAULT 0,
                    top_level_url TEXT NOT NULL DEFAULT '',
                    resource_context TEXT NOT NULL DEFAULT '',
                    method TEXT NOT NULL DEFAULT '',
                    url TEXT NOT NULL DEFAULT '',
                    host TEXT NOT NULL DEFAULT '',
                    path TEXT NOT NULL DEFAULT '',
                    extension TEXT NOT NULL DEFAULT '',
                    status TEXT NOT NULL DEFAULT '',
                    status_code INTEGER,
                    session_document_host TEXT NOT NULL DEFAULT '',
                    mime_category INTEGER,
                    mime_primary TEXT NOT NULL DEFAULT '',
                    mime_type TEXT NOT NULL DEFAULT '',
                    has_query INTEGER NOT NULL DEFAULT 0,
                    fingerprint_eligible INTEGER NOT NULL DEFAULT 0,
                    response_body_len INTEGER NOT NULL DEFAULT 0,
                    request_headers TEXT NOT NULL DEFAULT '',
                    request_body TEXT NOT NULL DEFAULT '',
                    response_headers TEXT NOT NULL DEFAULT '',
                    response_body TEXT NOT NULL DEFAULT '',
                    request_body_raw BLOB,
                    response_body_raw BLOB,
                    request_content_type TEXT NOT NULL DEFAULT '',
                    response_content_type TEXT NOT NULL DEFAULT '',
                    latency_ms INTEGER,
                    color TEXT NOT NULL DEFAULT '',
                    remark TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS idx_http_traffic_captured_at ON "{TableName}" (captured_at_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_http_traffic_method ON "{TableName}" (method);
                CREATE INDEX IF NOT EXISTS idx_http_traffic_host ON "{TableName}" (host);
                CREATE INDEX IF NOT EXISTS idx_http_traffic_status_code ON "{TableName}" (status_code);
                CREATE INDEX IF NOT EXISTS idx_http_traffic_source ON "{TableName}" (source);
                CREATE INDEX IF NOT EXISTS idx_http_traffic_browser_tab ON "{TableName}" (browser_tab_id);
                CREATE INDEX IF NOT EXISTS idx_http_traffic_mime_category ON "{TableName}" (mime_category);
                CREATE INDEX IF NOT EXISTS idx_http_traffic_session_doc_host ON "{TableName}" (session_document_host);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await MigrateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            _schemaReady = true;
            return true;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private static void BindEntry(SqliteCommand command, TrafficLogEntryViewModel entry)
    {
        var meta = entry.HasDerivedMetadata
            ? entry.DerivedMetadata
            : TrafficEntryMetadataComputer.Compute(entry);

        command.Parameters.AddWithValue("$entry_id", entry.EntryId);
        command.Parameters.AddWithValue("$captured_at_utc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$time_text", entry.Time);
        command.Parameters.AddWithValue("$source", ResolveSource(entry));
        command.Parameters.AddWithValue("$tab", entry.Tab);
        command.Parameters.AddWithValue("$browser_tab_id", entry.BrowserTabId);
        command.Parameters.AddWithValue("$page_session_id", entry.PageSessionId);
        command.Parameters.AddWithValue("$top_level_url", entry.TopLevelUrl);
        command.Parameters.AddWithValue("$session_document_host", meta.SessionDocumentHost);
        command.Parameters.AddWithValue("$resource_context", entry.ResourceContext.ToStorageKey());
        command.Parameters.AddWithValue("$method", entry.Method);
        command.Parameters.AddWithValue("$url", entry.Url);
        command.Parameters.AddWithValue("$host", meta.Host);
        command.Parameters.AddWithValue("$path", meta.Path);
        command.Parameters.AddWithValue("$extension", meta.Extension);
        command.Parameters.AddWithValue("$status", entry.Status);
        command.Parameters.AddWithValue("$status_code", meta.StatusCode is null ? DBNull.Value : meta.StatusCode.Value);
        command.Parameters.AddWithValue("$mime_category", (int)meta.Mime.FilterCategory);
        command.Parameters.AddWithValue("$mime_primary", meta.Mime.PrimaryClass);
        command.Parameters.AddWithValue("$mime_type", meta.Mime.MediaType);
        command.Parameters.AddWithValue("$has_query", meta.HasQuery ? 1 : 0);
        command.Parameters.AddWithValue("$fingerprint_eligible", meta.FingerprintEligible ? 1 : 0);
        command.Parameters.AddWithValue("$response_body_len", meta.ResponseBodyLength);
        command.Parameters.AddWithValue("$request_headers", entry.RequestHeaders);
        command.Parameters.AddWithValue("$request_body", entry.RequestBody);
        command.Parameters.AddWithValue("$response_headers", entry.ResponseHeaders);
        command.Parameters.AddWithValue("$response_body", entry.ResponseBody);
        command.Parameters.AddWithValue("$request_body_raw", (object?)entry.RequestBodyRaw ?? DBNull.Value);
        command.Parameters.AddWithValue("$response_body_raw", (object?)entry.ResponseBodyRaw ?? DBNull.Value);
        command.Parameters.AddWithValue("$request_content_type", entry.RequestContentType);
        command.Parameters.AddWithValue("$response_content_type", entry.ResponseContentType);
        command.Parameters.AddWithValue("$latency_ms", entry.LatencyMs is null ? DBNull.Value : entry.LatencyMs.Value);
        command.Parameters.AddWithValue("$color", TrafficLogEntryViewModel.ToStorageValue(entry.Color));
        command.Parameters.AddWithValue("$remark", entry.Remark ?? string.Empty);
    }

    private static async Task MigrateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var colCmd = connection.CreateCommand();
        colCmd.CommandText = $"PRAGMA table_info(\"{TableName}\")";
        await using var reader = await colCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            existingColumns.Add(reader.GetString(1));

        await EnsureColumnAsync(connection, existingColumns, "latency_ms", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "color", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "remark", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "request_body_raw", "BLOB", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "response_body_raw", "BLOB", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "resource_context", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "status_code", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "session_document_host", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "mime_category", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "mime_primary", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "mime_type", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "has_query", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "fingerprint_eligible", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, existingColumns, "response_body_len", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        await using (var indexCmd = connection.CreateCommand())
        {
            indexCmd.CommandText = $"""
                CREATE INDEX IF NOT EXISTS idx_http_traffic_mime_category ON "{TableName}" (mime_category);
                CREATE INDEX IF NOT EXISTS idx_http_traffic_session_doc_host ON "{TableName}" (session_document_host);
                """;
            await indexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (existingColumns.Contains("highlight_color"))
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = $"""
                UPDATE "{TableName}"
                SET color = highlight_color
                WHERE TRIM(color) = '' AND TRIM(highlight_color) <> ''
                """;
            await migrateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        HashSet<string> existingColumns,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        if (existingColumns.Contains(columnName))
            return;

        await using var alterCmd = connection.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE \"{TableName}\" ADD COLUMN {columnName} {columnDefinition}";
        await alterCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        existingColumns.Add(columnName);
    }

    /// <summary>确保父目录存在；SQLite 在首次 Open 时创建库文件（勿依赖 File.Exists）。</summary>
    private void EnsureDatabaseFileReady()
    {
        var dir = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static TrafficLogEntryViewModel ReadEntry(SqliteDataReader reader)
    {
        var entry = new TrafficLogEntryViewModel
        {
            EntryId = GetString(reader, "entry_id"),
            Time = GetString(reader, "time_text"),
            Tab = GetString(reader, "tab"),
            BrowserTabId = GetString(reader, "browser_tab_id"),
            CaptureSource = ParseCaptureSource(
                HasColumn(reader, "source") ? GetString(reader, "source") : null,
                GetString(reader, "browser_tab_id")),
            PageSessionId = GetInt32(reader, "page_session_id"),
            TopLevelUrl = GetString(reader, "top_level_url"),
            ResourceContext = ParseResourceContext(GetString(reader, "resource_context")),
            Method = GetString(reader, "method"),
            Url = GetString(reader, "url"),
            Status = GetString(reader, "status"),
            LatencyMs = GetNullableInt64(reader, "latency_ms"),
            RequestHeaders = HasColumn(reader, "request_headers") ? GetString(reader, "request_headers") : string.Empty,
            RequestBody = HasColumn(reader, "request_body") ? GetString(reader, "request_body") : string.Empty,
            RequestBodyRaw = HasColumn(reader, "request_body_raw") ? GetBytes(reader, "request_body_raw") : null,
            ResponseHeaders = HasColumn(reader, "response_headers") ? GetString(reader, "response_headers") : string.Empty,
            ResponseBody = HasColumn(reader, "response_body") ? GetString(reader, "response_body") : string.Empty,
            ResponseBodyRaw = HasColumn(reader, "response_body_raw") ? GetBytes(reader, "response_body_raw") : null,
            Color = TrafficLogEntryViewModel.ParseColor(GetString(reader, "color")),
            Remark = GetString(reader, "remark")
        };

        if (HasColumn(reader, "mime_category"))
        {
            var mimeCategory = GetNullableInt32(reader, "mime_category");
            if (mimeCategory is int category && category >= 0)
            {
                entry.ApplyDerivedMetadata(new TrafficEntryDerivedMetadata
                {
                    Mime = new TrafficMimeSnapshot
                    {
                        FilterCategory = (TrafficMimeCategory)category,
                        PrimaryClass = GetString(reader, "mime_primary"),
                        MediaType = GetString(reader, "mime_type")
                    },
                    SessionDocumentHost = GetString(reader, "session_document_host"),
                    HasQuery = GetInt32(reader, "has_query") != 0,
                    FingerprintEligible = GetInt32(reader, "fingerprint_eligible") != 0,
                    ResponseBodyLength = GetInt32(reader, "response_body_len"),
                    StatusCode = GetNullableInt32(reader, "status_code"),
                    Host = GetString(reader, "host"),
                    Path = GetString(reader, "path"),
                    Extension = GetString(reader, "extension"),
                    RequestContentType = GetString(reader, "request_content_type"),
                    ResponseContentType = GetString(reader, "response_content_type")
                });
            }
            else
            {
                TrafficEntryMetadataComputer.Apply(entry);
            }
        }
        else
        {
            TrafficEntryMetadataComputer.Apply(entry);
        }

        return entry;
    }

    private static string ResolveSource(TrafficLogEntryViewModel entry) =>
        entry.CaptureSource switch
        {
            TrafficCaptureSource.Proxy => "proxy",
            TrafficCaptureSource.AiFetch => "AiFetch",
            _ => "browser"
        };

    private static TrafficCaptureSource ParseCaptureSource(string? source, string browserTabId)
    {
        if (string.Equals(source, "AiFetch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "ai-fetch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(browserTabId, AiFetchTrafficSource.BrowserTabId, StringComparison.Ordinal))
            return TrafficCaptureSource.AiFetch;

        if (string.Equals(source, "proxy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(browserTabId, ProxyTrafficSource.BrowserTabId, StringComparison.Ordinal))
            return TrafficCaptureSource.Proxy;

        return TrafficCaptureSource.Browser;
    }

    private static WebTrafficResourceContext ParseResourceContext(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? WebTrafficResourceContext.Unknown
            : Enum.TryParse<WebTrafficResourceContext>(raw, ignoreCase: true, out var parsed)
                ? parsed
                : WebTrafficResourceContext.Unknown;

    private static string GetExtension(Uri? uri)
    {
        if (uri is null)
            return string.Empty;
        return Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
    }

    private static int? ParseStatusCode(string statusText)
    {
        var first = statusText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, out var code) ? code : null;
    }

    private static string GetString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static byte[]? GetBytes(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
            return null;

        return (byte[])reader.GetValue(ordinal);
    }

    private static int GetInt32(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
    }

    private static long? GetNullableInt64(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static int? GetNullableInt32(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static bool HasColumn(SqliteDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private sealed record PendingBodyUpdate(
        string RequestBody,
        string ResponseBody,
        byte[]? RequestBodyRaw,
        byte[]? ResponseBodyRaw,
        bool? FingerprintEligible,
        int? ResponseBodyLength);

    private static bool IsEmptyBodyPayload(
        string requestBody,
        string responseBody,
        byte[]? requestBodyRaw,
        byte[]? responseBodyRaw) =>
        string.IsNullOrEmpty(requestBody)
        && string.IsNullOrEmpty(responseBody)
        && requestBodyRaw is null
        && responseBodyRaw is null;

    private void QueuePendingBodyUpdate(
        string entryId,
        string requestBody,
        string responseBody,
        byte[]? requestBodyRaw,
        byte[]? responseBodyRaw,
        bool? fingerprintEligible,
        int? responseBodyLength)
    {
        if (IsEmptyBodyPayload(requestBody, responseBody, requestBodyRaw, responseBodyRaw))
            return;

        lock (_pendingBodyGate)
        {
            _pendingBodyUpdates[entryId] = new PendingBodyUpdate(
                requestBody,
                responseBody,
                requestBodyRaw,
                responseBodyRaw,
                fingerprintEligible,
                responseBodyLength);
        }
    }

    private void RemovePendingBodyUpdate(string entryId)
    {
        lock (_pendingBodyGate)
            _pendingBodyUpdates.Remove(entryId);
    }

    private void ApplyPendingBodyToEntry(TrafficLogEntryViewModel entry)
    {
        PendingBodyUpdate? pending;
        lock (_pendingBodyGate)
        {
            if (!_pendingBodyUpdates.TryGetValue(entry.EntryId, out pending))
                return;
        }

        if (!string.IsNullOrEmpty(pending.RequestBody) || pending.RequestBodyRaw is not null)
        {
            entry.RequestBody = pending.RequestBody;
            entry.RequestBodyRaw = pending.RequestBodyRaw;
        }

        if (!string.IsNullOrEmpty(pending.ResponseBody) || pending.ResponseBodyRaw is not null)
        {
            entry.ResponseBody = pending.ResponseBody;
            entry.ResponseBodyRaw = pending.ResponseBodyRaw;
        }
    }

    private async Task FlushPendingBodyAsync(
        SqliteConnection connection,
        string entryId,
        CancellationToken cancellationToken)
    {
        PendingBodyUpdate? pending;
        lock (_pendingBodyGate)
        {
            if (!_pendingBodyUpdates.TryGetValue(entryId, out pending))
                return;
        }

        await using var command = connection.CreateCommand();
        if (pending.FingerprintEligible is not null || pending.ResponseBodyLength is not null)
        {
            command.CommandText = $"""
                UPDATE "{TableName}"
                SET request_body = $request_body,
                    response_body = $response_body,
                    request_body_raw = COALESCE($request_body_raw, request_body_raw),
                    response_body_raw = COALESCE($response_body_raw, response_body_raw),
                    fingerprint_eligible = COALESCE($fingerprint_eligible, fingerprint_eligible),
                    response_body_len = COALESCE($response_body_len, response_body_len)
                WHERE entry_id = $entry_id
                """;
            command.Parameters.AddWithValue("$fingerprint_eligible",
                pending.FingerprintEligible is true ? 1 : pending.FingerprintEligible is false ? 0 : DBNull.Value);
            command.Parameters.AddWithValue("$response_body_len",
                pending.ResponseBodyLength is int len ? len : DBNull.Value);
        }
        else
        {
            command.CommandText = $"""
                UPDATE "{TableName}"
                SET request_body = $request_body,
                    response_body = $response_body,
                    request_body_raw = COALESCE($request_body_raw, request_body_raw),
                    response_body_raw = COALESCE($response_body_raw, response_body_raw)
                WHERE entry_id = $entry_id
                """;
        }

        command.Parameters.AddWithValue("$request_body", pending.RequestBody ?? string.Empty);
        command.Parameters.AddWithValue("$response_body", pending.ResponseBody ?? string.Empty);
        command.Parameters.AddWithValue("$request_body_raw", (object?)pending.RequestBodyRaw ?? DBNull.Value);
        command.Parameters.AddWithValue("$response_body_raw", (object?)pending.ResponseBodyRaw ?? DBNull.Value);
        command.Parameters.AddWithValue("$entry_id", entryId);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows > 0)
            RemovePendingBodyUpdate(entryId);
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<ProjectOpenedEvent>(OnProjectOpened);
        _dbGate.Dispose();
    }
}
