using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ZeroFall.Base.Data;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Services;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Providers;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.Dock.Services;

public class ContentCreationService
{
    private static readonly HashSet<string> SqliteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".sqlite3"
    };

    private readonly ISqliteService _sqliteService;
    private readonly IRelationalDbBrowserRegistry _relationalDbRegistry;
    private readonly IContentFactoryRegistry _contentFactoryRegistry;
    private readonly IFileTypeInspector _fileTypeInspector;

    public ContentCreationService(
        ISqliteService sqliteService,
        IRelationalDbBrowserRegistry relationalDbRegistry,
        IContentFactoryRegistry contentFactoryRegistry,
        IFileTypeInspector fileTypeInspector)
    {
        _sqliteService = sqliteService;
        _relationalDbRegistry = relationalDbRegistry;
        _contentFactoryRegistry = contentFactoryRegistry;
        _fileTypeInspector = fileTypeInspector;
    }

    private object? CreateFromFactory(string contentType, ContentFactoryContext ctx, Func<object?> fallback)
    {
        if (_contentFactoryRegistry.TryCreateContent(contentType, ctx, out var content) && content != null)
            return content;

        return fallback();
    }

    public object? CreateContentForDataSource(TreeNodeSelectedEvent e)
    {
        if (e.Node.NodeType == TreeNodeType.File)
            return CreateFilePreview(e.Node);

        if (e.Node.DataSourceType == DataSourceType.Csv && !string.IsNullOrEmpty(e.Node.FilePath))
        {
            var ctx = new ContentFactoryContext
            {
                Id = e.Node.Id,
                Title = e.Node.Name,
                FilePath = e.Node.FilePath,
                DataSourceType = e.Node.DataSourceType,
                NodeType = e.Node.NodeType
            };

            return CreateFromFactory("csv-data", ctx, () =>
            {
                try { return DataTableViewModel.FromCsv(e.Node.FilePath); }
                catch (Exception ex) { return new TextBlock { Text = $"加载失败: {ex.Message}" }; }
            });
        }

        return new TextBlock { Text = $"内容区域 - {e.Node.Name}" };
    }

    private object? CreateFilePreview(TreeNodeViewModel node)
    {
        if (string.IsNullOrEmpty(node.FilePath))
            return new TextBlock { Text = "无文件路径" };

        var ext = Path.GetExtension(node.FilePath).ToLowerInvariant();

        if (ext == ".csv")
        {
            var ctx = new ContentFactoryContext
            {
                Id = node.Id,
                Title = node.Name,
                FilePath = node.FilePath,
                DataSourceType = DataSourceType.Csv,
                NodeType = node.NodeType
            };

            return CreateFromFactory("csv-data", ctx, () =>
            {
                try { return DataTableViewModel.FromCsv(node.FilePath); }
                catch (Exception ex) { return new TextBlock { Text = $"加载失败: {ex.Message}" }; }
            });
        }

        if (IsTextPreviewFile(node.FilePath))
        {
            var ctx = new ContentFactoryContext
            {
                Id = node.Id,
                Title = node.Name,
                FilePath = node.FilePath,
                NodeType = node.NodeType
            };

            return CreateFromFactory("text-file", ctx, () =>
            {
                try
                {
                    var content = File.ReadAllText(node.FilePath);
                    return new TextBlock { Text = content, TextWrapping = TextWrapping.NoWrap };
                }
                catch (Exception ex) { return new TextBlock { Text = $"加载失败: {ex.Message}" }; }
            });
        }

        if (IsBinaryPreviewFile(node.FilePath))
        {
            return CreateBinaryFilePreview(node.FilePath, node.Name, node.Id);
        }

        return new TextBlock { Text = $"无法预览: {node.Name} ({_fileTypeInspector.Probe(node.FilePath).MimeType ?? "未知类型"})" };
    }

    private bool IsTextPreviewFile(string filePath)
    {
        if (DatabaseConnectionFiles.IsMySqlConnectionFile(filePath))
            return true;

        var ext = Path.GetExtension(filePath);
        if (SqliteExtensions.Contains(ext))
            return false;

        return _fileTypeInspector.IsTextFile(filePath);
    }

    private bool IsBinaryPreviewFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        if (DatabaseConnectionFiles.IsMySqlConnectionFile(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return false;

        return !IsTextPreviewFile(filePath);
    }

    /// <summary>在 Content 区打开工作区文件（文本编辑器或 Hex 二进制预览）。</summary>
    public object? CreateWorkspaceFilePreview(string filePath, string title)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return new TextBlock { Text = "文件不存在" };

        if (IsTextPreviewFile(filePath))
            return CreateTextFilePreview(filePath, title);

        if (IsBinaryPreviewFile(filePath))
            return CreateBinaryFilePreview(filePath, title, BuildFileTabId(filePath));

        return new TextBlock { Text = $"无法预览: {title}" };
    }

    private object? CreateBinaryFilePreview(string filePath, string title, string? tabId = null)
    {
        var ctx = new ContentFactoryContext
        {
            Id = tabId ?? BuildFileTabId(filePath),
            Title = title,
            FilePath = filePath,
            NodeType = TreeNodeType.File
        };

        return CreateFromFactory("binary-file", ctx, () =>
            new TextBlock { Text = $"无法加载二进制预览: {title}" });
    }

    /// <summary>在 Content 区打开文本编辑器 Tab（与侧边栏选中文件相同逻辑）。</summary>
    public object? CreateTextFilePreview(string filePath, string title)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return new TextBlock { Text = "文件不存在" };

        if (!IsTextPreviewFile(filePath))
            return new TextBlock { Text = $"不是可编辑的文本文件: {title}" };

        var ctx = new ContentFactoryContext
        {
            Id = BuildFileTabId(filePath),
            Title = title,
            FilePath = filePath,
            NodeType = TreeNodeType.File
        };

        return CreateFromFactory("text-file", ctx, () =>
        {
            try
            {
                var content = File.ReadAllText(filePath);
                return new TextBlock { Text = content, TextWrapping = TextWrapping.NoWrap };
            }
            catch (Exception ex)
            {
                return new TextBlock { Text = $"加载失败: {ex.Message}" };
            }
        });
    }

    public static string BuildFileTabId(string filePath) =>
        "file:" + Path.GetFullPath(filePath).Replace('\\', '/').ToLowerInvariant();

    public async Task<TableLoadResult> LoadTableDataAsync(string filePath, string tableName, DataSourceType dataSourceType = DataSourceType.Sqlite)
    {
        try
        {
            var browser = _relationalDbRegistry.Resolve(filePath);
            if (browser == null)
                return TableLoadResult.Failure($"不支持的数据源: {filePath}");

            var provider = RelationalDbDataProvider.ForTable(browser, filePath, tableName);
            return await BuildTableContentOnUiThreadAsync(provider, filePath, tableName);
        }
        catch (Exception ex)
        {
            return TableLoadResult.Failure($"错误: {ex.Message}");
        }
    }

    private async Task<TableLoadResult> BuildTableContentOnUiThreadAsync(
        RelationalDbDataProvider provider, string filePath, string tableName)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return await BuildTableContentCoreAsync(provider, filePath, tableName);

        return await Dispatcher.UIThread.InvokeAsync(() =>
            BuildTableContentCoreAsync(provider, filePath, tableName));
    }

    private async Task<TableLoadResult> BuildTableContentCoreAsync(
        RelationalDbDataProvider provider, string filePath, string tableName)
    {
        var dtvm = new DataTableViewModel
        {
            UserPageSize = 200,
            ShowHeaderPanel = false
        };
        await dtvm.InitializePagedAsync(provider);

        var ctx = new ContentFactoryContext
        {
            FilePath = filePath,
            TableName = tableName,
            Extra = { ["DataTableViewModel"] = dtvm }
        };

        if (!_contentFactoryRegistry.TryCreateContent("data-table", ctx, out var tableContent) ||
            tableContent == null)
        {
            return TableLoadResult.Failure("无法创建表格视图");
        }

        return TableLoadResult.Success(tableContent, dtvm);
    }

    public object? CreateSqlEditorContent(string queryId, string filePath, string dataSourceName)
    {
        var ctx = new ContentFactoryContext
        {
            Id = queryId,
            Title = $"查询 - {dataSourceName}",
            FilePath = filePath,
            DataSourceName = dataSourceName
        };

        return CreateFromFactory("sql-editor", ctx, () => new TextBlock { Text = $"SQL编辑器 - {dataSourceName}" });
    }

    public object? CreateDataTableContent(string filePath, DataTableViewModel dtvm)
    {
        var ctx = new ContentFactoryContext
        {
            FilePath = filePath,
            Extra = { ["DataTableViewModel"] = dtvm }
        };

        return CreateFromFactory("data-table", ctx, () => dtvm);
    }

    public async Task<long> GetTableRowCountAsync(string filePath, string tableName)
    {
        try
        {
            var browser = _relationalDbRegistry.Resolve(filePath);
            if (browser != null)
                return await browser.GetTableRowCountAsync(filePath, tableName);

            return await _sqliteService.GetTableRowCountAsync(filePath, tableName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ContentCreationService] GetTableRowCountAsync failed: {ex.Message}, File: {filePath}, Table: {tableName}");
            return 0;
        }
    }

    public async Task<ProviderContentResult> CreateFromProviderAsync(IDataProvider provider)
    {
        try
        {
            var dtvm = new DataTableViewModel();
            await dtvm.InitializePagedAsync(provider);

            var ctx = new ContentFactoryContext
            {
                FilePath = provider.DatabasePath,
                TableName = provider.QuerySource,
                Extra = { ["DataTableViewModel"] = dtvm, ["IDataProvider"] = provider }
            };

            if (_contentFactoryRegistry.TryCreateContent("data-table", ctx, out var tableContent) && tableContent != null)
                return ProviderContentResult.Success(tableContent, dtvm);

            return ProviderContentResult.Success(dtvm, dtvm);
        }
        catch (Exception ex)
        {
            return ProviderContentResult.Failure($"加载失败: {ex.Message}");
        }
    }
}

public class TableLoadResult
{
    public object? Content { get; init; }
    public DataTableViewModel? DataTable { get; init; }
    public string? Error { get; init; }
    public bool IsSuccess => Error == null;

    public static TableLoadResult Success(object? content, DataTableViewModel? dataTable) => new()
    {
        Content = content,
        DataTable = dataTable
    };

    public static TableLoadResult Failure(string error) => new()
    {
        Error = error
    };
}

public class ProviderContentResult
{
    public object? Content { get; init; }
    public DataTableViewModel? DataTable { get; init; }
    public string? Error { get; init; }
    public bool IsSuccess => Error == null;

    public static ProviderContentResult Success(object? content, DataTableViewModel? dataTable) => new()
    {
        Content = content,
        DataTable = dataTable
    };

    public static ProviderContentResult Failure(string error) => new()
    {
        Error = error
    };
}
