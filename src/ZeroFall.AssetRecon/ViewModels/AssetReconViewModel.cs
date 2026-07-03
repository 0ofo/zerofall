using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.AssetRecon.Providers;
using ZeroFall.AssetRecon.Services;
using ZeroFall.AssetRecon.Views;
using ZeroFall.Base.Data;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Views;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.AssetRecon.ViewModels;

public partial class AssetReconViewModel : ViewModelBase
{
    private const string ResultsBottomTabId = "asset-recon-results";

    private const int UserResultsPageSize = 20;

    /// <summary>聚合模式（多源或统一语法）下，每个情报源单次 API 请求的条数上限。</summary>
    private const int AggregatedPerSourceApiPageSize = 10;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isQuerying;

    /// <summary>0 智能聚合，1 FOFA，2 Hunter，3 360 Quake（与面板 ComboBox 顺序一致）。</summary>
    [ObservableProperty]
    private int _sourceModeIndex;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _quotaColumnFofa = "F —";

    [ObservableProperty]
    private string _quotaColumnHunter = "H —";

    [ObservableProperty]
    private string _quotaColumnQuake = "Q —";

    public DataTableViewModel ResultsTable { get; }

    /// <summary>左侧搜索历史面板 VM（选中任务时打开底部历史结果表）。</summary>
    public AssetReconLeftPanelViewModel? LeftPanelViewModel => _leftPanelVm;

    private readonly IEventBus _eventBus;
    private readonly ISettingsService _settingsService;
    private readonly ISqliteService _sqliteService;
    private readonly IWorkspaceService _workspaceService;
    private readonly AssetReconQuotaClient _quotaClient;
    private readonly AssetReconQueryService _queryService;

    private AssetReconLeftPanelViewModel? _leftPanelVm;
    private AssetReconResultsView? _resultsView;
    private string _dbPath = string.Empty;
    private string _queryTaskId = string.Empty;
    private int _quotaColumnsLoadScheduled;

    /// <summary>供结果表行详情：当前会话测绘对应的数据库路径与任务 ID。</summary>
    internal bool TryGetReconAssetRowSource(out string dbPath, out string queryTaskId)
    {
        dbPath = _dbPath;
        queryTaskId = _queryTaskId;
        return !string.IsNullOrEmpty(dbPath) && !string.IsNullOrEmpty(queryTaskId);
    }

    public AssetReconViewModel(IEventBus eventBus, ISettingsService settingsService,
        ISqliteService sqliteService,
        IWorkspaceService workspaceService,
        AssetReconQuotaClient quotaClient,
        AssetReconQueryService queryService)
    {
        _eventBus = eventBus;
        _settingsService = settingsService;
        _sqliteService = sqliteService;
        _workspaceService = workspaceService;
        _quotaClient = quotaClient;
        _queryService = queryService;

        ResultsTable = new DataTableViewModel
        {
            UserPageSize = UserResultsPageSize,
            ShowHeaderPanel = false,
            OpenUrlInApp = OpenAssetReconLinkInBrowser
        };
        SubscribeEvent(eventBus,
            (AssetReconHistoryResultsOpenRequestedEvent e) =>
                Dispatcher.UIThread.Post(() => _ = OpenHistoryResultsBottomTabAsync(e),
                    DispatcherPriority.Normal));
    }

    private void OpenAssetReconLinkInBrowser(string url)
    {
        var title = "预览";
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
                title = u.Host;
        }
        catch { /* keep default */ }

