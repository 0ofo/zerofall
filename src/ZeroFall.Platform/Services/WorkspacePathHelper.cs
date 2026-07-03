using System.IO;

namespace ZeroFall.Platform.Services;

public static class WorkspacePathHelper
{
    /// <summary>将相对工作区路径解析为绝对路径；已是绝对路径则规范化返回。</summary>
    public static string? ResolveFilePath(string? path, string? workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = path.Trim();
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        if (string.IsNullOrWhiteSpace(workspaceDirectory))
            return null;

        return Path.GetFullPath(Path.Combine(workspaceDirectory, path));
    }
}
