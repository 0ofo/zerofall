using System;
using System.IO;

namespace ZeroFall.Platform.Models;

/// <summary>项目工作区 SQLite 数据库文件名与路径解析。</summary>
public static class WorkspaceDatabase
{
    public const string FileName = ".zerofall.db";

    public static string GetPath(string workspaceDirectory)
    {
        if (string.IsNullOrEmpty(workspaceDirectory))
            return string.Empty;

        return Path.Combine(workspaceDirectory, FileName);
    }

    public static bool IsProtectedDatabaseFile(string? fileName) =>
        string.Equals(fileName, FileName, StringComparison.OrdinalIgnoreCase);
}
