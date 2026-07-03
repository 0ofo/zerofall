using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.AssetRecon.ViewModels;

public partial class AssetReconLeftPanelViewModel : ViewModelBase
{
    private readonly ISqliteService _sqliteService;
    private readonly IWorkspaceService _workspaceService;
    private readonly IEventBus _eventBus;

    [ObservableProperty]
    private ObservableCollection<SearchTaskItem> _searchTasks = new();

    [ObservableProperty]
    private SearchTaskItem? _selectedTask;

    [ObservableProperty]
    private ObservableCollection<DetailProperty> _detailProperties = new();

    [ObservableProperty]
    private AssetReconDetailScope _detailScope;

    /// <summary>递增以使 Flyout 视图在异步填充完成后同步。</summary>
    [ObservableProperty]
    private int _detailGeneration;

    public AssetReconLeftPanelViewModel(ISqliteService sqliteService, IWorkspaceService workspaceService,
        IEventBus eventBus)
    {
        _sqliteService = sqliteService;
        _workspaceService = workspaceService;
        _eventBus = eventBus;

        _workspaceService.WorkspaceOpened += OnWorkspaceOpened;
        _workspaceService.WorkspaceClosed += OnWorkspaceClosed;

        if (_workspaceService.HasWorkspace)
            Dispatcher.UIThread.Post(RequestReloadTasksFromDisk, DispatcherPriority.Background);
    }

    private void OnWorkspaceOpened(object? sender, Workspace e) =>
        Dispatcher.UIThread.Post(RequestReloadTasksFromDisk, DispatcherPriority.Background);

    private void OnWorkspaceClosed(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(HandleWorkspaceClosedOnUiThread, DispatcherPriority.Background);

    private void HandleWorkspaceClosedOnUiThread()
    {
        SelectedTask = null;
        SearchTasks.Clear();
        ClearDetail();
    }

    private void RequestReloadTasksFromDisk() =>
        _ = LoadTasksCommand.ExecuteAsync(null);

    [RelayCommand]
    private void OpenHistoryResults(SearchTaskItem? task)
    {
        if (task is null)
            return;

        SelectedTask = task;

        var dbPath = _workspaceService.GetDatabasePath();
        if (string.IsNullOrEmpty(dbPath))
            return;

        _eventBus.Publish(new AssetReconHistoryResultsOpenRequestedEvent(dbPath, task.TaskId, task.Query));
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(SearchTaskItem? task)
    {
        if (task is null)
            return;

        var dbPath = _workspaceService.GetDatabasePath();
        if (string.IsNullOrEmpty(dbPath))
            return;

        var taskId = task.TaskId.Replace("'", "''");
        var sql = $"DELETE FROM \"asset_recon_results\" WHERE query_task_id = '{taskId}'";
        await _sqliteService.ExecuteNonQueryAsync(dbPath, sql).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SearchTasks.Remove(task);
            if (ReferenceEquals(SelectedTask, task))
                SelectedTask = null;
            ClearDetailIfResultRow();
        });

        _eventBus.Publish(new StatusMessageEvent($"已删除搜索历史：{task.Query}"));
    }

