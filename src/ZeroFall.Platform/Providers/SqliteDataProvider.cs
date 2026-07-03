using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ZeroFall.Base.Data;
using ZeroFall.Platform.Services;

namespace ZeroFall.Platform.Providers;

public class SourceMeta
{
    public string SourceType { get; init; } = "sqlite";
    public string FilePath { get; init; } = "";
    public bool CanEdit { get; init; } = true;
    public bool CanWriteBack { get; init; }
    public string WriteBackFormat { get; init; } = "";
    public bool IsDirty { get; set; }
}

public class SqliteDataProvider : IDataProvider
{
    private readonly ISqliteService _sqliteService;
    private readonly IFileIndexService? _fileIndexService;
    private readonly SourceMeta _meta;
    private IReadOnlyList<string>? _columns;
    private long? _cachedTotalCount;
    private bool _disposed;

    private SqliteDataProvider(ISqliteService sqliteService, IFileIndexService? fileIndexService,
        string databasePath, string querySource, bool isTableMode, SourceMeta meta)
    {
        _sqliteService = sqliteService;
        _fileIndexService = fileIndexService;
        DatabasePath = databasePath;
        QuerySource = querySource;
        _isTableMode = isTableMode;
        _meta = meta;
    }

    private readonly bool _isTableMode;

    public string Title { get; init; } = "";
    public string DatabasePath { get; }
    public string QuerySource { get; }
    public bool SupportsTotalCount => true;
    public bool CanEdit => _meta.CanEdit;
    public bool CanWriteBack => _meta.CanWriteBack;
    public bool IsDirty => _meta.IsDirty;

    public IReadOnlyList<string> Columns
    {
        get
        {
            if (_columns != null) return _columns;
            if (_isTableMode)
            {
                var result = _sqliteService.GetTableDataPagedAsync(DatabasePath, QuerySource, 0, 1).GetAwaiter().GetResult();
                _columns = result.Columns;
            }
            else
            {
                var result = _sqliteService.ExecuteQueryPagedAsync(DatabasePath, QuerySource, 0, 1).GetAwaiter().GetResult();
                _columns = result.Columns;
            }
            return _columns ?? Array.Empty<string>();
        }
    }

    public static SqliteDataProvider ForNativeTable(ISqliteService svc, string dbPath, string tableName)
    {
        return new SqliteDataProvider(svc, null, dbPath, tableName, true, new SourceMeta
        {
            SourceType = "sqlite",
            CanEdit = true,
            CanWriteBack = false
        })
        { Title = tableName };
    }

    public static SqliteDataProvider ForIndexedFile(ISqliteService svc, IFileIndexService idxSvc,
        string dbPath, string tableName, string filePath, string sourceType, string writeBackFormat)
    {
        return new SqliteDataProvider(svc, idxSvc, dbPath, tableName, true, new SourceMeta
        {
            SourceType = sourceType,
            FilePath = filePath,
            CanEdit = true,
            CanWriteBack = true,
            WriteBackFormat = writeBackFormat
        })
        { Title = tableName };
    }

    public static readonly string[] AssetReconDefaultColumns = AssetReconFieldCatalog.DefaultColumnNames;

    public static SqliteDataProvider ForApiResult(ISqliteService svc, string dbPath, string tableName, string title,
        string[]? displayColumns = null, string? whereClause = null)
    {
        var where = string.IsNullOrWhiteSpace(whereClause) ? "" : $" WHERE {whereClause}";

        if (displayColumns != null && displayColumns.Length > 0)
        {
            var cols = string.Join(", ", displayColumns.Select(c => $"\"{c}\""));
            var sql = $"SELECT {cols} FROM \"{tableName}\"{where}";
            return new SqliteDataProvider(svc, null, dbPath, sql, false, new SourceMeta
            {
                SourceType = "api",
                CanEdit = false,
                CanWriteBack = false
            })
            { Title = title };
        }

        return new SqliteDataProvider(svc, null, dbPath, tableName, true, new SourceMeta
        {
            SourceType = "api",
            CanEdit = false,
            CanWriteBack = false
        })
        { Title = title };
    }

    public static SqliteDataProvider ForQuery(ISqliteService svc, string dbPath, string sql, string title)
    {
        return new SqliteDataProvider(svc, null, dbPath, sql, false, new SourceMeta
        {
            SourceType = "sqlite",
            CanEdit = false,
            CanWriteBack = false
        })
        { Title = title };
    }

    public async Task<DataPageResult> GetPageAsync(int offset, int limit)
    {
        if (_isTableMode)
        {
            var result = await _sqliteService.GetTableDataPagedAsync(DatabasePath, QuerySource, offset, limit);
            _columns ??= result.Columns;
            return new DataPageResult { Rows = result.Rows, Offset = offset, Count = (int)result.RowCount };
        }
        else
        {
            var result = await _sqliteService.ExecuteQueryPagedAsync(DatabasePath, QuerySource, offset, limit);
            _columns ??= result.Columns;
            return new DataPageResult { Rows = result.Rows, Offset = offset, Count = (int)result.RowCount };
        }
    }

