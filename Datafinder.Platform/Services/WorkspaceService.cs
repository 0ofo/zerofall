using System;
using System.Threading.Tasks;
using Datafinder.Platform.Models;

namespace Datafinder.Platform.Services;

public class WorkspaceService : IWorkspaceService
{
    private Workspace? _currentWorkspace;

    public Workspace? CurrentWorkspace => _currentWorkspace;
    public bool HasWorkspace => _currentWorkspace != null;

    public event EventHandler<Workspace>? WorkspaceOpened;
    public event EventHandler? WorkspaceClosed;

    public async Task OpenWorkspaceAsync(string directoryPath)
    {
        _currentWorkspace = Workspace.FromDirectory(directoryPath);
        WorkspaceOpened?.Invoke(this, _currentWorkspace);
        await Task.CompletedTask;
    }

    public async Task CloseWorkspaceAsync()
    {
        _currentWorkspace = null;
        WorkspaceClosed?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask;
    }

    public string? GetDatabasePath()
    {
        return _currentWorkspace?.DatabasePath;
    }
}
