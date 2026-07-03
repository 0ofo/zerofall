using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Data;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using ZeroFall.Platform.Services.RelationalDb;
using ZeroFall.Sidebar.Services;

namespace ZeroFall.Sidebar.ViewModels;

public partial class SidebarViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<TreeNodeViewModel> _rootChildren = new();

    [ObservableProperty]
    private TreeNodeViewModel? _selectedTreeNode;

    [ObservableProperty]
    private string _projectDirectory = string.Empty;

    [ObservableProperty]
    private string _projectDatabasePath = string.Empty;

    [ObservableProperty]
    private bool _hasProject;

    private readonly IRelationalDbBrowserRegistry _relationalDbRegistry;
    private readonly IProjectService _projectService;
    private readonly IEventBus _eventBus;
    private readonly IWorkspaceService _workspaceService;
    private string _projectDirectoryFullPath = string.Empty;
    private string _projectDirectoryRootPrefix = string.Empty;
    private readonly HashSet<string> _expandedFolderPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WorkspaceFileChangedEvent> _pendingWorkspaceChanges = new();
    private readonly DispatcherTimer _workspaceChangeDebounceTimer;
    private static readonly TimeSpan WorkspaceChangeDebounce = TimeSpan.FromMilliseconds(280);
    private const string SidebarTabId = "sidebar";
    private bool _leftPanelVisible = true;
    private bool _sidebarTabSelected = true;
    private bool _workspaceRefreshDeferred;

    private static readonly HashSet<string> TextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".json", ".xml", ".txt", ".log", ".md", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".sql"
    };

    private static readonly HashSet<string> DatabaseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".sqlite3"
    };

    private static readonly HashSet<string> HiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zerofall.db", ".git", ".svn", ".hg", "node_modules", "__pycache__", ".vs", ".idea", "bin", "obj"
    };

    public SidebarViewModel(
        IRelationalDbBrowserRegistry relationalDbRegistry,
        IProjectService projectService,
        IEventBus eventBus,
        IWorkspaceService workspaceService)
    {
        _relationalDbRegistry = relationalDbRegistry;
        _projectService = projectService;
        _eventBus = eventBus;
        _workspaceService = workspaceService;
        _workspaceService.WorkspaceOpened += OnWorkspaceOpened;
        _workspaceChangeDebounceTimer = new DispatcherTimer { Interval = WorkspaceChangeDebounce };
        _workspaceChangeDebounceTimer.Tick += (_, _) => _ = FlushPendingWorkspaceChangesAsync();
        SubscribeEvent(eventBus, (ProjectOpenedEvent e) => SetProjectDirectory(e.DirectoryPath, e.DatabasePath));
        SubscribeEvent(eventBus, (WorkspaceFileChangedEvent e) => OnWorkspaceFileChanged(e));
        SubscribeEvent(eventBus, (DockTabSelectedEvent e) => OnDockTabSelected(e));
        SubscribeEvent(eventBus, (PanelVisibilityChangedEvent e) => OnPanelVisibilityChanged(e));
        TrySyncFromOpenWorkspace();
    }

    private bool IsSidebarPanelActive => _leftPanelVisible && _sidebarTabSelected;

    private void OnDockTabSelected(DockTabSelectedEvent e)
    {
        if (e.Region != DockPosition.Left)
            return;

        var wasActive = IsSidebarPanelActive;
        _sidebarTabSelected = string.Equals(e.Tab?.Id, SidebarTabId, StringComparison.Ordinal);
        if (!wasActive && IsSidebarPanelActive && _workspaceRefreshDeferred)
            _ = ResumeDeferredWorkspaceRefreshAsync();
    }

    private void OnPanelVisibilityChanged(PanelVisibilityChangedEvent e)
    {
        if (e.Position != DockPosition.Left)
            return;

        var wasActive = IsSidebarPanelActive;
        _leftPanelVisible = e.IsVisible;
        if (!wasActive && IsSidebarPanelActive && _workspaceRefreshDeferred)
            _ = ResumeDeferredWorkspaceRefreshAsync();
    }

    private async Task ResumeDeferredWorkspaceRefreshAsync()
    {
        if (_pendingWorkspaceChanges.Count == 0)
        {
            _workspaceRefreshDeferred = false;
            return;
        }

        await StartupPerformance.YieldUiFramesAsync(2);
        if (!IsSidebarPanelActive)
            return;

        await FlushPendingWorkspaceChangesAsync();
    }

    private void OnWorkspaceOpened(object? sender, Workspace workspace) =>
        Dispatcher.UIThread.Post(() => SetProjectDirectory(workspace.DirectoryPath, workspace.DatabasePath));

    private void TrySyncFromOpenWorkspace()
    {
        if (!_workspaceService.HasWorkspace || _workspaceService.CurrentWorkspace is null)
            return;

        var ws = _workspaceService.CurrentWorkspace;
        SetProjectDirectory(ws.DirectoryPath, ws.DatabasePath);
    }

    private void OnWorkspaceFileChanged(WorkspaceFileChangedEvent e) =>
        Dispatcher.UIThread.Post(() =>
        {
            _pendingWorkspaceChanges.Add(e);
            _workspaceChangeDebounceTimer.Stop();
            _workspaceChangeDebounceTimer.Start();
        }, IsSidebarPanelActive ? DispatcherPriority.Normal : DispatcherPriority.ApplicationIdle);

    private async Task FlushPendingWorkspaceChangesAsync()
    {
        _workspaceChangeDebounceTimer.Stop();
        if (_pendingWorkspaceChanges.Count == 0)
            return;

        if (!IsSidebarPanelActive)
        {
            _workspaceRefreshDeferred = true;
            return;
        }

        _workspaceRefreshDeferred = false;
        var batch = _pendingWorkspaceChanges.ToList();
        _pendingWorkspaceChanges.Clear();

        var byPath = new Dictionary<string, WorkspaceFileChangedEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in batch)
        {
            if (WorkspaceWatchPathRules.ShouldIgnore(change.FilePath))
                continue;

            var fullPath = Path.GetFullPath(change.FilePath);
            if (!IsUnderProjectDirectory(fullPath))
                continue;

            byPath[fullPath] = change;
        }

        var refreshDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in byPath.Values.Where(c => c.Deleted))
            await ApplyWorkspaceFileChangedAsync(change, refreshDirectories);

        foreach (var change in byPath.Values.Where(c => !c.Deleted))
            await ApplyWorkspaceFileChangedAsync(change, refreshDirectories);

        foreach (var dir in refreshDirectories)
        {
            await RefreshDirectoryFromWatcherAsync(dir);
            await StartupPerformance.YieldUiFrameAsync();
        }
    }

    private async Task ApplyWorkspaceFileChangedAsync(
        WorkspaceFileChangedEvent e,
        HashSet<string> refreshDirectories)
    {
        if (!HasProject || string.IsNullOrEmpty(_projectDirectoryFullPath))
            return;

        if (WorkspaceWatchPathRules.ShouldIgnore(e.FilePath))
            return;

        var fullPath = Path.GetFullPath(e.FilePath);
        if (!IsUnderProjectDirectory(fullPath))
            return;

        // 已存在节点的内容变更不需要动树结构（避免保存/索引等频繁刷新导致展开状态丢失）
        if (!e.Deleted && FindNodeByPath(fullPath) != null)
            return;

        if (e.Deleted)
        {
            if (!TryRemoveNodeByPath(fullPath))
            {
                var parent = GetParentDirectory(fullPath);
                AddRefreshDirectory(refreshDirectories, parent);
            }

            return;
        }

        if (!await TryInsertEntryNodeAsync(fullPath))
        {
            var parent = GetParentDirectory(fullPath);
            AddRefreshDirectory(refreshDirectories, parent);
        }
    }

    internal void RegisterExpandedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        _expandedFolderPaths.Add(Path.GetFullPath(path));
    }

    internal void UnregisterExpandedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        _expandedFolderPaths.Remove(Path.GetFullPath(path));
    }

    public void SetProjectDirectory(string directoryPath, string databasePath)
    {
        ProjectDirectory = directoryPath;
        ProjectDatabasePath = databasePath;
        _projectDirectoryFullPath = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _projectDirectoryRootPrefix = _projectDirectoryFullPath + Path.DirectorySeparatorChar;
        HasProject = true;
        _expandedFolderPaths.Clear();
        _ = LoadDirectoryTreeAsync();
    }

    private bool IsUnderProjectDirectory(string fullPath) =>
        fullPath.StartsWith(_projectDirectoryRootPrefix, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fullPath, _projectDirectoryFullPath, StringComparison.OrdinalIgnoreCase);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _workspaceChangeDebounceTimer.Stop();
            _workspaceService.WorkspaceOpened -= OnWorkspaceOpened;
        }

        base.Dispose(disposing);
    }

    private static void AddRefreshDirectory(HashSet<string> directories, string? directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath))
            return;

        directories.Add(Path.GetFullPath(directoryPath));
    }

    private static string? GetParentDirectory(string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path));

    private bool TryRemoveNodeByPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var normalized = Path.GetFullPath(path);
        foreach (var node in RootChildren.ToList())
        {
            if (TryRemoveNodeByPathRecursive(node, normalized))
                return true;
        }

        return false;
    }

    private TreeNodeViewModel? FindNodeByPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var normalized = Path.GetFullPath(path);
        foreach (var node in RootChildren)
        {
            var found = FindNodeByPathRecursive(node, normalized);
            if (found != null)
                return found;
        }

        return null;
    }

    private static TreeNodeViewModel? FindNodeByPathRecursive(TreeNodeViewModel node, string normalizedPath)
    {
        if (!string.IsNullOrEmpty(node.FilePath)
            && string.Equals(Path.GetFullPath(node.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindNodeByPathRecursive(child, normalizedPath);
            if (found != null)
                return found;
        }

        return null;
    }

    private TreeNodeViewModel? FindFolderNodeByPath(string? directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(_projectDirectoryFullPath))
            return null;

        var normalized = Path.GetFullPath(directoryPath);
        if (string.Equals(normalized, _projectDirectoryFullPath, StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var node in RootChildren)
        {
            var found = FindFolderNodeByPathRecursive(node, normalized);
            if (found != null)
                return found;
        }

        return null;
    }

    private static TreeNodeViewModel? FindFolderNodeByPathRecursive(TreeNodeViewModel node, string normalizedPath)
    {
        if (node.NodeType == TreeNodeType.Folder
            && !string.IsNullOrEmpty(node.FilePath)
            && string.Equals(Path.GetFullPath(node.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindFolderNodeByPathRecursive(child, normalizedPath);
            if (found != null)
                return found;
        }

        return null;
    }

    private async Task<bool> TryInsertEntryNodeAsync(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return false;

        var normalized = Path.GetFullPath(fullPath);
        if (!File.Exists(normalized) && !Directory.Exists(normalized))
            return false;

        if (FindNodeByPath(normalized) != null)
            return true;

        var parentDir = Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(parentDir))
            return false;

        if (string.Equals(parentDir, _projectDirectoryFullPath, StringComparison.OrdinalIgnoreCase))
        {
            await InsertEntryIntoCollectionAsync(normalized, RootChildren, null);
            return true;
        }

        var parentNode = FindFolderNodeByPath(parentDir);
        if (parentNode == null)
            return false;

        if (parentNode.Children.Count == 1 && parentNode.Children[0].Name == "...")
        {
            parentNode.IsExpanded = true;
            RegisterExpandedPath(parentNode.FilePath);
            parentNode.Children.Clear();
            await LoadDirectoryContentsAsync(parentNode.FilePath, parentNode.Children, parentNode);
            if (FindNodeByPath(normalized) != null)
                return true;
        }

        await InsertEntryIntoCollectionAsync(normalized, parentNode.Children, parentNode);
        return true;
    }

    private async Task InsertEntryIntoCollectionAsync(
        string entryPath,
        ObservableCollection<TreeNodeViewModel> targetCollection,
        TreeNodeViewModel? parentNode)
    {
        var name = Path.GetFileName(entryPath);
        if (targetCollection.Any(n => !string.Equals(n.Name, "...", StringComparison.Ordinal)
                                      && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var isDir = Directory.Exists(entryPath);
        var node = new TreeNodeViewModel
        {
            Name = name,
            FilePath = entryPath,
            Parent = parentNode,
            NodeType = isDir ? TreeNodeType.Folder : GetNodeType(entryPath),
            DataSourceType = isDir ? DataSourceType.Other : GetDataSourceType(entryPath)
        };

        if (isDir)
            node.Children.Add(new TreeNodeViewModel { Name = "..." });

        var insertIndex = targetCollection.Count;
        for (var i = 0; i < targetCollection.Count; i++)
        {
            if (string.Equals(targetCollection[i].Name, "...", StringComparison.Ordinal))
                continue;

            if (CompareTreeEntries(targetCollection[i], node) > 0)
            {
                insertIndex = i;
                break;
            }
        }

        targetCollection.Insert(insertIndex, node);

        if (!isDir && DatabaseExtensions.Contains(Path.GetExtension(entryPath)))
            await LoadConnectionTablesAsync(node);
        else if (!isDir && DatabaseConnectionFiles.IsMySqlConnectionFile(entryPath))
            await LoadConnectionTablesAsync(node);
    }

    private static int CompareTreeEntries(TreeNodeViewModel existing, TreeNodeViewModel candidate)
    {
        var existingIsDir = existing.NodeType == TreeNodeType.Folder;
        var candidateIsDir = candidate.NodeType == TreeNodeType.Folder;
        if (existingIsDir != candidateIsDir)
            return existingIsDir ? 1 : -1;

        return string.Compare(existing.Name, candidate.Name, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryRemoveNodeByPathRecursive(TreeNodeViewModel node, string normalizedPath)
    {
        if (!string.IsNullOrEmpty(node.FilePath)
            && string.Equals(Path.GetFullPath(node.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            RemoveNodeFromTree(node);
            return true;
        }

        foreach (var child in node.Children.ToList())
        {
            if (TryRemoveNodeByPathRecursive(child, normalizedPath))
                return true;
        }

        return false;
    }

    private async Task RefreshDirectoryFromWatcherAsync(string directoryPath)
    {
        if (!HasProject || string.IsNullOrEmpty(_projectDirectoryFullPath))
            return;

        var normalized = Path.GetFullPath(directoryPath);
        if (!normalized.StartsWith(_projectDirectoryFullPath, StringComparison.OrdinalIgnoreCase))
            return;

        var current = normalized;
        while (!string.IsNullOrEmpty(current))
        {
            if (await TryRefreshDirectoryInTreeAsync(current))
                return;

            if (string.Equals(current, _projectDirectoryFullPath, StringComparison.OrdinalIgnoreCase))
                return;

            current = Path.GetDirectoryName(current)!;
        }
    }

    private async Task<bool> TryRefreshDirectoryInTreeAsync(string directoryPath)
    {
        var normalized = Path.GetFullPath(directoryPath);
        if (string.Equals(normalized, _projectDirectoryFullPath, StringComparison.OrdinalIgnoreCase))
        {
            await RefreshTargetDirectoryAsync(ProjectDirectory);
            return true;
        }

        foreach (var node in RootChildren)
        {
            if (await RefreshNodeDirectoryAsync(node, normalized))
                return true;
        }

        return false;
    }

    private async Task LoadDirectoryTreeAsync()
    {
        if (string.IsNullOrEmpty(ProjectDirectory) || !Directory.Exists(ProjectDirectory)) return;

        var expandedPaths = SnapshotExpandedPaths(RootChildren);

        RootChildren.Clear();

        try
        {
            await LoadDirectoryContentsAsync(ProjectDirectory, RootChildren, null);
            await RestoreExpandedPathsAsync(RootChildren, expandedPaths);
        }
        catch (Exception ex)
        {
            _eventBus.Publish(new StatusMessageEvent($"加载目录树失败: {ex.Message}"));
        }
    }

    private HashSet<string> SnapshotExpandedPaths(ObservableCollection<TreeNodeViewModel> nodes)
    {
        var paths = new HashSet<string>(_expandedFolderPaths, StringComparer.OrdinalIgnoreCase);
        CollectExpandedPathsRecursive(nodes, paths);
        return paths;
    }

    private static void CollectExpandedPathsRecursive(ObservableCollection<TreeNodeViewModel> nodes, HashSet<string> paths)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded && !string.IsNullOrEmpty(node.FilePath))
            {
                paths.Add(node.FilePath);
                AddAncestorPaths(node.Parent, paths);
            }

            CollectExpandedPathsRecursive(node.Children, paths);
        }
    }

    private static void AddAncestorPaths(TreeNodeViewModel? node, HashSet<string> paths)
    {
        while (node != null)
        {
            if (!string.IsNullOrEmpty(node.FilePath))
                paths.Add(node.FilePath);
            node = node.Parent;
        }
    }

    private async Task RestoreExpandedPathsAsync(ObservableCollection<TreeNodeViewModel> nodes, HashSet<string> expandedPaths)
    {
        foreach (var node in nodes)
        {
            if (!string.IsNullOrEmpty(node.FilePath) && expandedPaths.Contains(node.FilePath))
            {
                node.IsExpanded = true;
                RegisterExpandedPath(node.FilePath);
                if (node.NodeType == TreeNodeType.Folder && node.Children.Count == 1 && node.Children[0].Name == "...")
                {
                    node.Children.Clear();
                    await LoadDirectoryContentsAsync(node.FilePath, node.Children, node);
                }
            }
            await RestoreExpandedPathsAsync(node.Children, expandedPaths);
        }
    }

    private static List<string>? EnumerateSortedDirectoryEntries(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directoryPath)
                .Where(p => !HiddenNames.Contains(Path.GetFileName(p)))
                .OrderBy(p => !Directory.Exists(p))
                .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task LoadDirectoryContentsAsync(string directoryPath, ObservableCollection<TreeNodeViewModel> targetCollection, TreeNodeViewModel? parentNode)
    {
        var entries = EnumerateSortedDirectoryEntries(directoryPath);
        if (entries == null)
            return;

        foreach (var entryPath in entries)
        {
            var name = Path.GetFileName(entryPath);
            var isDir = Directory.Exists(entryPath);

            var node = new TreeNodeViewModel
            {
                Name = name,
                FilePath = entryPath,
                Parent = parentNode,
                NodeType = isDir ? TreeNodeType.Folder : GetNodeType(entryPath),
                DataSourceType = isDir ? DataSourceType.Other : GetDataSourceType(entryPath)
            };

            if (isDir)
            {
                node.Children.Add(new TreeNodeViewModel { Name = "..." });
            }

            targetCollection.Add(node);

            if (!isDir && DatabaseExtensions.Contains(Path.GetExtension(entryPath)))
            {
                await LoadConnectionTablesAsync(node);
            }
            else if (!isDir && DatabaseConnectionFiles.IsMySqlConnectionFile(entryPath))
            {
                await LoadConnectionTablesAsync(node);
            }
        }
    }

    private static TreeNodeType GetNodeType(string filePath)
    {
        if (DatabaseConnectionFiles.IsMySqlConnectionFile(filePath))
            return TreeNodeType.DataSource;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (DatabaseExtensions.Contains(ext)) return TreeNodeType.DataSource;
        if (TextFileExtensions.Contains(ext)) return TreeNodeType.File;
        return TreeNodeType.File;
    }

    private static DataSourceType GetDataSourceType(string filePath)
    {
        if (DatabaseConnectionFiles.IsMySqlConnectionFile(filePath))
            return DataSourceType.MySql;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".db" or ".sqlite" or ".sqlite3" => DataSourceType.Sqlite,
            ".csv" => DataSourceType.Csv,
            ".json" => DataSourceType.Json,
            ".xlsx" or ".xls" => DataSourceType.Excel,
            _ => DataSourceType.Other
        };
    }

    [RelayCommand]
    private async Task TreeNodeExpanded(TreeNodeViewModel? node)
    {
        if (node == null || node.NodeType != TreeNodeType.Folder) return;
        RegisterExpandedPath(node.FilePath);
        if (node.Children.Count == 1 && node.Children[0].Name == "...")
        {
            node.Children.Clear();
            await LoadDirectoryContentsAsync(node.FilePath, node.Children, node);
        }
    }

    [RelayCommand]
    private void TreeNodeSelected(TreeNodeViewModel? node)
    {
        if (node == null) return;
        SelectedTreeNode = node;
        _eventBus.Publish(new StatusMessageEvent($"已选择: {node.Name}"));
    }

    [RelayCommand]
    private async Task TreeNodeOpenAsync(TreeNodeViewModel? node)
    {
        if (node == null) return;
        SelectedTreeNode = node;

        if (node.NodeType == TreeNodeType.DataSource
            && node.DataSourceType is DataSourceType.MySql or DataSourceType.Sqlite)
        {
            node.IsExpanded = true;
            await RefreshConnectionTablesAsync(node);
            _eventBus.Publish(new StatusMessageEvent($"已打开: {node.Name}"));
            return;
        }

        if (node.NodeType is TreeNodeType.Folder or TreeNodeType.Database)
            return;

        _eventBus.Publish(new TreeNodeSelectedEvent(node));
    }

    private async Task<bool> LoadConnectionTablesAsync(TreeNodeViewModel node, bool forceRefresh = false)
    {
        if (string.IsNullOrEmpty(node.FilePath)) return false;

        if (forceRefresh)
            ClearConnectionTableNodes(node);
        else if (node.Children.Any(c => c.NodeType is TreeNodeType.Table or TreeNodeType.Database))
            return true;

        var browser = _relationalDbRegistry.Resolve(node.FilePath);
        if (browser == null) return false;

        try
        {
            var tables = await browser.GetTablesAsync(node.FilePath);
            var dataSourceType = DatabaseConnectionFiles.ToDataSourceType(browser.Kind);

            if (browser.Kind == RelationalDbKind.MySql)
                AddMySqlTableNodes(node, tables, dataSourceType);
            else
                AddFlatTableNodes(node, tables, dataSourceType);

            return true;
        }
        catch (Exception ex)
        {
            ClearConnectionTableNodes(node);
            _eventBus.Publish(new StatusMessageEvent(
                RelationalDbUserMessages.FormatLoadTablesFailure(node.Name, ex)));
            return false;
        }
    }

    private static void ClearConnectionTableNodes(TreeNodeViewModel node)
    {
        var tableNodes = node.Children
            .Where(c => c.NodeType is TreeNodeType.Table or TreeNodeType.Database)
            .ToList();
        foreach (var table in tableNodes)
            node.Children.Remove(table);
    }

    private static void AddFlatTableNodes(
        TreeNodeViewModel connectionNode,
        IReadOnlyList<RelationalTableEntry> tables,
        DataSourceType dataSourceType)
    {
        foreach (var table in tables)
        {
            connectionNode.Children.Add(CreateTableNode(connectionNode, table, dataSourceType));
        }
    }

    private static void AddMySqlTableNodes(
        TreeNodeViewModel connectionNode,
        IReadOnlyList<RelationalTableEntry> tables,
        DataSourceType dataSourceType)
    {
        foreach (var group in tables
                     .GroupBy(t => t.Schema, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var databaseNode = new TreeNodeViewModel
            {
                Name = group.Key,
                NodeType = TreeNodeType.Database,
                DataSourceType = dataSourceType,
                FilePath = connectionNode.FilePath,
                Parent = connectionNode
            };

            foreach (var table in group.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                databaseNode.Children.Add(CreateTableNode(databaseNode, table, dataSourceType));
            }

            connectionNode.Children.Add(databaseNode);
        }
    }

    private static TreeNodeViewModel CreateTableNode(
        TreeNodeViewModel parentNode,
        RelationalTableEntry table,
        DataSourceType dataSourceType) =>
        new()
        {
            Name = table.DisplayName,
            TableReference = table.Reference,
            NodeType = TreeNodeType.Table,
            DataSourceType = dataSourceType,
            FilePath = parentNode.FilePath,
            Parent = parentNode
        };

    [RelayCommand]
    private async Task RefreshConnectionTablesAsync(TreeNodeViewModel? node)
    {
        if (node == null || node.NodeType != TreeNodeType.DataSource || string.IsNullOrEmpty(node.FilePath))
            return;

        if (!await LoadConnectionTablesAsync(node, forceRefresh: true))
            return;

        var count = CountConnectionTableEntries(node);
        _eventBus.Publish(new StatusMessageEvent($"已刷新 {node.Name} 表列表（{count} 项）"));
    }

    private static int CountConnectionTableEntries(TreeNodeViewModel node)
    {
        var count = 0;
        foreach (var child in node.Children)
        {
            if (child.NodeType == TreeNodeType.Table)
                count++;
            else if (child.NodeType == TreeNodeType.Database)
                count += child.Children.Count(c => c.NodeType == TreeNodeType.Table);
        }

        return count;
    }

    [RelayCommand]
    private async Task TestConnectionAsync(TreeNodeViewModel? node)
    {
        if (node == null || string.IsNullOrEmpty(node.FilePath)) return;

        var browser = _relationalDbRegistry.Resolve(node.FilePath);
        if (browser == null)
        {
            _eventBus.Publish(new StatusMessageEvent("不支持的数据源类型"));
            return;
        }

        try
        {
            await browser.TestConnectionAsync(node.FilePath);
            _eventBus.Publish(new StatusMessageEvent($"连接成功: {node.Name}"));
        }
        catch (Exception ex)
        {
            _eventBus.Publish(new StatusMessageEvent(
                RelationalDbUserMessages.FormatTestConnectionFailure(node.Name, ex)));
        }
    }

    [RelayCommand]
    private void OpenConnectionConfig(TreeNodeViewModel? node)
    {
        if (node == null || !DatabaseConnectionFiles.IsMySqlConnectionFile(node.FilePath)) return;

        _eventBus.Publish(new TreeNodeSelectedEvent(new TreeNodeViewModel
        {
            Id = $"file:{node.FilePath}",
            Name = node.Name,
            FilePath = node.FilePath,
            NodeType = TreeNodeType.File,
            DataSourceType = DataSourceType.Other
        }));
    }

    public async Task<bool> CreateMySqlConnectionAsync(string targetDirectory, MySqlConnectionConfig config)
    {
        if (string.IsNullOrEmpty(targetDirectory) || !Directory.Exists(targetDirectory))
            return false;

        var baseName = string.IsNullOrWhiteSpace(config.Name) ? "mysql" : SanitizeFileName(config.Name.Trim());
        var fileName = $"{baseName}{DatabaseConnectionFiles.MySqlSuffix}";
        var counter = 1;
        while (File.Exists(Path.Combine(targetDirectory, fileName)))
        {
            fileName = $"{baseName}-{counter++}{DatabaseConnectionFiles.MySqlSuffix}";
        }

        var filePath = Path.Combine(targetDirectory, fileName);
        try
        {
            config.Save(filePath);
            var browser = _relationalDbRegistry.Resolve(filePath);
            if (browser != null)
                await browser.TestConnectionAsync(filePath);

            await RefreshTargetDirectoryAsync(targetDirectory);
            _eventBus.Publish(new StatusMessageEvent($"已创建 MySQL 连接: {fileName}"));
            return true;
        }
        catch (Exception ex)
        {
            if (File.Exists(filePath))
            {
                try { File.Delete(filePath); } catch { /* ignore */ }
            }

            _eventBus.Publish(new StatusMessageEvent($"创建 MySQL 连接失败: {ex.Message}"));
            return false;
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "mysql" : name;
    }

    [RelayCommand]
    private void ClosePanel()
    {
        _eventBus.Publish(new SidebarVisibilityChangedEvent(false));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDirectoryTreeAsync();
    }

    [RelayCommand]
    private async Task CommitRenameAsync(TreeNodeViewModel? node)
    {
        if (node == null || string.IsNullOrEmpty(node.FilePath)) return;

        var oldName = Path.GetFileName(node.FilePath);
        if (string.Equals(oldName, node.Name, StringComparison.Ordinal)) return;

        var oldPath = node.FilePath;
        var success = await _projectService.RenameEntryAsync(oldPath, node.Name);
        if (success)
        {
            var dir = Path.GetDirectoryName(oldPath);
            if (dir != null)
            {
                node.FilePath = Path.Combine(dir, node.Name);
            }

            if (node.NodeType == TreeNodeType.Folder)
            {
                UpdateChildPaths(node, oldPath, node.FilePath);
            }

            _eventBus.Publish(new StatusMessageEvent($"已重命名为 {node.Name}"));
        }
        else
        {
            node.Name = oldName;
            _eventBus.Publish(new StatusMessageEvent($"重命名失败"));
        }
    }

    private static void UpdateChildPaths(TreeNodeViewModel parent, string oldParentPath, string newParentPath)
    {
        foreach (var child in parent.Children)
        {
            if (!string.IsNullOrEmpty(child.FilePath) &&
                child.FilePath.StartsWith(oldParentPath, StringComparison.OrdinalIgnoreCase))
            {
                child.FilePath = newParentPath + child.FilePath.Substring(oldParentPath.Length);
            }
            UpdateChildPaths(child, oldParentPath, newParentPath);
        }
    }

    [RelayCommand]
    private async Task DeleteNodeAsync(TreeNodeViewModel? node)
    {
        if (node == null || string.IsNullOrEmpty(node.FilePath)) return;

        var success = await _projectService.DeleteEntryAsync(node.FilePath);
        if (success)
        {
            RemoveNodeFromTree(node);
            _eventBus.Publish(new StatusMessageEvent($"已删除 {node.Name}"));
        }
        else
        {
            _eventBus.Publish(new StatusMessageEvent($"删除失败，文件可能被占用"));
        }
    }

    private static TreeNodeViewModel? ResolveFolderContext(TreeNodeViewModel? contextNode)
    {
        if (contextNode is null)
            return null;

        if (contextNode.NodeType == TreeNodeType.Folder)
            return contextNode;

        if (contextNode.Parent?.NodeType == TreeNodeType.Folder)
            return contextNode.Parent;

        return null;
    }

    private string? ResolveTargetDirectory(TreeNodeViewModel? contextNode)
    {
        if (!HasProject || string.IsNullOrEmpty(ProjectDirectory))
            return null;

        var folderNode = ResolveFolderContext(contextNode);
        if (!string.IsNullOrEmpty(folderNode?.FilePath))
            return folderNode.FilePath;

        return ProjectDirectory;
    }

    [RelayCommand]
    private async Task CreateFolderAsync(TreeNodeViewModel? parentNode)
    {
        var targetDir = ResolveTargetDirectory(parentNode);
        if (string.IsNullOrEmpty(targetDir)) return;

        var folderName = "新建文件夹";
        var finalName = folderName;
        var counter = 1;
        while (Directory.Exists(Path.Combine(targetDir, finalName)))
        {
            finalName = $"{folderName}{counter++}";
        }

        var folderParent = ResolveFolderContext(parentNode);
        var success = await _projectService.CreateFolderAsync(targetDir, finalName);
        if (success)
        {
            var newNode = new TreeNodeViewModel
            {
                Name = finalName,
                FilePath = Path.Combine(targetDir, finalName),
                NodeType = TreeNodeType.Folder,
                Parent = folderParent
            };
            newNode.Children.Add(new TreeNodeViewModel { Name = "..." });

            if (folderParent != null)
            {
                folderParent.Children.Add(newNode);
                folderParent.IsExpanded = true;
            }
            else
            {
                RootChildren.Add(newNode);
            }

            _eventBus.Publish(new StatusMessageEvent($"已创建文件夹 {finalName}"));
        }
        else
        {
            _eventBus.Publish(new StatusMessageEvent($"创建文件夹失败"));
        }
    }

    [RelayCommand]
    private async Task CreateFileAsync(TreeNodeViewModel? parentNode)
    {
        var targetDir = ResolveTargetDirectory(parentNode);
        if (string.IsNullOrEmpty(targetDir)) return;

        const string baseName = "新建文件";
        const string extension = ".txt";
        var finalName = $"{baseName}{extension}";
        var counter = 1;
        while (File.Exists(Path.Combine(targetDir, finalName)))
        {
            finalName = $"{baseName}{counter++}{extension}";
        }

        var folderParent = ResolveFolderContext(parentNode);
        var success = await _projectService.CreateFileAsync(targetDir, finalName);
        if (success)
        {
            var filePath = Path.Combine(targetDir, finalName);
            var newNode = new TreeNodeViewModel
            {
                Name = finalName,
                FilePath = filePath,
                NodeType = GetNodeType(filePath),
                DataSourceType = GetDataSourceType(filePath),
                Parent = folderParent
            };

            if (folderParent != null)
            {
                folderParent.Children.Add(newNode);
                folderParent.IsExpanded = true;
            }
            else
            {
                RootChildren.Add(newNode);
            }

            _eventBus.Publish(new StatusMessageEvent($"已创建文件 {finalName}"));
        }
        else
        {
            _eventBus.Publish(new StatusMessageEvent("创建文件失败"));
        }
    }

    private void RemoveNodeFromTree(TreeNodeViewModel node)
    {
        if (node.Parent != null)
        {
            node.Parent.Children.Remove(node);
        }
        else
        {
            RootChildren.Remove(node);
        }
    }

    [RelayCommand]
    private async Task DropFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (!HasProject || string.IsNullOrEmpty(ProjectDirectory) || filePaths.Count == 0) return;

        var destDir = ProjectDirectory;

        if (SelectedTreeNode != null && SelectedTreeNode.NodeType == TreeNodeType.Folder)
        {
            destDir = SelectedTreeNode.FilePath;
        }

        var copied = await _projectService.ImportFilesAsync(filePaths, destDir);

        if (copied > 0)
        {
            await RefreshTargetDirectoryAsync(destDir);
            _eventBus.Publish(new StatusMessageEvent($"已导入 {copied} 个文件"));
        }
    }

    private async Task RefreshTargetDirectoryAsync(string directoryPath)
    {
        if (string.Equals(directoryPath, ProjectDirectory, StringComparison.OrdinalIgnoreCase))
        {
            await SyncDirectoryChildrenWithDiskAsync(null, ProjectDirectory, RootChildren);
            return;
        }

        foreach (var node in RootChildren)
        {
            if (await RefreshNodeDirectoryAsync(node, directoryPath)) return;
        }
    }

    private async Task<bool> RefreshNodeDirectoryAsync(TreeNodeViewModel node, string directoryPath)
    {
        if (node.NodeType == TreeNodeType.Folder
            && !string.IsNullOrEmpty(node.FilePath)
            && string.Equals(Path.GetFullPath(node.FilePath), directoryPath, StringComparison.OrdinalIgnoreCase))
        {
            if (node.Children.Count == 1 && node.Children[0].Name == "...")
                return true;

            await SyncDirectoryChildrenWithDiskAsync(node, node.FilePath, node.Children);
            return true;
        }

        foreach (var child in node.Children)
        {
            if (child.NodeType == TreeNodeType.Folder && await RefreshNodeDirectoryAsync(child, directoryPath))
                return true;
        }

        return false;
    }

    private async Task SyncDirectoryChildrenWithDiskAsync(
        TreeNodeViewModel? parentNode,
        string directoryPath,
        ObservableCollection<TreeNodeViewModel> targetCollection)
    {
        var entries = EnumerateSortedDirectoryEntries(directoryPath);
        if (entries == null)
            return;

        var diskPaths = new HashSet<string>(
            entries.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);

        for (var i = targetCollection.Count - 1; i >= 0; i--)
        {
            var child = targetCollection[i];
            if (string.Equals(child.Name, "...", StringComparison.Ordinal))
                continue;

            if (string.IsNullOrEmpty(child.FilePath)
                || !diskPaths.Contains(Path.GetFullPath(child.FilePath)))
            {
                targetCollection.RemoveAt(i);
            }
        }

        var existingPaths = new HashSet<string>(
            targetCollection
                .Where(c => !string.Equals(c.Name, "...", StringComparison.Ordinal) && !string.IsNullOrEmpty(c.FilePath))
                .Select(c => Path.GetFullPath(c.FilePath!)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entryPath in entries)
        {
            var normalized = Path.GetFullPath(entryPath);
            if (existingPaths.Contains(normalized))
                continue;

            await InsertEntryIntoCollectionAsync(entryPath, targetCollection, parentNode);
        }
    }

    public bool CanDropOnNode(TreeNodeViewModel source, TreeNodeViewModel? target)
    {
        if (string.IsNullOrEmpty(source.FilePath)) return false;

        if (target == null)
        {
            if (string.IsNullOrEmpty(ProjectDirectory)) return false;
            var sourceDir = Path.GetDirectoryName(source.FilePath);
            return !string.Equals(sourceDir, ProjectDirectory, StringComparison.OrdinalIgnoreCase);
        }

        if (source == target) return false;
        if (target.NodeType != TreeNodeType.Folder) return false;
        if (string.IsNullOrEmpty(target.FilePath)) return false;

        if (target.FilePath.StartsWith(source.FilePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        var srcDir = Path.GetDirectoryName(source.FilePath);
        if (string.Equals(srcDir, target.FilePath, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public string GetDropTargetPath(TreeNodeViewModel? target)
    {
        return target != null ? target.FilePath : ProjectDirectory;
    }

    public string GetDropTargetName(TreeNodeViewModel? target)
    {
        return target != null ? target.Name : Path.GetFileName(ProjectDirectory);
    }

    [RelayCommand]
    private async Task MoveNodeToFolderAsync((TreeNodeViewModel Source, TreeNodeViewModel? Target) args)
    {
        var (source, target) = args;
        var targetPath = GetDropTargetPath(target);
        var targetName = GetDropTargetName(target);

        if (source.NodeType == TreeNodeType.Folder && Directory.Exists(source.FilePath))
        {
            var sourceDirName = Path.GetFileName(source.FilePath);
            var targetSubDir = Path.Combine(targetPath, sourceDirName);

            if (Directory.Exists(targetSubDir))
            {
                var success = await _projectService.MergeDirectoryAsync(source.FilePath, targetSubDir);
                if (success)
                {
                    RemoveNodeFromTree(source);
                    _eventBus.Publish(new StatusMessageEvent($"已合并文件夹 {source.Name} → {targetName}"));
                }
                else
                {
                    _eventBus.Publish(new StatusMessageEvent($"合并文件夹失败"));
                }
                return;
            }
        }

        var moveSuccess = await _projectService.MoveEntryAsync(source.FilePath, targetPath);
        if (moveSuccess)
        {
            var oldPath = source.FilePath;
            source.FilePath = Path.Combine(targetPath, source.Name);

            if (source.NodeType == TreeNodeType.Folder)
            {
                UpdateChildPaths(source, oldPath, source.FilePath);
            }

            RemoveNodeFromTree(source);

            if (target != null && target.NodeType == TreeNodeType.Folder)
            {
                target.Children.Add(source);
                source.Parent = target;
            }
            else
            {
                RootChildren.Add(source);
                source.Parent = null;
            }

            _eventBus.Publish(new StatusMessageEvent($"已移动 {source.Name} → {targetName}"));
        }
        else
        {
            _eventBus.Publish(new StatusMessageEvent($"移动失败"));
        }
    }

}