    public async Task<long> GetTotalCountAsync()
    {
        if (_cachedTotalCount.HasValue) return _cachedTotalCount.Value;
        _cachedTotalCount = _isTableMode
            ? await _sqliteService.GetTableRowCountAsync(DatabasePath, QuerySource)
            : await _sqliteService.GetQueryRowCountAsync(DatabasePath, QuerySource);
        return _cachedTotalCount.Value;
    }

    public async Task<int> UpdateRowAsync(long rowId, IReadOnlyList<object?> values)
    {
        if (!CanEdit) return 0;

        var cols = Columns;
        var sets = new List<string>();
        for (var i = 0; i < values.Count && i < cols.Count; i++)
            sets.Add($"\"{cols[i]}\" = @p{i}");

        var escapedSource = QuerySource.Replace("\"", "\"\"");
        var sql = $"UPDATE \"{escapedSource}\" SET {string.Join(", ", sets)} WHERE rowid = @rowid";

        var affected = await _sqliteService.ExecuteNonQueryAsync(DatabasePath, sql);
        MarkDirty();
        _cachedTotalCount = null;
        return affected;
    }

    public async Task<int> InsertRowAsync(IReadOnlyList<object?> values)
    {
        if (!CanEdit) return 0;

        var cols = Columns;
        var colNames = string.Join(", ", cols.Take(values.Count).Select(c => $"\"{c}\""));
        var parms = string.Join(", ", cols.Take(values.Count).Select((_, i) => $"@p{i}"));
        var escapedSource = QuerySource.Replace("\"", "\"\"");
        var sql = $"INSERT INTO \"{escapedSource}\" ({colNames}) VALUES ({parms})";

        var affected = await _sqliteService.ExecuteNonQueryAsync(DatabasePath, sql);
        MarkDirty();
        _cachedTotalCount = null;
        return affected;
    }

    public async Task<int> DeleteRowAsync(long rowId)
    {
        if (!CanEdit) return 0;

        var escapedSource = QuerySource.Replace("\"", "\"\"");
        var sql = $"DELETE FROM \"{escapedSource}\" WHERE rowid = @rowid";

        var affected = await _sqliteService.ExecuteNonQueryAsync(DatabasePath, sql);
        MarkDirty();
        _cachedTotalCount = null;
        return affected;
    }

    public async Task WriteBackAsync()
    {
        if (!CanWriteBack || string.IsNullOrEmpty(_meta.FilePath)) return;

        var allData = await _sqliteService.GetTableDataPagedAsync(DatabasePath, QuerySource, 0, int.MaxValue);

        switch (_meta.WriteBackFormat)
        {
            case "csv":
                await WriteBackCsvAsync(_meta.FilePath, allData.Columns, allData.Rows);
                break;
            case "json":
                await WriteBackJsonAsync(_meta.FilePath, allData.Columns, allData.Rows);
                break;
        }

        _meta.IsDirty = false;
        var escapedTable = QuerySource.Replace("\"", "\"\"");
        await _sqliteService.ExecuteNonQueryAsync(DatabasePath,
            $"UPDATE source_registry SET is_dirty = 0 WHERE table_name = '{escapedTable}'");
    }

    public async Task RefreshFromSourceAsync()
    {
        if (_fileIndexService != null && !string.IsNullOrEmpty(_meta.FilePath))
        {
            await _fileIndexService.IndexCsvAsync(_meta.FilePath, DatabasePath);
            _meta.IsDirty = false;
        }

        _cachedTotalCount = null;
        _columns = null;
    }

    private void MarkDirty()
    {
        if (_meta.IsDirty) return;
        _meta.IsDirty = true;
        var escapedTable = QuerySource.Replace("\"", "\"\"");
        _ = _sqliteService.ExecuteNonQueryAsync(DatabasePath,
            $"UPDATE source_registry SET is_dirty = 1 WHERE table_name = '{escapedTable}'");
    }

    private static async Task WriteBackCsvAsync(string filePath,
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns));

        foreach (var row in rows)
        {
            var fields = row.Select(v => EscapeCsvField(v?.ToString() ?? ""));
            sb.AppendLine(string.Join(",", fields));
        }

        await File.WriteAllTextAsync(filePath, sb.ToString());
    }

    private static string EscapeCsvField(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            return $"\"{field.Replace("\"", "\"\"")}\"";
        return field;
    }

    private static async Task WriteBackJsonAsync(string filePath,
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartArray();

        foreach (var row in rows)
        {
            writer.WriteStartObject();
            for (var i = 0; i < columns.Count; i++)
            {
                var value = i < row.Count ? row[i] : null;
                writer.WritePropertyName(columns[i]);
                WriteValue(writer, value);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        await writer.FlushAsync();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            writer.WriteNullValue();
        }
        else switch (value)
        {
            case string s:
                writer.WriteStringValue(s);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
