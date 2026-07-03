using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroFall.Sidebar.Services;

internal enum DirectoryWatchChangeKind
{
    Created,
    Deleted,
    Renamed,
    Changed
}

internal readonly record struct DirectoryWatchNotification(
    DirectoryWatchChangeKind Kind,
    string FullPath,
    string? OldFullPath = null);

/// <summary>
/// 监听项目目录外部文件变动，防抖后回调变更通知。
/// </summary>
internal sealed class ProjectDirectoryWatcher : IDisposable
{
    private const int DebounceMs = 250;

    private readonly Func<string, bool> _shouldIgnorePath;
    private readonly Action<IReadOnlyList<DirectoryWatchNotification>> _onChanges;
    private readonly List<DirectoryWatchNotification> _pending = [];
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private string _watchedDirectory = string.Empty;

    public ProjectDirectoryWatcher(
        Func<string, bool> shouldIgnorePath,
        Action<IReadOnlyList<DirectoryWatchNotification>> onChanges)
    {
        _shouldIgnorePath = shouldIgnorePath;
        _onChanges = onChanges;
    }

    public void Start(string projectDirectory)
    {
        Stop();
        if (string.IsNullOrEmpty(projectDirectory) || !Directory.Exists(projectDirectory))
            return;

        _watchedDirectory = Path.GetFullPath(projectDirectory);
        _watcher = new FileSystemWatcher(_watchedDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };
        _watcher.Created += OnCreated;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Changed += OnChanged;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        CancelDebounce();
        if (_watcher is null)
            return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnCreated;
        _watcher.Deleted -= OnDeleted;
        _watcher.Renamed -= OnRenamed;
        _watcher.Changed -= OnChanged;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _watcher = null;
        _watchedDirectory = string.Empty;

        lock (_gate)
            _pending.Clear();
    }

    private void OnCreated(object sender, FileSystemEventArgs e) =>
        Enqueue(new DirectoryWatchNotification(DirectoryWatchChangeKind.Created, e.FullPath));

    private void OnDeleted(object sender, FileSystemEventArgs e) =>
        Enqueue(new DirectoryWatchNotification(DirectoryWatchChangeKind.Deleted, e.FullPath));

    private void OnRenamed(object sender, RenamedEventArgs e) =>
        Enqueue(new DirectoryWatchNotification(DirectoryWatchChangeKind.Renamed, e.FullPath, e.OldFullPath));

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FullPath) || Directory.Exists(e.FullPath))
            return;

        Enqueue(new DirectoryWatchNotification(DirectoryWatchChangeKind.Changed, e.FullPath));
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (string.IsNullOrEmpty(_watchedDirectory))
            return;

        var directory = _watchedDirectory;
        Stop();
        Start(directory);
    }

    private void Enqueue(DirectoryWatchNotification notification)
    {
        if (string.IsNullOrEmpty(notification.FullPath) || _shouldIgnorePath(notification.FullPath))
            return;

        if (notification.Kind == DirectoryWatchChangeKind.Renamed
            && !string.IsNullOrEmpty(notification.OldFullPath)
            && _shouldIgnorePath(notification.OldFullPath))
        {
            return;
        }

        lock (_gate)
            _pending.Add(notification);

        ScheduleDebounce();
    }

    private void ScheduleDebounce()
    {
        CancelDebounce();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _ = DebounceAsync(token);
    }

    private async Task DebounceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceMs, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        DirectoryWatchNotification[] notifications;
        lock (_gate)
        {
            notifications = _pending.ToArray();
            _pending.Clear();
        }

        if (notifications.Length == 0)
            return;

        _onChanges(notifications);
    }

    private void CancelDebounce()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
    }

    public void Dispose() => Stop();
}
