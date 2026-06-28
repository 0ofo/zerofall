using System.Collections.Generic;
using System.Threading.Tasks;

namespace ZeroFall.Base.Data;

/// <summary>关系型数据库浏览（表列表、分页读表）。SQLite / MySQL / PostgreSQL / SQL Server 各自实现。</summary>
public interface IRelationalDbBrowser
{
    RelationalDbKind Kind { get; }

    bool CanHandle(string connectionReference);

    Task<IReadOnlyList<string>> GetTableNamesAsync(string connectionReference);

    /// <summary>侧边栏表节点：DisplayName 为展示名，Reference 为查询用完整表标识。</summary>
    Task<IReadOnlyList<RelationalTableEntry>> GetTablesAsync(string connectionReference);

    Task<RelationalTablePage> GetTablePageAsync(string connectionReference, string tableName, int offset, int limit);

    Task<long> GetTableRowCountAsync(string connectionReference, string tableName);

    Task TestConnectionAsync(string connectionReference);

    Task<RelationalQueryResult> ExecuteQueryAsync(string connectionReference, string sql);

    Task<RelationalTablePage> ExecuteQueryPageAsync(string connectionReference, string sql, int offset, int limit);

    Task<long> ExecuteQueryRowCountAsync(string connectionReference, string sql);

    Task<int> ExecuteNonQueryAsync(string connectionReference, string sql);
}

public enum RelationalDbKind
{
    Sqlite,
    MySql,
    PostgreSql,
    SqlServer
}

public sealed class RelationalTableEntry
{
    public string Schema { get; init; } = "";
    public required string DisplayName { get; init; }
    public required string Reference { get; init; }
}

public class RelationalTablePage
{
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = [];
    public long RowCount { get; init; }
    public string? Error { get; init; }
}

public class RelationalQueryResult
{
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = [];
    public long RowCount { get; init; }
    public string? Error { get; init; }
}

public interface IRelationalDbBrowserRegistry
{
    IRelationalDbBrowser? Resolve(string connectionReference);
}