        _eventBus.Publish(new OpenBrowserTabRequestedEvent(url, title));
    }

    partial void OnSourceModeIndexChanged(int value) => ScheduleRefreshAllQuotaColumns();

    private async Task OpenHistoryResultsBottomTabAsync(AssetReconHistoryResultsOpenRequestedEvent e)
    {
        var tabId = $"asset-recon-history-{e.QueryTaskId}";
        var historicalTable = new DataTableViewModel
        {
            UserPageSize = UserResultsPageSize,
            ShowHeaderPanel = false,
            OpenUrlInApp = OpenAssetReconLinkInBrowser
        };

        var provider = new AssetReconHistoricalResultsProvider(_sqliteService,
            e.DatabasePath, e.QueryTaskId, e.QueryText);
        await historicalTable.InitializePagedAsync(provider).ConfigureAwait(true);

        if (_leftPanelVm is null)
            return;

        var view = new AssetReconHistoryDiagnosticView { DataContext = historicalTable };
        var dbSnap = e.DatabasePath;
        var taskSnap = e.QueryTaskId;
        _ = new AssetReconAssetRowFlyoutBinder(view, historicalTable, _leftPanelVm,
            () => (dbSnap, taskSnap));

        var icon = IconHelper.GetIcon("SemiIconListView");

        var tab = new DockTabItemViewModel
        {
            Id = tabId,
            Title = BuildHistoryResultTabTitle(e.QueryText),
            Icon = icon,
            Content = view,
            IsClosable = true
        };

        _eventBus.Publish(new AddDockTabEvent(DockPosition.Bottom, tab));
        _eventBus.Publish(new TerminalVisibilityChangedEvent(true));
        _eventBus.Publish(new SwitchDockTabRequestedEvent(DockPosition.Bottom, tabId));
    }

    private static string BuildHistoryResultTabTitle(string query)
    {
        var t = query.Trim();
        const int maxInner = 28;
        var inner = t.Length <= maxInner ? t : t[..maxInner] + "…";
        return $"历史 · {inner}";
    }

    private void OpenResultsBottomTab()
    {
        _resultsView ??= new AssetReconResultsView { DataContext = this };
        _eventBus.Publish(new AddDockTabEvent(DockPosition.Bottom, new DockTabItemViewModel
        {
            Id = ResultsBottomTabId,
            Title = "侦察结果",
            Icon = IconHelper.GetIcon("SemiIconListView"),
            Content = _resultsView,
            IsClosable = true
        }));
        _eventBus.Publish(new TerminalVisibilityChangedEvent(true));
        _eventBus.Publish(new SwitchDockTabRequestedEvent(DockPosition.Bottom, ResultsBottomTabId));
    }

    [RelayCommand]
    private async Task QueryAsync()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            StatusText = "请输入查询内容";
            return;
        }

        IsQuerying = true;
        try
        {
            var result = await _queryService.ExecuteAsync(new AssetReconQueryRequest
            {
                Query = Query,
                SourceModeIndex = SourceModeIndex,
                MaxStoredRows = null,
                SyncUi = true,
                RequireUserConfirm = false
            }).ConfigureAwait(true);

            if (!result.Success)
            {
                StatusText = result.Error ?? "查询失败";
                return;
            }

            if (result.Provider == null)
            {
                StatusText = "无结果";
                _queryTaskId = string.Empty;
                ResultsTable.Subtitle = string.Empty;
                await ResultsTable.InitializePagedAsync(
                    new EmptyPagedDataProvider(AssetReconPagedDataProvider.ColumnHeaders));
                return;
            }

            _dbPath = _workspaceService.GetDatabasePath() ?? string.Empty;
            _queryTaskId = result.QueryTaskId;
            OpenResultsBottomTab();
            ResultsTable.UserPageSize = result.Provider.PageSize;
            ResultsTable.Subtitle = string.Empty;
            await ResultsTable.InitializePagedAsync(result.Provider);

            StatusText = "查询完成";

            if (_leftPanelVm != null)
                await _leftPanelVm.LoadTasksCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            StatusText = $"查询异常: {ex.Message}";
        }
        finally
        {
            IsQuerying = false;
            ScheduleRefreshAllQuotaColumns();
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _eventBus.Publish(new SettingsRequestedEvent("资产侦察"));
    }

    public void SetLeftPanelViewModel(AssetReconLeftPanelViewModel vm)
    {
        _leftPanelVm = vm;
    }

    /// <summary>首次打开资产侦察页时再拉取各源积分，避免启动即打外网。</summary>
    public void EnsureQuotaColumnsLoaded()
    {
        if (Interlocked.CompareExchange(ref _quotaColumnsLoadScheduled, 1, 0) != 0)
            return;

        ScheduleRefreshAllQuotaColumns();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ResultsTable.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>三列各自发起请求，完成后独立更新对应文案（不互相等待）。</summary>
    private void ScheduleRefreshAllQuotaColumns()
    {
        var c = _settingsService.Load().AssetRecon;
        var mode = SourceModeIndex;
        _ = RefreshQuotaColumnFofaAsync(c, mode);
        _ = RefreshQuotaColumnHunterAsync(c, mode);
        _ = RefreshQuotaColumnQuakeAsync(c, mode);
    }

    private async Task RefreshQuotaColumnFofaAsync(AssetReconSettings c, int mode)
    {
        var include = mode is 0 or 1;
        try
        {
            if (!include)
            {
                await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnFofa = "F —");
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnFofa = "F …");
            var text = await _quotaClient.BuildFofaCompactAsync(c, true).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnFofa = text);
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnFofa = "F -1");
        }
    }

    private async Task RefreshQuotaColumnHunterAsync(AssetReconSettings c, int mode)
    {
        var include = mode is 0 or 2;
        try
        {
            if (!include)
            {
                await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnHunter = "H —");
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnHunter = "H …");
            var text = await _quotaClient.BuildHunterCompactAsync(c, true).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnHunter = text);
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnHunter = "H —");
        }
    }

    private async Task RefreshQuotaColumnQuakeAsync(AssetReconSettings c, int mode)
    {
        var include = mode is 0 or 3;
        try
        {
            if (!include)
            {
                await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnQuake = "Q —");
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnQuake = "Q …");
            var text = await _quotaClient.BuildQuakeCompactAsync(c, true).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnQuake = text);
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => QuotaColumnQuake = "Q —");
        }
    }

    /// <summary>清空结果占位用（无列时 InitializePaged 会建空表头）。</summary>
    private sealed class EmptyPagedDataProvider : IDataProvider
    {
        private readonly string[] _cols;
        private bool _disposed;

        public EmptyPagedDataProvider(string[] cols) => _cols = cols;

        public string Title => "";
        public string DatabasePath => "";
        public string QuerySource => "";
        public IReadOnlyList<string> Columns => _cols;
        public bool SupportsTotalCount => true;
        public bool CanEdit => false;
        public bool CanWriteBack => false;
        public bool IsDirty => false;

        public Task<DataPageResult> GetPageAsync(int offset, int limit) =>
            Task.FromResult(new DataPageResult { Rows = Array.Empty<IReadOnlyList<object?>>(), Offset = offset, Count = 0 });

        public Task<long> GetTotalCountAsync() => Task.FromResult(0L);

        public Task<int> UpdateRowAsync(long rowId, IReadOnlyList<object?> values) => Task.FromResult(0);

        public Task<int> InsertRowAsync(IReadOnlyList<object?> values) => Task.FromResult(0);

        public Task<int> DeleteRowAsync(long rowId) => Task.FromResult(0);

        public Task WriteBackAsync() => Task.CompletedTask;

        public Task RefreshFromSourceAsync() => Task.CompletedTask;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
