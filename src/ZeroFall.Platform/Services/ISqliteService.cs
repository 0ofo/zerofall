using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ZeroFall.Platform.Services;

public interface ISqliteService
{
    Task<SqlTableResult> GetTableDataPagedAsync(string databasePath, string tableName, int offset, int limit);
    Task<long> GetTableRowCountAsync(string databasePath, string tableName);
    Task<IReadOnlyList<string>> GetTableNamesAsync(string databasePath);
    Task CreateTableAsync(string databasePath, string tableName, IReadOnlyList<string> columns);
    Task InsertBatchAsync(string databasePath, string tableName, IEnumerable<string[]> rows);
    Task<int> ExecuteNonQueryAsync(string databasePath, string sql);
    Task<SqlQueryResult> ExecuteQueryAsync(string databasePath, string sql);
    Task<SqlQueryResult> ExecuteQueryPagedAsync(string databasePath, string sql, int offset, int limit);
    Task<long> GetQueryRowCountAsync(string databasePath, string sql);
}

public class SqlTableResult
{
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = Array.Empty<IReadOnlyList<object?>>();
    public long RowCount { get; init; }
    public string? Error { get; init; }
}

public class SqlQueryResult
{
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = Array.Empty<IReadOnlyList<object?>>();
    public long RowCount { get; init; }
    public string? Error { get; init; }
}
