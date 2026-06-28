using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace ZeroFall.Platform.Services;

public class ProjectService : IProjectService
{
    public async Task EnsureDatabaseAsync(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(databasePath))
        {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS _meta (key TEXT PRIMARY KEY, value TEXT)";
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task<string> GetProjectNameAsync(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(dir)) return "Untitled";
        return Path.GetFileName(dir) ?? "Untitled";
    }

    public async Task<int> ImportFilesAsync(IReadOnlyList<string> sourcePaths, string targetDirectory)
    {
        if (sourcePaths.Count == 0) return 0;

        if (!Directory.Exists(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        var copied = 0;
        foreach (var srcPath in sourcePaths)
        {
            try
            {
                var fileName = Path.GetFileName(srcPath);
                var destPath = Path.Combine(targetDirectory, fileName);

                if (string.Equals(srcPath, destPath, StringComparison.OrdinalIgnoreCase)) continue;

                if (Directory.Exists(srcPath))
                {
                    CopyDirectory(srcPath, Path.Combine(targetDirectory, fileName));
                }
                else if (File.Exists(srcPath))
                {
                    File.Copy(srcPath, destPath, overwrite: true);
                }

                copied++;
            }
            catch
            {
            }
        }

        return await Task.FromResult(copied);
    }

    public async Task<bool> MoveEntryAsync(string sourcePath, string targetDirectory)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                var fileName = Path.GetFileName(sourcePath);
                var destPath = Path.Combine(targetDirectory, fileName);

                if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (destPath.StartsWith(sourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (Directory.Exists(sourcePath))
                {
                    Directory.Move(sourcePath, destPath);
                }
                else if (File.Exists(sourcePath))
                {
                    try
                    {
                        File.Move(sourcePath, destPath, overwrite: true);
                    }
                    catch (IOException)
                    {
                        File.Copy(sourcePath, destPath, overwrite: true);
                        TryDeleteFile(sourcePath);
                    }
                }
                else
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    public async Task<bool> MergeDirectoryAsync(string sourceDirectory, string targetDirectory)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(sourceDirectory)) return false;
                if (!Directory.Exists(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                foreach (var file in Directory.GetFiles(sourceDirectory))
                {
                    var fileName = Path.GetFileName(file);
                    File.Move(file, Path.Combine(targetDirectory, fileName), overwrite: true);
                }

                foreach (var dir in Directory.GetDirectories(sourceDirectory))
                {
                    var dirName = Path.GetFileName(dir);
                    var targetSubDir = Path.Combine(targetDirectory, dirName);

                    if (Directory.Exists(targetSubDir))
                    {
                        MergeDirectoryRecursive(dir, targetSubDir);
                    }
                    else
                    {
                        Directory.Move(dir, targetSubDir);
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
                {
                    Directory.Delete(sourceDirectory);
                }

                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    private static void MergeDirectoryRecursive(string sourceDir, string destDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            File.Move(file, Path.Combine(destDir, fileName), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            var targetSubDir = Path.Combine(destDir, dirName);

            if (Directory.Exists(targetSubDir))
            {
                MergeDirectoryRecursive(dir, targetSubDir);
            }
            else
            {
                Directory.Move(dir, targetSubDir);
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destDir, fileName), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            CopyDirectory(dir, Path.Combine(destDir, dirName));
        }
    }

    public async Task<bool> RenameEntryAsync(string sourcePath, string newName)
    {
        return await Task.Run(() =>
        {
            try
            {
                var dir = Path.GetDirectoryName(sourcePath);
                if (string.IsNullOrEmpty(dir)) return false;

                var destPath = Path.Combine(dir, newName);

                if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (Directory.Exists(sourcePath))
                {
                    Directory.Move(sourcePath, destPath);
                }
                else if (File.Exists(sourcePath))
                {
                    try
                    {
                        File.Move(sourcePath, destPath);
                    }
                    catch (IOException)
                    {
                        File.Copy(sourcePath, destPath, overwrite: true);
                        TryDeleteFile(sourcePath);
                    }
                }
                else
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    public async Task<bool> DeleteEntryAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    TryDeleteFile(path);
                }
                else
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    public async Task<bool> CreateFolderAsync(string parentDirectory, string folderName)
    {
        return await Task.Run(() =>
        {
            try
            {
                var path = Path.Combine(parentDirectory, folderName);
                if (Directory.Exists(path)) return false;

                Directory.CreateDirectory(path);
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    public async Task<bool> CreateFileAsync(string parentDirectory, string fileName)
    {
        return await Task.Run(() =>
        {
            try
            {
                var path = Path.Combine(parentDirectory, fileName);
                if (File.Exists(path)) return false;

                using (File.Create(path)) { }
                return true;
            }
            catch
            {
                return false;
            }
        });
    }
}
