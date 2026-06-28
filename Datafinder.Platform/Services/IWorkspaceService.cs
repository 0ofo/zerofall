using System;
using System.Threading.Tasks;
using Datafinder.Platform.Models;

namespace Datafinder.Platform.Services;

public interface IWorkspaceService
{
    Workspace? CurrentWorkspace { get; }
    bool HasWorkspace { get; }
    event EventHandler<Workspace>? WorkspaceOpened;
    event EventHandler? WorkspaceClosed;

    Task OpenWorkspaceAsync(string directoryPath);
    Task CloseWorkspaceAsync();
    string? GetDatabasePath();
}
