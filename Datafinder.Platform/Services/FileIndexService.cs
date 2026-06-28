using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Datafinder.Platform.Services;

public class FileIndexService : IFileIndexService
{
    private readonly ISqliteService _sqliteService;

    public FileIndexService(ISqliteService sqliteService)
    {
        _sqliteService = sqliteService;
    }

    public async Task<IndexResult> IndexCsvAsync(string csvFilePath, string projectDatabasePath)
    {
        if (!File.Exists(csvFilePath))
            return IndexResult.Failure("文件不存在");

        if (string.IsNullOrEmpty(projectDatabasePath))
            return IndexResult.Failure("项目数据库路径为空");

        try
        {
            var fileName = Path.GetFileName(csvFilePath);
            var tableName = SanitizeTableName(fileName);

            await EnsureSourceTableAsync(projectDatabasePath);

            var sourceId = await InsertSourceRecordAsync(projectDatabasePath, csvFilePath, fileName, tableName);

            var (columns, rows) = ReadCsvData(csvFilePath);

            if (columns.Count == 0)
                return IndexResult.Failure("CSV文件为空或无法解析");

            await CreateTableAsync(projectDatabasePath, tableName, columns);

            await InsertDataAsync(projectDatabasePath, tableName, columns, rows);

            return IndexResult.Success(tableName, rows.Count, sourceId);
        }
        catch (Exception ex)
        {
            return IndexResult.Failure($"索引失败: {ex.Message}");
        }
    }

    private async Task EnsureSourceTableAsync(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS source_registry (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_uuid TEXT NOT NULL UNIQUE,
                file_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                source_type TEXT NOT NULL DEFAULT 'csv',
                row_count INTEGER DEFAULT 0,
                indexed_at TEXT NOT NULL,
                file_modified_at TEXT,
                is_dirty INTEGER DEFAULT 0,
                can_write_back INTEGER DEFAULT 0,
                write_back_format TEXT DEFAULT NULL
            )";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> InsertSourceRecordAsync(string dbPath, string filePath, string fileName, string tableName)
    {
        var sourceUuid = Guid.NewGuid().ToString("N");
        var indexedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var fileModifiedAt = File.GetLastWriteTimeUtc(filePath).ToString("o", CultureInfo.InvariantCulture);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO source_registry (source_uuid, file_path, file_name, table_name, source_type, indexed_at, file_modified_at, can_write_back, write_back_format)
            VALUES (@uuid, @path, @name, @table, @type, @indexed, @modified, 1, 'csv')";
        command.Parameters.AddWithValue("@uuid", sourceUuid);
        command.Parameters.AddWithValue("@path", filePath);
        command.Parameters.AddWithValue("@name", fileName);
        command.Parameters.AddWithValue("@table", tableName);
        command.Parameters.AddWithValue("@type", "csv");
        command.Parameters.AddWithValue("@indexed", indexedAt);
        command.Parameters.AddWithValue("@modified", fileModifiedAt);

        await command.ExecuteNonQueryAsync();
        return sourceUuid;
    }

    private static (List<string> Columns, List<List<string>> Rows) ReadCsvData(string filePath)
    {
        var columns = new List<string>();
        var rows = new List<List<string>>();

        using var reader = new StreamReader(filePath);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrEmpty(headerLine)) return (columns, rows);

        columns = ParseCsvLine(headerLine);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            rows.Add(ParseCsvLine(line));
        }

        return (columns, rows);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        result.Add(current.ToString());
        return result;
    }

    private static string SanitizeTableName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var sanitized = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sanitized.Append(c);
            else if (c == ' ' || c == '-' || c == '.')
                sanitized.Append('_');
        }

        var result = sanitized.ToString();
        if (string.IsNullOrEmpty(result))
            result = "data";

        if (char.IsDigit(result[0]))
            result = "t_" + result;

        return $"source_{result}";
    }

    private async Task CreateTableAsync(string dbPath, string tableName, List<string> columns)
    {
        var safeColumns = MakeUniqueColumnNames(columns);
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        var columnDefs = safeColumns.Select(c => $"\"{c}\" TEXT");

        var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE IF NOT EXISTS \"{tableName}\" ({string.Join(", ", columnDefs)})";
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertDataAsync(string dbPath, string tableName, List<string> columns, List<List<string>> rows)
    {
        var safeColumns = MakeUniqueColumnNames(columns);
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        using var transaction = await connection.BeginTransactionAsync();

        var columnNames = safeColumns.Select(c => $"\"{c}\"");
        var placeholders = columns.Select((_, i) => $"@p{i}");
        var insertSql = $"INSERT INTO \"{tableName}\" ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", placeholders)})";

        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = insertSql;

            for (int i = 0; i < columns.Count; i++)
            {
                var value = i < row.Count ? row[i] : string.Empty;
                command.Parameters.AddWithValue($"@p{i}", value);
            }

            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();

        var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = "UPDATE source_registry SET row_count = @count WHERE table_name = @table";
        updateCommand.Parameters.AddWithValue("@count", rows.Count);
        updateCommand.Parameters.AddWithValue("@table", tableName);
        await updateCommand.ExecuteNonQueryAsync();
    }

    private static List<string> MakeUniqueColumnNames(IReadOnlyList<string> columns)
    {
        var result = new List<string>();
        var seen = new Dictionary<string, int>();

        for (var i = 0; i < columns.Count; i++)
        {
            var name = string.IsNullOrWhiteSpace(columns[i]) ? $"col_{i}" : columns[i].Trim();
            if (!seen.ContainsKey(name))
            {
                seen[name] = 1;
                result.Add(name);
            }
            else
            {
                seen[name]++;
                result.Add($"{name}_{seen[name]}");
            }
        }

        return result;
    }
}
