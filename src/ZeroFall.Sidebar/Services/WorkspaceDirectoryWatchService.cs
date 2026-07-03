using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Threading;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.Sidebar.Services;

/// <summary>
/// 工作区目录文件系统监听（单例，不依赖 Sidebar ViewModel 是否已物化）。
/// 防抖后发布 <see cref="WorkspaceFileChangedEvent"/>，由侧边栏与其它面板消费。
/// </summary>
public sealed class WorkspaceDirectoryWatchService : IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly IWorkspaceService _workspaceService;
    private readonly Action<ProjectOpenedEvent> _projectOpenedHandler;
    private readonly ProjectDirectoryWatcher _watcher;
    private string _projectRootPrefix = string.Empty;

    public WorkspaceDirectoryWatchService(IEventBus eventBus, IWorkspaceService workspaceService)
    {
        _eventBus = eventBus;
        _workspaceService = workspaceService;
        _watcher = new ProjectDirectoryWatcher(WorkspaceWatchPathRules.ShouldIgnore, OnExternalDirectoryChanged);
        _projectOpenedHandler = OnProjectOpened;
        _eventBus.Subscribe(_projectOpenedHandler);
        _workspaceService.WorkspaceOpened += OnWorkspaceOpened;
        _workspaceService.WorkspaceClosed += OnWorkspaceClosed;
        TryAttachCurrentWorkspace();
    }

    private void OnProjectOpened(ProjectOpenedEvent e) => Attach(e.DirectoryPath);

    private void OnWorkspaceOpened(object? sender, Workspace workspace) =>
        Dispatcher.UIThread.Post(() => Attach(workspace.DirectoryPath));

    private void OnWorkspaceClosed(object? sender, EventArgs e)
    {
        _projectRootPrefix = string.Empty;
        _watcher.Stop();
    }

    private void TryAttachCurrentWorkspace()
    {
        if (!_workspaceService.HasWorkspace || _workspaceService.CurrentWorkspace is null)
            return;

        Attach(_workspaceService.CurrentWorkspace.DirectoryPath);
    }

    private void Attach(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return;

        _projectRootPrefix = NormalizeProjectRoot(directoryPath);
        _watcher.Start(_projectRootPrefix.TrimEnd(Path.DirectorySeparatorChar));
    }

    private void OnExternalDirectoryChanged(IReadOnlyList<DirectoryWatchNotification> notifications)
    {
        Dispatcher.UIThread.Post(
            () => PublishNotifications(notifications),
            DispatcherPriority.Normal);
    }

    private void PublishNotifications(IReadOnlyList<DirectoryWatchNotification> notifications)
    {
        if (string.IsNullOrEmpty(_projectRootPrefix))
            return;

        foreach (var notification in notifications)
        {
            switch (notification.Kind)
            {
                case DirectoryWatchChangeKind.Deleted:
                    PublishIfUnderProject(notification.FullPath, deleted: true);
                    break;

                case DirectoryWatchChangeKind.Renamed:
                    if (!string.IsNullOrEmpty(notification.OldFullPath))
                        PublishIfUnderProject(notification.OldFullPath, deleted: true);
                    PublishIfUnderProject(notification.FullPath, deleted: false);
                    break;

                case DirectoryWatchChangeKind.Changed:
                    if (File.Exists(notification.FullPath))
                        PublishIfUnderProject(notification.FullPath, deleted: false);
                    break;

                default:
                    if (File.Exists(notification.FullPath) || Directory.Exists(notification.FullPath))
                        PublishIfUnderProject(notification.FullPath, deleted: false);
                    break;
            }
        }
    }

    private void PublishIfUnderProject(string path, bool deleted)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var fullPath = Path.GetFullPath(path);
        if (!IsUnderProject(fullPath))
            return;

        _eventBus.Publish(new WorkspaceFileChangedEvent(fullPath, deleted));
    }

    private bool IsUnderProject(string fullPath)
    {
        if (string.IsNullOrEmpty(_projectRootPrefix))
            return false;

        return fullPath.StartsWith(_projectRootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProjectRoot(string directoryPath) =>
        Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    public void Dispose()
    {
        _eventBus.Unsubscribe(_projectOpenedHandler);
        _workspaceService.WorkspaceOpened -= OnWorkspaceOpened;
        _workspaceService.WorkspaceClosed -= OnWorkspaceClosed;
        _watcher.Dispose();
    }
}
