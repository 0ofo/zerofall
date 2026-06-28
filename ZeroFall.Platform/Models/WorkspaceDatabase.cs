using System;
using System.IO;

namespace ZeroFall.Platform.Models;

/// <summary>项目工作区 SQLite 数据库文件名与路径解析（含旧版 <c>.datafinder.db</c> 兼容）。</summary>
public static class WorkspaceDatabase
{
    public const string FileName = ".zerofall.db";
    public const string LegacyFileName = ".datafinder.db";

    public static string GetPath(string workspaceDirectory)
    {
        if (string.IsNullOrEmpty(workspaceDirectory))
            return string.Empty;

        var current = Path.Combine(workspaceDirectory, FileName);
        var legacy = Path.Combine(workspaceDirectory, LegacyFileName);

        if (File.Exists(current))
            return current;
        if (File.Exists(legacy))
            return legacy;
        return current;
    }

    public static bool IsProtectedDatabaseFile(string? fileName) =>
        string.Equals(fileName, FileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, LegacyFileName, StringComparison.OrdinalIgnoreCase);
}