    [RelayCommand]
    private async Task LoadTasksAsync()
    {
        var dbPath = _workspaceService.GetDatabasePath();
        if (string.IsNullOrEmpty(dbPath)) return;

        var sql = "SELECT query_task_id, query, source, MIN(created_at) " +
                  "FROM \"asset_recon_results\" " +
                  "GROUP BY query_task_id, query, source " +
                  "ORDER BY MIN(created_at) DESC";

        var result = await _sqliteService.ExecuteQueryPagedAsync(dbPath, sql, 0, 200);

        var grouped = new Dictionary<string, SearchTaskItem>();

        foreach (var row in result.Rows)
        {
            var taskId = row[0]?.ToString() ?? string.Empty;
            var query = row[1]?.ToString() ?? string.Empty;
            var source = row[2]?.ToString() ?? string.Empty;
            var createdAt = row[3]?.ToString() ?? string.Empty;

            if (!grouped.TryGetValue(taskId, out var task))
            {
                task = new SearchTaskItem { TaskId = taskId, Query = query, CreatedAt = createdAt };
                grouped[taskId] = task;
            }

            if (!task.Sources.Contains(source))
                task.Sources.Add(source);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SearchTasks.Clear();
            foreach (var task in grouped.Values)
                SearchTasks.Add(task);
        });
    }

    public async Task ShowAssetDetailAsync(string dbPath, string taskId, int sortOrder)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            DetailScope = AssetReconDetailScope.ResultRow;
            DetailProperties.Clear();
        });

        var allColumns = new[]
        {
            "ip", "port", "protocol", "title", "domain", "country", "country_code",
            "province", "city", "org", "isp", "os", "server", "banner", "status_code",
            "product", "product_category", "version", "cert_issuer", "cert_subject",
            "icp", "as_number", "header", "jarm", "cname", "vuln", "base_protocol",
            "link", "cert", "longitude", "latitude", "company", "service_name",
            "scene", "district", "x_powered_by", "quake_id", "updated_at"
        };

        var cols = string.Join(", ", allColumns.Select(c => $"\"{c}\""));
        var sql = $"SELECT {cols} FROM \"asset_recon_results\" " +
                  $"WHERE query_task_id = '{taskId}' AND sort_order = {sortOrder}";

        var result = await _sqliteService.ExecuteQueryPagedAsync(dbPath, sql, 0, 1).ConfigureAwait(false);
        if (result.Rows.Count == 0)
        {
            await Dispatcher.UIThread.InvokeAsync(ClearDetail);
            return;
        }

        var row = result.Rows[0];
        var labels = new[]
        {
            "IP", "端口", "协议", "标题", "域名", "国家", "国家代码",
            "省份", "城市", "组织", "ISP", "操作系统", "服务器", "Banner", "状态码",
            "产品", "产品分类", "版本", "证书颁发者", "证书主体",
            "ICP", "AS号", "响应头", "JARM", "CNAME", "漏洞", "基础协议",
            "链接", "证书", "经度", "纬度", "公司", "服务名",
            "场景", "区域", "X-Powered-By", "QuakeID", "更新时间"
        };

        var items = new List<DetailProperty>();
        for (var i = 0; i < labels.Length && i < row.Count; i++)
        {
            var val = row[i]?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(val))
                items.Add(new DetailProperty(labels[i], val));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyDetailPropertiesWithLinkFirst(items);
            BumpDetailGeneration();
        });
    }

    public void ClearDetailIfResultRow()
    {
        if (DetailScope != AssetReconDetailScope.ResultRow)
            return;
        ClearDetail();
    }

    public void ClearDetail()
    {
        DetailProperties.Clear();
        DetailScope = AssetReconDetailScope.None;
        BumpDetailGeneration();
    }

    /// <summary>有「链接」时置于列表最前。</summary>
    private void ApplyDetailPropertiesWithLinkFirst(IReadOnlyList<DetailProperty> items)
    {
        DetailProperties.Clear();
        if (items.Count == 0)
            return;

        var linkIndex = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Label == "链接")
            {
                linkIndex = i;
                break;
            }
        }

        if (linkIndex >= 0)
        {
            DetailProperties.Add(items[linkIndex]);
            for (var i = 0; i < items.Count; i++)
            {
                if (i != linkIndex)
                    DetailProperties.Add(items[i]);
            }
            return;
        }

        foreach (var p in items)
            DetailProperties.Add(p);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _workspaceService.WorkspaceOpened -= OnWorkspaceOpened;
            _workspaceService.WorkspaceClosed -= OnWorkspaceClosed;
        }

        base.Dispose(disposing);
    }

    private void BumpDetailGeneration() => DetailGeneration++;
}

public class SearchTaskItem
{
    public string TaskId { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public List<string> Sources { get; init; } = new();
    public string CreatedAt { get; init; } = string.Empty;
    public string DisplaySources => string.Join(", ", Sources);
}

public class DetailProperty
{
    public string Label { get; }
    public string Value { get; }

    public DetailProperty(string label, string value)
    {
        Label = label;
        Value = value;
    }
}
