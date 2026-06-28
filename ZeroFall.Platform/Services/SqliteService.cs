using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZeroFall.Platform.Services;
using Microsoft.Data.Sqlite;

namespace ZeroFall.Platform.Services;

public class SqliteService : ISqliteService
{
    public async Task<IReadOnlyList<string>> GetTableNamesAsync(string filePath)
    {
        var tables = new List<string>();
        var connectionString = $"Data Source={filePath}";

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT name FROM sqlite_master 
            WHERE type='table' 
            AND name NOT LIKE 'sqlite_%' 
            AND name NOT IN ('DataSources', 'Groups', 'DataSourceTree')
            ORDER BY name";
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public async Task<SqlQueryResult> ExecuteQueryAsync(string filePath, string sql)
    {
        var result = new SqlQueryResult();
        var connectionString = $"Data Source={filePath}";

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        try
        {
            using var reader = await command.ExecuteReaderAsync();

            var columns = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            var rows = new List<IReadOnlyList<object?>>();
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }

            result = new SqlQueryResult
            {
                Columns = columns,
                Rows = rows,
                RowCount = rows.Count
            };
        }
        catch (Exception ex)
        {
            result = new SqlQueryResult { Error = ex.Message };
        }

        return result;
    }

    public async Task<SqlQueryResult> ExecuteQueryPagedAsync(string filePath, string sql, int offset, int limit)
    {
        return await ExecuteQueryAsync(filePath, $"{sql} LIMIT {limit} OFFSET {offset}");
    }

    public async Task<long> GetQueryRowCountAsync(string filePath, string sql)
    {
        var connectionString = $"Data Source={filePath}";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM ({sql})";
        try
        {
            var result = await command.ExecuteScalarAsync();
            return result != null ? Convert.ToInt64(result) : 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SqliteService] GetQueryRowCountAsync failed: {ex.Message}, SQL: {sql}, File: {filePath}");
            return 0;
        }
    }

    public async Task<int> ExecuteNonQueryAsync(string filePath, string sql)
    {
        var connectionString = $"Data Source={filePath}";

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<long> GetTableRowCountAsync(string filePath, string tableName)
    {
        var connectionString = $"Data Source={filePath}";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var escapedName = tableName.Replace("\"", "\"\"");
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{escapedName}\"";
        var result = await command.ExecuteScalarAsync();
        return result != null ? Convert.ToInt64(result) : 0;
    }

    public async Task<SqlTableResult> GetTableDataPagedAsync(string filePath, string tableName, int offset, int limit)
    {
        var escapedName = tableName.Replace("\"", "\"\"");
        var queryResult = await ExecuteQueryAsync(filePath, $"SELECT * FROM \"{escapedName}\" LIMIT {limit} OFFSET {offset}");

        return new SqlTableResult
        {
            Columns = queryResult.Columns,
            Rows = queryResult.Rows,
            RowCount = queryResult.RowCount,
            Error = queryResult.Error
        };
    }

    public async Task CreateTableAsync(string databasePath, string tableName, IReadOnlyList<string> columns)
    {
        var columnDefs = string.Join(", ", columns.Select((c, i) => $"\"{c}\" TEXT"));
        var sql = $"CREATE TABLE IF NOT EXISTS \"{tableName}\" ({columnDefs})";
        await ExecuteNonQueryAsync(databasePath, sql);
    }

    public async Task InsertBatchAsync(string databasePath, string tableName, IEnumerable<string[]> rows)
    {
        var connectionString = $"Data Source={databasePath}";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            foreach (var row in rows)
            {
                var placeholders = string.Join(", ", row.Select((_, i) => $"${i + 1}"));
                var sql = $"INSERT INTO \"{tableName}\" VALUES ({placeholders})";

                using var command = connection.CreateCommand();
                command.CommandText = sql;
                for (var i = 0; i < row.Length; i++)
                {
                    command.Parameters.AddWithValue($"${i + 1}", row[i]);
                }
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
