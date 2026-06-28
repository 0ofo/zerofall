using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Datafinder.Base.Data;
using Datafinder.Platform.Models;

namespace Datafinder.Platform.Services.RelationalDb;

public sealed class SqliteRelationalDbBrowser(ISqliteService sqliteService) : IRelationalDbBrowser
{
    public RelationalDbKind Kind => RelationalDbKind.Sqlite;

    public bool CanHandle(string connectionReference) =>
        DatabaseConnectionFiles.IsSqliteDatabaseFile(connectionReference);

    public Task<IReadOnlyList<string>> GetTableNamesAsync(string connectionReference) =>
        sqliteService.GetTableNamesAsync(connectionReference);

    public async Task<IReadOnlyList<RelationalTableEntry>> GetTablesAsync(string connectionReference)
    {
        var names = await sqliteService.GetTableNamesAsync(connectionReference).ConfigureAwait(false);
        return names.Select(name => new RelationalTableEntry
        {
            Schema = "",
            DisplayName = name,
            Reference = name
        }).ToList();
    }

    public async Task<RelationalTablePage> GetTablePageAsync(string connectionReference, string tableName, int offset, int limit)
    {
        var result = await sqliteService.GetTableDataPagedAsync(connectionReference, tableName, offset, limit);
        return new RelationalTablePage
        {
            Columns = result.Columns,
            Rows = result.Rows,
            RowCount = result.RowCount,
            Error = result.Error
        };
    }

    public Task<long> GetTableRowCountAsync(string connectionReference, string tableName) =>
        sqliteService.GetTableRowCountAsync(connectionReference, tableName);

    public async Task TestConnectionAsync(string connectionReference)
    {
        _ = await sqliteService.GetTableNamesAsync(connectionReference);
    }

    public async Task<RelationalQueryResult> ExecuteQueryAsync(string connectionReference, string sql)
    {
        var result = await sqliteService.ExecuteQueryAsync(connectionReference, sql);
        return ToQueryResult(result.Columns, result.Rows, result.RowCount, result.Error);
    }

    public async Task<RelationalTablePage> ExecuteQueryPageAsync(string connectionReference, string sql, int offset, int limit)
    {
        var result = await sqliteService.ExecuteQueryPagedAsync(connectionReference, sql, offset, limit);
        return new RelationalTablePage
        {
            Columns = result.Columns,
            Rows = result.Rows,
            RowCount = result.RowCount,
            Error = result.Error
        };
    }

    public Task<long> ExecuteQueryRowCountAsync(string connectionReference, string sql) =>
        sqliteService.GetQueryRowCountAsync(connectionReference, sql);

    public Task<int> ExecuteNonQueryAsync(string connectionReference, string sql) =>
        sqliteService.ExecuteNonQueryAsync(connectionReference, sql);

    private static RelationalQueryResult ToQueryResult(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        long rowCount,
        string? error) =>
        new()
        {
            Columns = columns,
            Rows = rows,
            RowCount = rowCount,
            Error = error
        };
}
