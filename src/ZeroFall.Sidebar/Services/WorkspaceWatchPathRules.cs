using System;
using System.Collections.Generic;
using System.IO;

namespace ZeroFall.Sidebar.Services;

internal static class WorkspaceWatchPathRules
{
    private static readonly HashSet<string> HiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zerofall.db", ".git", ".svn", ".hg", "node_modules", "__pycache__", ".vs", ".idea", "bin", "obj",
        ".cursor", ".venv", "venv", ".pytest_cache", ".mypy_cache", ".ai"
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pyc", ".pyo", ".tmp", ".temp", ".swp", ".swo", ".db-wal", ".db-shm", ".db-journal"
    };

    public static bool ShouldIgnore(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
            return true;

        if (fileName.StartsWith("~", StringComparison.Ordinal)
            || fileName.EndsWith('~'))
        {
            return true;
        }

        var ext = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(ext) && IgnoredExtensions.Contains(ext))
            return true;

        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (HiddenNames.Contains(segment))
                return true;
        }

        return false;
    }
}
