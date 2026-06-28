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
    private readonly ProjectDirectoryWatcher _directoryWatcher;
    private string _projectDirectoryFullPath = string.Empty;

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
        ".zerofall.db", ".datafinder.db", ".git", ".svn", ".hg", "node_modules", "__pycache__", ".vs", ".idea", "bin", "obj"
    };

    public SidebarViewModel(
        IRelationalDbBrowserRegistry relationalDbRegistry,
        IProjectService projectService,
        IEventBus eventBus)
    {
        _relationalDbRegistry = relationalDbRegistry;
        _projectService = projectService;
        _eventBus = eventBus;
        _directoryWatcher = new ProjectDirectoryWatcher(ShouldIgnoreWatcherPath, OnExternalDirectoryChanged);
        SubscribeEvent(eventBus, (DataSourceChangedEvent e) => OnDataSourceChanged(e));
        SubscribeEvent(eventBus, (ProjectOpenedEvent e) => SetProjectDirectory(e.DirectoryPath, e.DatabasePath));
    }

    public void SetProjectDirectory(string directoryPath, string databasePath)
    {
        ProjectDirectory = directoryPath;
        ProjectDatabasePath = databasePath;
        _projectDirectoryFullPath = Path.GetFullPath(directoryPath);
        HasProject = true;
        _directoryWatcher.Start(_projectDirectoryFullPath);
        _ = LoadDirectoryTreeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _directoryWatcher.Dispose();

        base.Dispose(disposing);
    }

    private static bool ShouldIgnoreWatcherPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (HiddenNames.Contains(segment))
                return true;
        }

        return false;
    }

    private void OnExternalDirectoryChanged(IReadOnlyList<DirectoryWatchNotification> notifications)
    {
        Dispatcher.UIThread.Post(
            () => _ = HandleExternalDirectoryChangesAsync(notifications),
            DispatcherPriority.Background);
    }

    private async Task HandleExternalDirectoryChangesAsync(IReadOnlyList<DirectoryWatchNotification> notifications)
    {
        if (!HasProject || string.IsNullOrEmpty(_projectDirectoryFullPath))
            return;

        var refreshDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var notification in notifications)
        {
            switch (notification.Kind)
            {
                case DirectoryWatchChangeKind.Deleted:
                    if (TryRemoveNodeByPath(notification.FullPath))
                        continue;
                    AddRefreshDirectory(refreshDirectories, GetParentDirectory(notification.FullPath));
                    break;

                case DirectoryWatchChangeKind.Renamed:
                    TryRemoveNodeByPath(notification.OldFullPath);
                    AddRefreshDirectory(refreshDirectories, GetParentDirectory(notification.OldFullPath));
                    AddRefreshDirectory(refreshDirectories, GetParentDirectory(notification.FullPath));
                    break;

                default:
                    AddRefreshDirectory(refreshDirectories, GetParentDirectory(notification.FullPath));
                    break;
            }
        }

        foreach (var directory in refreshDirectories)
            await RefreshDirectoryFromWatcherAsync(directory);
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
            {
                await RefreshTargetDirectoryAsync(ProjectDirectory);
                return;
            }

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

        var expandedPaths = CollectExpandedPaths(RootChildren);

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

    private static HashSet<string> CollectExpandedPaths(ObservableCollection<TreeNodeViewModel> nodes)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            }
            CollectExpandedPathsRecursive(node.Children, paths);
        }
    }

    private async Task RestoreExpandedPathsAsync(ObservableCollection<TreeNodeViewModel> nodes, HashSet<string> expandedPaths)
    {
        foreach (var node in nodes)
        {
            if (!string.IsNullOrEmpty(node.FilePath) && expandedPaths.Contains(node.FilePath))
            {
                node.IsExpanded = true;
                if (node.NodeType == TreeNodeType.Folder && node.Children.Count == 1 && node.Children[0].Name == "...")
                {
                    node.Children.Clear();
                    await LoadDirectoryContentsAsync(node.FilePath, node.Children, node);
                }
            }
            await RestoreExpandedPathsAsync(node.Children, expandedPaths);
        }
    }

    private async Task LoadDirectoryContentsAsync(string directoryPath, ObservableCollection<TreeNodeViewModel> targetCollection, TreeNodeViewModel? parentNode)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directoryPath)
                .Where(p => !HiddenNames.Contains(Path.GetFileName(p)))
                .OrderBy(p => !Directory.Exists(p))
                .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

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

        if (node.NodeType is TreeNodeType.Folder or TreeNodeType.Database)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }

        _eventBus.Publish(new TreeNodeSelectedEvent(node));
        _eventBus.Publish(new StatusMessageEvent($"已选择: {node.Name}"));
    }

    [RelayCommand]
    private async Task TreeNodeDoubleClickedAsync(TreeNodeViewModel? node)
    {
        if (node == null || node.NodeType != TreeNodeType.DataSource || string.IsNullOrEmpty(node.FilePath))
            return;

        if (node.DataSourceType is not (DataSourceType.MySql or DataSourceType.Sqlite))
            return;

        node.IsExpanded = true;
        await RefreshConnectionTablesAsync(node);
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

    [RelayCommand]
    private async Task CreateFolderAsync(TreeNodeViewModel? parentNode)
    {
        if (!HasProject || string.IsNullOrEmpty(ProjectDirectory)) return;

        var targetDir = parentNode?.NodeType == TreeNodeType.Folder
            ? parentNode.FilePath
            : ProjectDirectory;

        if (string.IsNullOrEmpty(targetDir)) return;

        var folderName = "新建文件夹";
        var finalName = folderName;
        var counter = 1;
        while (Directory.Exists(Path.Combine(targetDir, finalName)))
        {
            finalName = $"{folderName}{counter++}";
        }

        var success = await _projectService.CreateFolderAsync(targetDir, finalName);
        if (success)
        {
            var newNode = new TreeNodeViewModel
            {
                Name = finalName,
                FilePath = Path.Combine(targetDir, finalName),
                NodeType = TreeNodeType.Folder,
                Parent = parentNode
            };
            newNode.Children.Add(new TreeNodeViewModel { Name = "..." });

            if (parentNode != null && parentNode.NodeType == TreeNodeType.Folder)
            {
                parentNode.Children.Add(newNode);
                parentNode.IsExpanded = true;
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
        if (!HasProject || string.IsNullOrEmpty(ProjectDirectory)) return;

        var targetDir = parentNode?.NodeType == TreeNodeType.Folder
            ? parentNode.FilePath
            : ProjectDirectory;

        if (string.IsNullOrEmpty(targetDir)) return;

        const string baseName = "新建文件";
        const string extension = ".txt";
        var finalName = $"{baseName}{extension}";
        var counter = 1;
        while (File.Exists(Path.Combine(targetDir, finalName)))
        {
            finalName = $"{baseName}{counter++}{extension}";
        }

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
                Parent = parentNode?.NodeType == TreeNodeType.Folder ? parentNode : null
            };

            if (parentNode != null && parentNode.NodeType == TreeNodeType.Folder)
            {
                parentNode.Children.Add(newNode);
                parentNode.IsExpanded = true;
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
            var expandedPaths = CollectExpandedPaths(RootChildren);
            RootChildren.Clear();
            await LoadDirectoryContentsAsync(ProjectDirectory, RootChildren, null);
            await RestoreExpandedPathsAsync(RootChildren, expandedPaths);
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
            var expandedPaths = CollectExpandedPaths(node.Children);
            node.Children.Clear();
            await LoadDirectoryContentsAsync(node.FilePath, node.Children, node);
            await RestoreExpandedPathsAsync(node.Children, expandedPaths);
            return true;
        }

        foreach (var child in node.Children)
        {
            if (child.NodeType == TreeNodeType.Folder && await RefreshNodeDirectoryAsync(child, directoryPath))
                return true;
        }

        return false;
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

    private void OnDataSourceChanged(DataSourceChangedEvent e)
    {
        if (HasProject)
        {
            _ = LoadDirectoryTreeAsync();
        }
    }
}
