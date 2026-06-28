using System.Collections.Generic;
using System.Threading.Tasks;

namespace Datafinder.Platform.Services;

public interface IProjectService
{
    Task EnsureDatabaseAsync(string databasePath);
    Task<string> GetProjectNameAsync(string databasePath);
    Task<int> ImportFilesAsync(IReadOnlyList<string> sourcePaths, string targetDirectory);
    Task<bool> MoveEntryAsync(string sourcePath, string targetDirectory);
    Task<bool> MergeDirectoryAsync(string sourceDirectory, string targetDirectory);
    Task<bool> RenameEntryAsync(string sourcePath, string newName);
    Task<bool> DeleteEntryAsync(string path);
    Task<bool> CreateFolderAsync(string parentDirectory, string folderName);
    Task<bool> CreateFileAsync(string parentDirectory, string fileName);
}
