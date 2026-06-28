using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Datafinder.Base.Data;
using Datafinder.Platform.Models;
using MySqlConnector;

namespace Datafinder.Platform.Services.RelationalDb;

public sealed class MySqlRelationalDbBrowser : IRelationalDbBrowser
{
    public RelationalDbKind Kind => RelationalDbKind.MySql;

    public bool CanHandle(string connectionReference) =>
        DatabaseConnectionFiles.IsMySqlConnectionFile(connectionReference);

    public async Task<IReadOnlyList<string>> GetTableNamesAsync(string connectionReference)
    {
        var tables = await GetTablesAsync(connectionReference).ConfigureAwait(false);
        return tables.Select(t => t.Reference).ToList();
    }

    public async Task<IReadOnlyList<RelationalTableEntry>> GetTablesAsync(string connectionReference)
    {
        var config = MySqlConnectionConfig.Load(connectionReference);
        await using var connection = new MySqlConnection(config.BuildConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        var hasDatabase = !string.IsNullOrWhiteSpace(config.Database);
        if (hasDatabase)
        {
            command.CommandText = """
                SELECT TABLE_NAME
                FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = @schema AND TABLE_TYPE = 'BASE TABLE'
                ORDER BY TABLE_NAME
                """;
            command.Parameters.AddWithValue("@schema", config.Database);
        }
        else
        {
            command.CommandText = """
                SELECT TABLE_SCHEMA, TABLE_NAME
                FROM information_schema.TABLES
                WHERE TABLE_TYPE = 'BASE TABLE'
                  AND TABLE_SCHEMA NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
                ORDER BY TABLE_SCHEMA, TABLE_NAME
                """;
        }

        var raw = new List<(string Schema, string Table)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (hasDatabase)
                raw.Add((config.Database, reader.GetString(0)));
            else
                raw.Add((reader.GetString(0), reader.GetString(1)));
        }

        return raw.Select(x => new RelationalTableEntry
        {
            Schema = x.Schema,
            DisplayName = x.Table,
            Reference = hasDatabase ? x.Table : $"{x.Schema}.{x.Table}"
        }).ToList();
    }

    public async Task<RelationalTablePage> GetTablePageAsync(string connectionReference, string tableName, int offset, int limit)
    {
        try
        {
            var config = MySqlConnectionConfig.Load(connectionReference);
            var (schema, table) = ResolveTableReference(config, tableName);
            await using var connection = new MySqlConnection(config.BuildConnectionString());
            await connection.OpenAsync();

            var escapedSchema = EscapeMySqlIdentifier(schema);
            var escapedTable = EscapeMySqlIdentifier(table);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT * FROM `{escapedSchema}`.`{escapedTable}`
                LIMIT @limit OFFSET @offset
                """;
            command.Parameters.AddWithValue("@limit", limit);
            command.Parameters.AddWithValue("@offset", offset);

            await using var reader = await command.ExecuteReaderAsync();
            var columns = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            var rows = new List<IReadOnlyList<object?>>();
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            return new RelationalTablePage
            {
                Columns = columns,
                Rows = rows,
                RowCount = rows.Count
            };
        }
        catch (Exception ex)
        {
            return new RelationalTablePage { Error = ex.Message };
        }
    }

    public async Task<long> GetTableRowCountAsync(string connectionReference, string tableName)
    {
        var config = MySqlConnectionConfig.Load(connectionReference);
        var (schema, table) = ResolveTableReference(config, tableName);
        await using var connection = new MySqlConnection(config.BuildConnectionString());
        await connection.OpenAsync();

        var escapedSchema = EscapeMySqlIdentifier(schema);
        var escapedTable = EscapeMySqlIdentifier(table);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*) FROM `{escapedSchema}`.`{escapedTable}`
            """;

        var result = await command.ExecuteScalarAsync();
        return result switch
        {
            null => 0,
            long l => l,
            int i => i,
            decimal d => (long)d,
            _ => Convert.ToInt64(result)
        };
    }

    public async Task TestConnectionAsync(string connectionReference)
    {
        var config = MySqlConnectionConfig.Load(connectionReference);
        await using var connection = new MySqlConnection(config.BuildConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        _ = await command.ExecuteScalarAsync();
    }

    public Task<RelationalQueryResult> ExecuteQueryAsync(string connectionReference, string sql) =>
        ReadQueryAsync(connectionReference, sql);

    public async Task<RelationalTablePage> ExecuteQueryPageAsync(string connectionReference, string sql, int offset, int limit)
    {
        var trimmed = TrimSql(sql);
        return await ReadQueryPageAsync(connectionReference, $"{trimmed} LIMIT {limit} OFFSET {offset}");
    }

    public async Task<long> ExecuteQueryRowCountAsync(string connectionReference, string sql)
    {
        var trimmed = TrimSql(sql);
        var config = MySqlConnectionConfig.Load(connectionReference);
        await using var connection = new MySqlConnection(config.BuildConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM ({trimmed}) AS _df_count";
        var result = await command.ExecuteScalarAsync();
        return result switch
        {
            null => 0,
            long l => l,
            int i => i,
            decimal d => (long)d,
            _ => Convert.ToInt64(result)
        };
    }

    public async Task<int> ExecuteNonQueryAsync(string connectionReference, string sql)
    {
        var config = MySqlConnectionConfig.Load(connectionReference);
        await using var connection = new MySqlConnection(config.BuildConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<RelationalQueryResult> ReadQueryAsync(string connectionReference, string sql)
    {
        try
        {
            var page = await ReadQueryPageAsync(connectionReference, sql);
            if (page.Error != null)
                return new RelationalQueryResult { Error = page.Error };

            return new RelationalQueryResult
            {
                Columns = page.Columns,
                Rows = page.Rows,
                RowCount = page.RowCount
            };
        }
        catch (Exception ex)
        {
            return new RelationalQueryResult { Error = ex.Message };
        }
    }

    private static async Task<RelationalTablePage> ReadQueryPageAsync(string connectionReference, string sql)
    {
        try
        {
            var config = MySqlConnectionConfig.Load(connectionReference);
            await using var connection = new MySqlConnection(config.BuildConnectionString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();

            var columns = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            var rows = new List<IReadOnlyList<object?>>();
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            return new RelationalTablePage
            {
                Columns = columns,
                Rows = rows,
                RowCount = rows.Count
            };
        }
        catch (Exception ex)
        {
            return new RelationalTablePage { Error = ex.Message };
        }
    }

    private static string TrimSql(string sql) =>
        sql.TrimEnd(';', ' ', '\n', '\r').Trim();

    private static (string Schema, string Table) ResolveTableReference(MySqlConnectionConfig config, string tableName)
    {
        var dot = tableName.IndexOf('.');
        if (dot > 0 && dot < tableName.Length - 1)
            return (tableName[..dot], tableName[(dot + 1)..]);

        if (string.IsNullOrWhiteSpace(config.Database))
            throw new InvalidOperationException("未指定数据库，请在连接配置中填写 database，或选择带 schema 前缀的表（如 mydb.users）。");

        return (config.Database, tableName);
    }

    private static string EscapeMySqlIdentifier(string identifier) =>
        identifier.Replace("`", "``", StringComparison.Ordinal);
}
