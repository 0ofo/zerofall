using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using Microsoft.Data.Sqlite;

namespace ZeroFall.Browser.Services;

/// <summary>
/// 指纹识别过程审计日志：写入项目 <c>.zerofall.db</c>，记录分引擎命中、rollup 决策与流量上下文，
/// 供后期用 SQL 比对各引擎噪声率、一致率与 rollup 影响。
/// </summary>
public sealed class FingerprintAuditJournalService : IDisposable
{
    public const string RunsTable = "fingerprint_audit_runs";
    public const string HitsTable = "fingerprint_audit_hits";

    private readonly IEventBus _eventBus;
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private readonly ConcurrentQueue<FingerprintAuditRecord> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerLoop;
    private string _databasePath = string.Empty;
    private bool _schemaReady;

    public FingerprintAuditJournalService(IEventBus eventBus)
    {
        _eventBus = eventBus;
        _eventBus.Subscribe<ProjectOpenedEvent>(OnProjectOpened);
        _writerLoop = Task.Run(WriterLoopAsync);
    }

    public bool HasDatabase => !string.IsNullOrWhiteSpace(_databasePath);

    public void Enqueue(FingerprintAuditRecord record)
    {
        if (record is null)
            return;
        _pending.Enqueue(record);
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
            command.CommandText = $"""
                DELETE FROM "{HitsTable}";
                DELETE FROM "{RunsTable}";
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<IReadOnlyList<FingerprintEngineQualityRow>> QueryEngineQualityAsync(
        string? rootAuthority = null,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return [];

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT
                    h.engine,
                    COUNT(DISTINCT h.run_id) AS run_count,
                    COUNT(*) AS hit_rows,
                    COUNT(DISTINCT h.framework_name) AS unique_frameworks,
                    ROUND(CAST(COUNT(*) AS REAL) / NULLIF(COUNT(DISTINCT h.run_id), 0), 3) AS hits_per_run,
                    SUM(CASE WHEN h.rolled_up = 1 THEN 1 ELSE 0 END) AS rolled_up_hits,
                    ROUND(
                        CAST(SUM(CASE WHEN h.rolled_up = 1 THEN 1 ELSE 0 END) AS REAL)
                        / NULLIF(COUNT(*), 0),
                        3) AS rollup_rate
                FROM "{HitsTable}" h
                JOIN "{RunsTable}" r ON r.run_id = h.run_id
                WHERE ($root = '' OR r.root_authority = $root)
                GROUP BY h.engine
                ORDER BY hits_per_run ASC, hit_rows DESC
                """;
            command.Parameters.AddWithValue("$root", rootAuthority ?? string.Empty);

            var rows = new List<FingerprintEngineQualityRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new FingerprintEngineQualityRow
                {
                    Engine = reader.GetString(0),
                    RunCount = reader.GetInt32(1),
                    HitRows = reader.GetInt32(2),
                    UniqueFrameworks = reader.GetInt32(3),
                    HitsPerRun = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                    RolledUpHits = reader.GetInt32(5),
                    RollupRate = reader.IsDBNull(6) ? 0 : reader.GetDouble(6)
                });
            }

            return rows;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<IReadOnlyList<FingerprintEngineAgreementRow>> QueryEngineAgreementAsync(
        int minEngineCount = 2,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return [];

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT
                    framework_name,
                    GROUP_CONCAT(DISTINCT engine) AS engines,
                    COUNT(DISTINCT engine) AS engine_count,
                    COUNT(*) AS hit_rows
                FROM "{HitsTable}"
                GROUP BY framework_name
                HAVING COUNT(DISTINCT engine) >= $minEngines
                ORDER BY engine_count DESC, hit_rows DESC
                LIMIT 200
                """;
            command.Parameters.AddWithValue("$minEngines", Math.Max(2, minEngineCount));

            var rows = new List<FingerprintEngineAgreementRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new FingerprintEngineAgreementRow
                {
                    FrameworkName = reader.GetString(0),
                    Engines = reader.GetString(1),
                    EngineCount = reader.GetInt32(2),
                    HitRows = reader.GetInt32(3)
                });
            }

            return rows;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private void OnProjectOpened(ProjectOpenedEvent e)
    {
        _databasePath = e.DatabasePath ?? string.Empty;
        _schemaReady = false;
        _ = EnsureSchemaAsync();
    }

    private async Task WriterLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(120, _cts.Token).ConfigureAwait(false);
                await FlushPendingAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // 审计写入失败不影响指纹识别
            }
        }

        try
        {
            await FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // shutdown flush best-effort
        }
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        if (_pending.IsEmpty)
            return;
        if (!await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false))
            return;

        var batch = new List<FingerprintAuditRecord>(64);
        while (batch.Count < 64 && _pending.TryDequeue(out var item))
            batch.Add(item);

        if (batch.Count == 0)
            return;

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in batch)
                await InsertRecordAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dbGate.Release();
        }

        if (!_pending.IsEmpty)
            await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FingerprintAuditRecord record,
        CancellationToken cancellationToken)
    {
        var capturedAt = DateTime.UtcNow.ToString("O");
        var rawJson = SerializeStringListMap(record.RawHitsByEngine);
        var appliedJson = SerializeStringListMap(record.AppliedHitsByEngine);
        var enabled = string.Join(',', record.EnabledEngines);
        var mergedJson = SerializeStringList(record.MergedToRequestHost);
        var rollupJson = SerializeStringList(record.RolledUpToRoot);
        var requestOnlyJson = SerializeStringList(record.RequestHostOnly);
        var contextJson = new JsonObject
        {
            ["pageTitle"] = record.PageTitle,
            ["serverHeader"] = record.ServerHeader
        }.ToJsonString();

        long runId;
        await using (var insertRun = connection.CreateCommand())
        {
            insertRun.Transaction = transaction;
            insertRun.CommandText = $"""
                INSERT INTO "{RunsTable}" (
                    captured_at_utc, trigger, entry_id, browser_tab_id,
                    url, top_level_url, request_authority, root_authority, is_cross_host,
                    asset_kind, resource_context, status, content_type, body_bytes, header_bytes,
                    enabled_engines, duration_ms,
                    raw_hits_json, applied_hits_json,
                    merged_frameworks_json, rollup_frameworks_json, request_only_json,
                    context_json
                ) VALUES (
                    $captured_at_utc, $trigger, $entry_id, $browser_tab_id,
                    $url, $top_level_url, $request_authority, $root_authority, $is_cross_host,
                    $asset_kind, $resource_context, $status, $content_type, $body_bytes, $header_bytes,
                    $enabled_engines, $duration_ms,
                    $raw_hits_json, $applied_hits_json,
                    $merged_frameworks_json, $rollup_frameworks_json, $request_only_json,
                    $context_json
                );
                SELECT last_insert_rowid();
                """;
            insertRun.Parameters.AddWithValue("$captured_at_utc", capturedAt);
            insertRun.Parameters.AddWithValue("$trigger", record.Trigger);
            insertRun.Parameters.AddWithValue("$entry_id", record.EntryId);
            insertRun.Parameters.AddWithValue("$browser_tab_id", record.BrowserTabId);
            insertRun.Parameters.AddWithValue("$url", record.Url);
            insertRun.Parameters.AddWithValue("$top_level_url", record.TopLevelUrl);
            insertRun.Parameters.AddWithValue("$request_authority", record.RequestAuthority);
            insertRun.Parameters.AddWithValue("$root_authority", record.RootAuthority);
            insertRun.Parameters.AddWithValue("$is_cross_host", record.IsCrossHost ? 1 : 0);
            insertRun.Parameters.AddWithValue("$asset_kind", record.AssetKind.ToString());
            insertRun.Parameters.AddWithValue("$resource_context", record.ResourceContext.ToStorageKey());
            insertRun.Parameters.AddWithValue("$status", record.Status);
            insertRun.Parameters.AddWithValue("$content_type", record.ContentType);
            insertRun.Parameters.AddWithValue("$body_bytes", record.BodyBytes);
            insertRun.Parameters.AddWithValue("$header_bytes", record.HeaderBytes);
            insertRun.Parameters.AddWithValue("$enabled_engines", enabled);
            insertRun.Parameters.AddWithValue("$duration_ms", record.DurationMs);
            insertRun.Parameters.AddWithValue("$raw_hits_json", rawJson);
            insertRun.Parameters.AddWithValue("$applied_hits_json", appliedJson);
            insertRun.Parameters.AddWithValue("$merged_frameworks_json", mergedJson);
            insertRun.Parameters.AddWithValue("$rollup_frameworks_json", rollupJson);
            insertRun.Parameters.AddWithValue("$request_only_json", requestOnlyJson);
            insertRun.Parameters.AddWithValue("$context_json", contextJson);
            runId = (long)(await insertRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        if (runId <= 0)
            return;

        foreach (var (engine, names) in record.AppliedHitsByEngine)
        {
            foreach (var displayName in names)
            {
                var rolledUp = record.RolledUpToRoot.Any(x =>
                    string.Equals(x, displayName, StringComparison.OrdinalIgnoreCase));
                await using var insertHit = connection.CreateCommand();
                insertHit.Transaction = transaction;
                insertHit.CommandText = $"""
                    INSERT INTO "{HitsTable}" (
                        run_id, engine, framework_name, framework_version, rolled_up
                    ) VALUES (
                        $run_id, $engine, $framework_name, $framework_version, $rolled_up
                    );
                    """;
                insertHit.Parameters.AddWithValue("$run_id", runId);
                insertHit.Parameters.AddWithValue("$engine", engine);
                SplitDisplayName(displayName, out var name, out var version);
                insertHit.Parameters.AddWithValue("$framework_name", name);
                insertHit.Parameters.AddWithValue("$framework_version", version);
                insertHit.Parameters.AddWithValue("$rolled_up", rolledUp ? 1 : 0);
                await insertHit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string SerializeStringList(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array.ToJsonString();
    }

    private static string SerializeStringListMap(IReadOnlyDictionary<string, IReadOnlyList<string>> map)
    {
        var obj = new JsonObject();
        foreach (var (key, values) in map)
        {
            var array = new JsonArray();
            foreach (var value in values)
                array.Add(value);
            obj[key] = array;
        }
        return obj.ToJsonString();
    }

    private static void SplitDisplayName(string displayName, out string name, out string version)
    {
        var colon = displayName.IndexOf(':');
        if (colon <= 0)
        {
            name = displayName;
            version = string.Empty;
            return;
        }

        name = displayName[..colon];
        version = displayName[(colon + 1)..];
    }

    private async Task<bool> EnsureSchemaAsync(CancellationToken cancellationToken = default)
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

            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            var emptyJsonObject = "{}";
            command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS "{RunsTable}" (
                    run_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    captured_at_utc TEXT NOT NULL,
                    trigger TEXT NOT NULL,
                    entry_id TEXT NOT NULL DEFAULT '',
                    browser_tab_id TEXT NOT NULL DEFAULT '',
                    url TEXT NOT NULL,
                    top_level_url TEXT NOT NULL DEFAULT '',
                    request_authority TEXT NOT NULL,
                    root_authority TEXT NOT NULL,
                    is_cross_host INTEGER NOT NULL DEFAULT 0,
                    asset_kind TEXT NOT NULL,
                    resource_context TEXT NOT NULL DEFAULT '',
                    status TEXT NOT NULL DEFAULT '',
                    content_type TEXT NOT NULL DEFAULT '',
                    body_bytes INTEGER NOT NULL DEFAULT 0,
                    header_bytes INTEGER NOT NULL DEFAULT 0,
                    enabled_engines TEXT NOT NULL DEFAULT '',
                    duration_ms INTEGER NOT NULL DEFAULT 0,
                    raw_hits_json TEXT NOT NULL DEFAULT '{emptyJsonObject}',
                    applied_hits_json TEXT NOT NULL DEFAULT '{emptyJsonObject}',
                    merged_frameworks_json TEXT NOT NULL DEFAULT '[]',
                    rollup_frameworks_json TEXT NOT NULL DEFAULT '[]',
                    request_only_json TEXT NOT NULL DEFAULT '[]',
                    context_json TEXT NOT NULL DEFAULT '{emptyJsonObject}'
                );
                CREATE INDEX IF NOT EXISTS idx_fp_audit_runs_root ON "{RunsTable}"(root_authority, captured_at_utc);
                CREATE INDEX IF NOT EXISTS idx_fp_audit_runs_entry ON "{RunsTable}"(entry_id);

                CREATE TABLE IF NOT EXISTS "{HitsTable}" (
                    hit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id INTEGER NOT NULL,
                    engine TEXT NOT NULL,
                    framework_name TEXT NOT NULL,
                    framework_version TEXT NOT NULL DEFAULT '',
                    rolled_up INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY(run_id) REFERENCES "{RunsTable}"(run_id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_fp_audit_hits_engine ON "{HitsTable}"(engine, framework_name);
                CREATE INDEX IF NOT EXISTS idx_fp_audit_hits_run ON "{HitsTable}"(run_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "resource_context", "TEXT NOT NULL DEFAULT ''", cancellationToken)
                .ConfigureAwait(false);
            _schemaReady = true;
            return true;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info(\"{RunsTable}\")";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                existing.Add(reader.GetString(1));
        }

        if (existing.Contains(columnName))
            return;

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{RunsTable}\" ADD COLUMN {columnName} {columnDefinition}";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _writerLoop.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignore
        }

        _cts.Dispose();
        _dbGate.Dispose();
    }
}

/// <summary>常用 SQL 片段，可在 SQL 编辑器中对 <c>.zerofall.db</c> 直接运行。</summary>
public static class FingerprintAuditSql
{
    public const string EngineQuality = """
        -- 各引擎噪声率粗判：hits_per_run 越低通常越稳；rollup_rate 高说明跨 host 污染主站多
        SELECT
            h.engine,
            COUNT(DISTINCT h.run_id) AS runs,
            COUNT(*) AS hits,
            COUNT(DISTINCT h.framework_name) AS uniq_frameworks,
            ROUND(CAST(COUNT(*) AS REAL) / NULLIF(COUNT(DISTINCT h.run_id), 0), 3) AS hits_per_run,
            ROUND(CAST(SUM(h.rolled_up) AS REAL) / NULLIF(COUNT(*), 0), 3) AS rollup_rate
        FROM fingerprint_audit_hits h
        GROUP BY h.engine
        ORDER BY hits_per_run ASC;
        """;

    public const string EngineAgreement = """
        -- 多引擎一致命中：engine_count 越高，可信度通常越高
        SELECT
            framework_name,
            GROUP_CONCAT(DISTINCT engine) AS engines,
            COUNT(DISTINCT engine) AS engine_count,
            COUNT(*) AS hit_rows
        FROM fingerprint_audit_hits
        GROUP BY framework_name
        HAVING COUNT(DISTINCT engine) >= 2
        ORDER BY engine_count DESC, hit_rows DESC
        LIMIT 100;
        """;

    public const string HitsByResourceContext = """
        -- 按 WebView2 ResourceContext 统计命中（收窄指纹范围后 Document 应占主导）
        SELECT
            COALESCE(NULLIF(r.resource_context, ''), 'Unknown') AS resource_context,
            COUNT(DISTINCT r.run_id) AS runs,
            COUNT(h.hit_id) AS hits
        FROM fingerprint_audit_runs r
        LEFT JOIN fingerprint_audit_hits h ON h.run_id = r.run_id
        GROUP BY resource_context
        ORDER BY runs DESC;
        """;

    public const string NoisyOnNonHtml = """
        -- 在非文档资源上仍大量命中的框架（疑似规则过泛；优先看 resource_context != Document）
        SELECT
            h.engine,
            h.framework_name,
            COALESCE(NULLIF(r.resource_context, ''), 'Unknown') AS resource_context,
            COUNT(*) AS hits
        FROM fingerprint_audit_hits h
        JOIN fingerprint_audit_runs r ON r.run_id = h.run_id
        WHERE r.resource_context != 'Document' AND r.resource_context != ''
        GROUP BY h.engine, h.framework_name, resource_context
        ORDER BY hits DESC
        LIMIT 100;
        """;

    public const string RollupPollution = """
        -- 被 rollup 到主站最多的框架（跨 host 污染排查）
        SELECT
            r.root_authority,
            h.engine,
            h.framework_name,
            COUNT(*) AS rollup_hits
        FROM fingerprint_audit_hits h
        JOIN fingerprint_audit_runs r ON r.run_id = h.run_id
        WHERE h.rolled_up = 1
        GROUP BY r.root_authority, h.engine, h.framework_name
        ORDER BY rollup_hits DESC
        LIMIT 100;
        """;
}
