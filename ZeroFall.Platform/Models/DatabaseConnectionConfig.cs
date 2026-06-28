using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroFall.Base.Data;

namespace ZeroFall.Platform.Models;

public static class DatabaseConnectionFiles
{
    public const string MySqlSuffix = ".mysql";

    /// <summary>旧版后缀，仍可读。</summary>
    public const string LegacyMySqlSuffix = ".mysql.json";

    public static bool IsMySqlConnectionFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        return filePath.EndsWith(MySqlSuffix, StringComparison.OrdinalIgnoreCase)
               || filePath.EndsWith(LegacyMySqlSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetDefaultConnectionName(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (fileName.EndsWith(LegacyMySqlSuffix, StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fileName));

        return Path.GetFileNameWithoutExtension(fileName);
    }

    public static bool IsSqliteDatabaseFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".db", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase);
    }

    public static RelationalDbKind? TryGetKind(string? connectionReference)
    {
        if (string.IsNullOrEmpty(connectionReference)) return null;
        if (IsMySqlConnectionFile(connectionReference)) return RelationalDbKind.MySql;
        if (IsSqliteDatabaseFile(connectionReference)) return RelationalDbKind.Sqlite;
        return null;
    }

    public static DataSourceType ToDataSourceType(RelationalDbKind kind) => kind switch
    {
        RelationalDbKind.MySql => DataSourceType.MySql,
        RelationalDbKind.Sqlite => DataSourceType.Sqlite,
        _ => DataSourceType.Other
    };
}

public sealed class MySqlConnectionConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "MySQL";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 3306;

    [JsonPropertyName("database")]
    public string Database { get; set; } = "";

    [JsonPropertyName("user")]
    public string User { get; set; } = "root";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    public static MySqlConnectionConfig Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var config = JsonSerializer.Deserialize(json, DatabaseConnectionJsonContext.Default.MySqlConnectionConfig)
                     ?? throw new InvalidOperationException("无法解析 MySQL 连接配置。");
        if (string.IsNullOrWhiteSpace(config.Name))
            config.Name = DatabaseConnectionFiles.GetDefaultConnectionName(filePath);
        return config;
    }

    public void Save(string filePath)
    {
        var json = JsonSerializer.Serialize(this, DatabaseConnectionJsonContext.Default.MySqlConnectionConfig);
        File.WriteAllText(filePath, json);
    }

    public string BuildConnectionString()
    {
        var builder = new MySqlConnector.MySqlConnectionStringBuilder
        {
            Server = Host,
            Port = (uint)Port,
            Database = Database,
            UserID = User,
            Password = Password,
            AllowUserVariables = true,
            DefaultCommandTimeout = 60
        };
        return builder.ConnectionString;
    }
}

[JsonSerializable(typeof(MySqlConnectionConfig))]
internal partial class DatabaseConnectionJsonContext : JsonSerializerContext;
