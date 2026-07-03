using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using ZeroFall.Browser.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Browser.Services;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Services;
using ZeroFall.Traffic.Capture;
using ZeroFall.Traffic.Ingest;

namespace ZeroFall.Browser.ViewModels;

public partial class TrafficMonitorTabViewModel : BrowserTabViewModelBase, IDisposable
{
    private const int MaxEntries = 3000;
    private const int MaxTrafficEventsPerBatch = 25;
    private readonly IEventBus _eventBus;
    private readonly TrafficArchiveService _trafficArchive;
    private readonly ITargetScopeService _targetScope;
    private readonly TrafficIngestGateway _ingestGateway;
    private readonly IDisposable _trafficSubscription;
    private readonly IDisposable _trafficBodySubscription;
    private readonly IDisposable _selectEntrySubscription;
    private readonly IDisposable _activeContentTabSubscription;
    private readonly IDisposable _projectOpenedSubscription;
    private readonly Dictionary<string, TrafficLogEntryViewModel> _entriesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WebTrafficBodyUpdatedEvent> _latestBodyByEntryId = new(StringComparer.Ordinal);
    private readonly Queue<TrafficLogEntryViewModel> _pendingTrafficEntries = new();
    private readonly object _pendingTrafficGate = new();
    private readonly object _bodyUpdateGate = new();
    private bool _trafficFlushScheduled;
    private int _selectEntryGeneration;
    private int _bodyHydrateGeneration;

    public DataTableViewModel Table { get; } = new();

    [ObservableProperty]
    private TrafficLogEntryViewModel? _selectedEntry;

    [ObservableProperty]
    private TrafficFilterSpec _appliedFilter = TrafficFilterSpec.Default;

    [ObservableProperty]
    private string _filterStatusText = "无筛选";

    /// <summary>为 true 时仅保留「Content 区最后选中的浏览器标签页」流量，按稳定 <see cref="TrafficLogEntryViewModel.BrowserTabId"/> 匹配（与 Dock 标签 Id 一致），不依赖页面标题。</summary>
    [ObservableProperty]
    private bool _onlyLastActiveBrowserTabTraffic;

    /// <summary>最后一次在 Content 区选中的浏览器标签页 Id（以 <c>browser</c> 或 <c>browser-*</c> 开头）。</summary>
    [ObservableProperty]
    private string _lastFocusedBrowserTabId = string.Empty;

    /// <summary>由 <see cref="TrafficMonitorView"/> 注入，在 View 层 ShowDialog。</summary>
    public Func<Task>? ShowTrafficFilterDialogAsync { get; set; }

    /// <summary>由 <see cref="TrafficMonitorView"/> 注入，更新单行高亮背景。</summary>
    public Action<TrafficLogEntryViewModel>? RefreshEntryRowVisual { get; set; }

    public TrafficMonitorTabViewModel(
        IEventBus eventBus,
        TrafficArchiveService trafficArchive,
        ITargetScopeService targetScope,
        TrafficIngestGateway ingestGateway)
    {
        _eventBus = eventBus;
        _trafficArchive = trafficArchive;
        _targetScope = targetScope;
        _ingestGateway = ingestGateway;
        _trafficSubscription = eventBus.SubscribeDisposable<TrafficEntryIngestedEvent>(OnTrafficIngested);
        _trafficBodySubscription = eventBus.SubscribeDisposable<WebTrafficBodyUpdatedEvent>(OnWebTrafficBodyUpdated);
        _selectEntrySubscription = eventBus.SubscribeDisposable<SelectTrafficEntryRequestedEvent>(OnSelectTrafficEntryRequested);
        _activeContentTabSubscription = eventBus.SubscribeDisposable<ActiveContentTabChangedEvent>(OnActiveContentTabChanged);
        _projectOpenedSubscription = eventBus.SubscribeDisposable<ProjectOpenedEvent>(OnProjectOpened);
        Table.InitializeLive(
            new[] { "时间", "方法", "状态", "URL", "备注" },
            MaxEntries,
            showHeaderPanel: false,
            showLineNumberColumn: false);
        Table.DisableUrlColumns = true;
        Table.PropertyChanged += OnTablePropertyChanged;
        TryScheduleArchiveRestoreIfProjectAlreadyOpen();
    }

    private void TryScheduleArchiveRestoreIfProjectAlreadyOpen()
    {
        if (!_trafficArchive.HasDatabase)
            return;

        ScheduleArchiveRestore();
    }

    private void OnProjectOpened(ProjectOpenedEvent e) => ScheduleArchiveRestore();

    private void ScheduleArchiveRestore()
    {
        StartupPerformance.RunOnUiIdle(async () =>
        {
            try
            {
                await _trafficArchive.WaitForReadyAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RebuildVisibleRowsFromArchiveAsync().ConfigureAwait(false);
        });
    }

    private void OnTablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DataTableViewModel.SelectedRow)) return;
        SelectedEntry = Table.SelectedRow?.Tag as TrafficLogEntryViewModel;
    }

    private void OnTrafficIngested(TrafficEntryIngestedEvent e)
    {
        if (e.Decision == TrafficCaptureDedup.Decision.SupersedeProxy
            && !string.IsNullOrWhiteSpace(e.SupersededEntryId))
        {
            _ = RemoveSupersededEntryAsync(e.SupersededEntryId);
        }

        EnqueueTrafficEntry(e.Entry);
    }

    private static DataRowViewModel ToRow(TrafficLogEntryViewModel entry)
    {
        var row = new DataRowViewModel { Tag = entry };
        row.Values.Add(entry.Time);
        row.Values.Add(entry.Method);
        row.Values.Add(entry.Status);
        row.Values.Add(entry.Url);
        row.Values.Add(FormatRemarkCell(entry.Remark));
        return row;
    }

    private static string FormatRemarkCell(string remark)
    {
        if (string.IsNullOrWhiteSpace(remark))
            return string.Empty;

        var trimmed = remark.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80] + "…";
    }

    private static void SyncRowValues(DataRowViewModel row, TrafficLogEntryViewModel entry)
    {
        if (row.Values.Count >= 5)
        {
            row.Values[0] = entry.Time;
            row.Values[1] = entry.Method;
            row.Values[2] = entry.Status;
            row.Values[3] = entry.Url;
            row.Values[4] = FormatRemarkCell(entry.Remark);
            return;
        }

        row.Values.Clear();
        row.Values.Add(entry.Time);
        row.Values.Add(entry.Method);
        row.Values.Add(entry.Status);
        row.Values.Add(entry.Url);
        row.Values.Add(FormatRemarkCell(entry.Remark));
    }

    private void OnActiveContentTabChanged(ActiveContentTabChangedEvent e)
    {
        if (string.IsNullOrEmpty(e.TabId))
            return;
        if (!e.TabId.StartsWith("browser", StringComparison.Ordinal))
            return;
        LastFocusedBrowserTabId = e.TabId;
    }

    partial void OnOnlyLastActiveBrowserTabTrafficChanged(bool value)
    {
        _ = RebuildVisibleRowsFromArchiveAsync();
        RefreshFilterStatus();
    }

    partial void OnLastFocusedBrowserTabIdChanged(string value)
    {
        if (!OnlyLastActiveBrowserTabTraffic)
            return;
        _ = RebuildVisibleRowsFromArchiveAsync();
        RefreshFilterStatus();
    }

    private void PrependEntryRow(TrafficLogEntryViewModel entry)
    {
        Table.PrependLiveRow(ToRow(entry));
        TrimEntryCacheToVisibleRows();
    }

    /// <summary>表行 eviction 后同步释放内存缓存；否则 _entriesById 会无限持有完整 HTTP body。</summary>
    private void TrimEntryCacheToVisibleRows()
    {
        var visibleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Table.Rows)
        {
            if (row.Tag is TrafficLogEntryViewModel vm)
                visibleIds.Add(vm.EntryId);
        }

        if (SelectedEntry is { EntryId: { Length: > 0 } selectedId })
            visibleIds.Add(selectedId);

        foreach (var id in _entriesById.Keys.ToList())
        {
            if (visibleIds.Contains(id))
                continue;

            ReleaseEntryCache(id);
        }
    }

    private void ReleaseEntryCache(string entryId)
    {
        if (_entriesById.Remove(entryId, out var entry))
            ReleaseEntryPayload(entry);

        lock (_bodyUpdateGate)
            _latestBodyByEntryId.Remove(entryId);
    }

    private static void ReleaseEntryPayload(TrafficLogEntryViewModel entry)
    {
        entry.RequestBody = string.Empty;
        entry.ResponseBody = string.Empty;
        entry.RequestBodyRaw = null;
        entry.ResponseBodyRaw = null;
    }

    private async Task RemoveSupersededEntryAsync(string entryId)
    {
        try
        {
            await _trafficArchive.DeleteAsync(entryId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrafficMonitor] Archive delete failed: {ex.Message}");
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_entriesById.Remove(entryId, out var removed))
                return;

            ReleaseEntryPayload(removed);

            var row = Table.Rows.FirstOrDefault(x => (x.Tag as TrafficLogEntryViewModel)?.EntryId == entryId);
            if (row is not null)
                Table.Rows.Remove(row);

            if (string.Equals(SelectedEntry?.EntryId, entryId, StringComparison.Ordinal))
                SelectedEntry = null;
        });
    }

    private void OnWebTrafficBodyUpdated(WebTrafficBodyUpdatedEvent e)
    {
        lock (_bodyUpdateGate)
            _latestBodyByEntryId[e.EntryId] = e;

        ApplyBodyUpdateToPendingEntries(e);
        _ = Task.Run(() => UpdateBodyAsync(e));
    }

    private void OnSelectTrafficEntryRequested(SelectTrafficEntryRequestedEvent e) =>
        _ = SelectEntryByIdAsync(e.EntryId);

    public void SelectEntryById(string entryId) => _ = SelectEntryByIdAsync(entryId);

    /// <summary>按 entryId 解析流量（表行 → 内存缓存 → 归档），供网站树/联动选中。</summary>
    public async Task<bool> SelectEntryByIdAsync(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
            return false;

        var generation = Interlocked.Increment(ref _selectEntryGeneration);

        var resolvedOnUi = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var row = Table.Rows.FirstOrDefault(x => (x.Tag as TrafficLogEntryViewModel)?.EntryId == entryId);
            if (row is not null)
            {
                Table.SelectedRow = row;
                return Table.SelectedRow?.Tag as TrafficLogEntryViewModel;
            }

            if (_entriesById.TryGetValue(entryId, out var cached))
            {
                Table.SelectedRow = null;
                SelectedEntry = cached;
                return cached;
            }

            return null;
        });

        if (resolvedOnUi is not null)
        {
            var hydrateGeneration = Interlocked.Increment(ref _bodyHydrateGeneration);
            await HydrateEntryBodiesIfNeededAsync(resolvedOnUi, hydrateGeneration).ConfigureAwait(false);
            return generation == Volatile.Read(ref _selectEntryGeneration);
        }

        var archived = await _trafficArchive.FindByIdAsync(entryId).ConfigureAwait(false);
        if (archived is null)
            return false;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != Volatile.Read(ref _selectEntryGeneration))
                return;

            Table.SelectedRow = null;
            SelectedEntry = archived;
        });

        return generation == Volatile.Read(ref _selectEntryGeneration);
    }

    [RelayCommand]
    private async Task ApplyFilter()
    {
        await ApplyFilterAsync();
    }

    public async Task ApplyFilterAsync()
    {
        await RebuildVisibleRowsFromArchiveAsync();
        RefreshFilterStatus();
    }

    [RelayCommand]
    private async Task ClearFilters()
    {
        AppliedFilter = TrafficFilterSpec.Default;
        await RebuildVisibleRowsFromArchiveAsync();
        RefreshFilterStatus();
    }

    [RelayCommand]
    private async Task Clear()
    {
        lock (_pendingTrafficGate)
        {
            _pendingTrafficEntries.Clear();
            _trafficFlushScheduled = false;
        }

        await _trafficArchive.ClearAsync();
        _ingestGateway.Reset();
        Table.ClearLiveRows();
        _entriesById.Clear();
        SelectedEntry = null;
        _eventBus.Publish(new TrafficRecordsClearedEvent());
    }

    [RelayCommand]
    private void OpenSelectedTrafficUrl()
    {
        if (SelectedEntry is null)
            return;
        if (!Uri.TryCreate(SelectedEntry.Url, UriKind.Absolute, out var uri))
            return;
        _eventBus.Publish(new OpenBrowserTabRequestedEvent(uri.ToString()));
    }

    [RelayCommand]
    private void OpenPortScanForSelectedTrafficHost()
    {
        if (SelectedEntry is null)
            return;
        if (!TryGetTrafficRequestHost(SelectedEntry.Url, out var host))
            return;
        _eventBus.Publish(new PortScanTargetHostRequestedEvent(host));
    }

    /// <summary>供右键菜单等判断：是否为可解析的 http(s) URL 且含主机名。</summary>
    public static bool TryGetTrafficRequestHost(string url, out string host)
    {
        host = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("http" or "https"))
            return false;
        host = uri.Host;
        return !string.IsNullOrWhiteSpace(host);
    }

    /// <summary>供右键「复制单元格」：列索引与 <see cref="DataTableViewModel.Columns"/> / 行 <c>Values</c> 一致。</summary>
    public string? GetSelectedTrafficCellText(int valueColumnIndex)
    {
        if (Table.SelectedRow is null)
            return null;
        if (valueColumnIndex < 0 || valueColumnIndex >= Table.SelectedRow.Values.Count)
            return null;
        return Table.SelectedRow.Values[valueColumnIndex]?.ToString();
    }

    public TrafficLogEntryViewModel? FindEntryById(string entryId) =>
        _entriesById.TryGetValue(entryId, out var entry) ? entry : null;

    public string? GetEntryUrl(string entryId) =>
        FindEntryById(entryId)?.Url;

    public void SendEntryToReplay(TrafficLogEntryViewModel entry)
    {
        _eventBus.Publish(new SwitchDockTabRequestedEvent(Platform.Registries.DockPosition.Content, "http-replay"));
        _eventBus.Publish(new HttpReplayRequestedEvent(
            entry.EntryId,
            entry.Method,
            entry.Url,
            entry.RequestHeaders,
            entry.RequestBody,
            entry.ResponseHeaders,
            entry.ResponseBody));
    }

    public void SendEntryToIntruder(TrafficLogEntryViewModel entry)
    {
        _eventBus.Publish(new HttpIntruderRequestedEvent(
            entry.EntryId,
            entry.Method,
            entry.Url,
            entry.RequestHeaders,
            entry.RequestBody,
            entry.ResponseHeaders,
            entry.ResponseBody));
    }

    public void SendEntryToDecode(TrafficLogEntryViewModel entry, HttpTrafficTextPart part)
    {
        var text = HttpTrafficTextHelper.GetText(entry, part);
        var label = HttpTrafficTextHelper.BuildLabel(entry, part);
        _eventBus.Publish(new HttpDecodeRequestedEvent(label, text));
    }

    public void SendEntryRequestVsResponseDiff(TrafficLogEntryViewModel entry)
    {
        _eventBus.Publish(new HttpDiffRequestedEvent(
            HttpTrafficTextHelper.BuildLabel(entry, HttpTrafficTextPart.Request),
            HttpTrafficTextHelper.GetText(entry, HttpTrafficTextPart.Request),
            HttpTrafficTextHelper.BuildLabel(entry, HttpTrafficTextPart.Response),
            HttpTrafficTextHelper.GetText(entry, HttpTrafficTextPart.Response)));
    }

    public void SendEntryVsPreviousResponseDiff(TrafficLogEntryViewModel entry)
    {
        var previous = GetPreviousEntry(entry);
        if (previous is null)
        {
            _eventBus.Publish(new StatusMessageEvent("无上一条流量记录可对比"));
            return;
        }

        _eventBus.Publish(new HttpDiffRequestedEvent(
            HttpTrafficTextHelper.BuildLabel(previous, HttpTrafficTextPart.Response),
            HttpTrafficTextHelper.GetText(previous, HttpTrafficTextPart.Response),
            HttpTrafficTextHelper.BuildLabel(entry, HttpTrafficTextPart.Response),
            HttpTrafficTextHelper.GetText(entry, HttpTrafficTextPart.Response)));
    }

    public TrafficLogEntryViewModel? GetPreviousEntry(TrafficLogEntryViewModel current)
    {
        DataRowViewModel? previous = null;
        foreach (var row in Table.Rows)
        {
            if (ReferenceEquals(row.Tag, current))
                return previous?.Tag as TrafficLogEntryViewModel;

            previous = row;
        }

        return null;
    }

    [RelayCommand]
    private void SendSelectedToReplay()
    {
        if (SelectedEntry is null)
            return;

        SendEntryToReplay(SelectedEntry);
    }

    [RelayCommand]
    private void SendSelectedToIntruder()
    {
        if (SelectedEntry is null)
            return;

        SendEntryToIntruder(SelectedEntry);
    }

    public void SendEntryToReplayById(string entryId)
    {
        var entry = FindEntryById(entryId);
        if (entry is not null)
            SendEntryToReplay(entry);
    }

    public async Task SetSelectedHighlightAsync(TrafficHighlightColor color)
    {
        if (SelectedEntry is null)
            return;

        await UpdateEntryAnnotationAsync(SelectedEntry, color, SelectedEntry.Remark);
    }

    public async Task SetSelectedRemarkAsync(string remark)
    {
        if (SelectedEntry is null)
            return;

        await UpdateEntryAnnotationAsync(SelectedEntry, SelectedEntry.Color, remark);
    }

    private async Task UpdateEntryAnnotationAsync(
        TrafficLogEntryViewModel entry,
        TrafficHighlightColor color,
        string remark)
    {
        var normalizedRemark = remark ?? string.Empty;
        try
        {
            await _trafficArchive.UpdateAnnotationAsync(entry.EntryId, color, normalizedRemark)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrafficMonitor] Archive annotation update failed: {ex.Message}");
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            entry.Color = color;
            entry.Remark = normalizedRemark;

            var row = Table.Rows.FirstOrDefault(x => ReferenceEquals(x.Tag, entry));
            if (row is null)
                return;

            if (!MatchesFilter(entry))
            {
                Table.Rows.Remove(row);
                if (ReferenceEquals(SelectedEntry, entry))
                    SelectedEntry = null;
                return;
            }

            SyncRowValues(row, entry);
            RefreshEntryRowVisual?.Invoke(entry);
        });
    }

    [RelayCommand]
    private async Task OpenTrafficFilterDialog()
    {
        if (ShowTrafficFilterDialogAsync is null)
            return;
        await ShowTrafficFilterDialogAsync();
    }

    private void EnqueueTrafficEntry(TrafficLogEntryViewModel entry)
    {
        lock (_pendingTrafficGate)
        {
            _pendingTrafficEntries.Enqueue(entry);
            if (_trafficFlushScheduled)
                return;

            _trafficFlushScheduled = true;
        }

        _ = Task.Run(ProcessPendingTrafficBatchesAsync);
    }

    private async Task ProcessPendingTrafficBatchesAsync()
    {
        while (true)
        {
            var batch = DequeueTrafficBatch();
            if (batch.Count == 0)
                return;

            var visibleEntries = new List<TrafficLogEntryViewModel>(batch.Count);
            foreach (var entry in batch)
            {
                MergeLatestBodyIntoEntry(entry);

                try
                {
                    await RetryPersistLatestBodyAsync(entry.EntryId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[TrafficMonitor] Archive body retry failed for {entry.EntryId}: {ex.Message}");
                }

                if (MatchesFilter(entry))
                    visibleEntries.Add(entry);
            }

            if (visibleEntries.Count == 0)
                continue;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var entry in visibleEntries)
                {
                    if (_entriesById.ContainsKey(entry.EntryId))
                        continue;

                    MergeLatestBodyIntoEntry(entry);
                    _entriesById[entry.EntryId] = entry;
                    PrependEntryRow(entry);
                }

                TrimEntryCacheToVisibleRows();
            });
        }
    }

    private List<TrafficLogEntryViewModel> DequeueTrafficBatch()
    {
        lock (_pendingTrafficGate)
        {
            if (_pendingTrafficEntries.Count == 0)
            {
                _trafficFlushScheduled = false;
                return [];
            }

            var batch = new List<TrafficLogEntryViewModel>(Math.Min(_pendingTrafficEntries.Count, MaxTrafficEventsPerBatch));
            while (_pendingTrafficEntries.Count > 0 && batch.Count < MaxTrafficEventsPerBatch)
                batch.Add(_pendingTrafficEntries.Dequeue());
            return batch;
        }
    }

    private async Task UpdateBodyAsync(WebTrafficBodyUpdatedEvent e)
    {
        if (IsEmptyBodyUpdate(e))
            return;

        bool? fingerprintEligible = null;
        int? responseBodyLength = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_entriesById.TryGetValue(e.EntryId, out var entry))
            {
                ApplyBodyUpdate(entry, e);
                TrafficEntryMetadataComputer.Apply(entry);
                fingerprintEligible = entry.FingerprintEligible;
                responseBodyLength = entry.ResponseBodyLength;
            }
        });

        try
        {
            await _trafficArchive.UpdateBodyAsync(
                e.EntryId,
                e.RequestBody,
                e.ResponseBody,
                e.RequestBodyRaw,
                e.ResponseBodyRaw,
                fingerprintEligible,
                responseBodyLength).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrafficMonitor] Archive body update failed: {ex.Message}");
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_entriesById.TryGetValue(e.EntryId, out var entry))
                ApplyBodyUpdate(entry, e);
        });
    }

    private void MergeLatestBodyIntoEntry(TrafficLogEntryViewModel entry)
    {
        lock (_bodyUpdateGate)
        {
            if (_latestBodyByEntryId.TryGetValue(entry.EntryId, out var body))
                ApplyBodyUpdate(entry, body);
        }
    }

    private async Task RetryPersistLatestBodyAsync(string entryId)
    {
        WebTrafficBodyUpdatedEvent? body;
        bool? fingerprintEligible = null;
        int? responseBodyLength = null;
        lock (_bodyUpdateGate)
        {
            if (!_latestBodyByEntryId.TryGetValue(entryId, out body))
                return;
        }
        if (IsEmptyBodyUpdate(body))
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_entriesById.TryGetValue(entryId, out var entry))
            {
                ApplyBodyUpdate(entry, body);
                TrafficEntryMetadataComputer.Apply(entry);
                fingerprintEligible = entry.FingerprintEligible;
                responseBodyLength = entry.ResponseBodyLength;
            }
        });

        try
        {
            await _trafficArchive.UpdateBodyAsync(
                body.EntryId,
                body.RequestBody,
                body.ResponseBody,
                body.RequestBodyRaw,
                body.ResponseBodyRaw,
                fingerprintEligible,
                responseBodyLength).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrafficMonitor] Archive body retry failed: {ex.Message}");
        }
    }

    private void ApplyBodyUpdateToPendingEntries(WebTrafficBodyUpdatedEvent e)
    {
        lock (_pendingTrafficGate)
        {
            foreach (var pending in _pendingTrafficEntries)
            {
                if (pending.EntryId == e.EntryId)
                    ApplyBodyUpdate(pending, e);
            }
        }
    }

    private static void ApplyBodyUpdate(TrafficLogEntryViewModel entry, WebTrafficBodyUpdatedEvent e)
    {
        if (!string.IsNullOrEmpty(e.RequestBody) || e.RequestBodyRaw is not null)
            entry.RequestBody = e.RequestBody;
        if (!string.IsNullOrEmpty(e.ResponseBody) || e.ResponseBodyRaw is not null)
            entry.ResponseBody = e.ResponseBody;
        if (e.RequestBodyRaw is not null)
            entry.RequestBodyRaw = e.RequestBodyRaw;
        if (e.ResponseBodyRaw is not null)
            entry.ResponseBodyRaw = e.ResponseBodyRaw;
    }

    private static bool IsEmptyBodyUpdate(WebTrafficBodyUpdatedEvent e) =>
        string.IsNullOrEmpty(e.RequestBody)
        && string.IsNullOrEmpty(e.ResponseBody)
        && e.RequestBodyRaw is null
        && e.ResponseBodyRaw is null;

    private static bool NeedsBodyHydration(TrafficLogEntryViewModel entry)
    {
        var hasResponsePayload = !string.IsNullOrEmpty(entry.ResponseBody) || entry.ResponseBodyRaw is { Length: > 0 };
        if (!hasResponsePayload && (entry.ResponseBodyLength > 0 || ResponseHeadersSuggestBody(entry)))
            return true;

        var hasRequestPayload = !string.IsNullOrEmpty(entry.RequestBody) || entry.RequestBodyRaw is { Length: > 0 };
        return !hasRequestPayload && RequestHeadersSuggestBody(entry);
    }

    private static bool ResponseHeadersSuggestBody(TrafficLogEntryViewModel entry)
    {
        if (entry.Status is "204" or "304")
            return false;

        var contentLength = HttpRequestComposer.TryGetHeader(entry.ResponseHeaders, "Content-Length");
        if (long.TryParse(contentLength, out var length) && length > 0)
            return true;

        return entry.ResponseHeaders.Contains("Content-Type:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequestHeadersSuggestBody(TrafficLogEntryViewModel entry)
    {
        if (entry.Method is not ("POST" or "PUT" or "PATCH" or "DELETE"))
            return false;

        var contentLength = HttpRequestComposer.TryGetHeader(entry.RequestHeaders, "Content-Length");
        if (long.TryParse(contentLength, out var length) && length > 0)
            return true;

        return entry.RequestHeaders.Contains("Content-Type:", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HydrateEntryBodiesIfNeededAsync(TrafficLogEntryViewModel entry, int generation)
    {
        if (!NeedsBodyHydration(entry))
            return;

        var entryId = entry.EntryId;
        TrafficLogEntryViewModel? full;
        try
        {
            full = await _trafficArchive.FindByIdAsync(entryId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrafficMonitor] Body hydrate failed for {entryId}: {ex.Message}");
            return;
        }

        if (full is null)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != Volatile.Read(ref _bodyHydrateGeneration))
                return;

            if (!_entriesById.TryGetValue(entryId, out var target))
                return;

            if (!NeedsBodyHydration(target))
                return;

            ApplyBodyPayloadFrom(target, full);
            MergeLatestBodyIntoEntry(target);
        });
    }

    private static void ApplyBodyPayloadFrom(TrafficLogEntryViewModel target, TrafficLogEntryViewModel source)
    {
        if (!string.IsNullOrEmpty(source.RequestBody) || source.RequestBodyRaw is not null)
        {
            target.RequestBody = source.RequestBody;
            target.RequestBodyRaw = source.RequestBodyRaw;
        }

        if (!string.IsNullOrEmpty(source.ResponseBody) || source.ResponseBodyRaw is not null)
        {
            target.ResponseBody = source.ResponseBody;
            target.ResponseBodyRaw = source.ResponseBodyRaw;
        }
    }

    private async Task RebuildVisibleRowsFromArchiveAsync()
    {
        var selectedEntryId = SelectedEntry?.EntryId;
        var raw = await _trafficArchive.QueryAsync(
            BuildEffectiveFilter(),
            LastFocusedBrowserTabId,
            OnlyLastActiveBrowserTabTraffic,
            MaxEntries,
            TrafficArchiveProjection.ListMeta);

        var pipeline = new TrafficFilterPipeline(BuildEffectiveFilter(), new TrafficFilterSpec());
        var visible = pipeline.Apply(raw).Take(MaxEntries).ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _entriesById.Clear();
            Table.ClearLiveRows();
            for (var i = visible.Count - 1; i >= 0; i--)
            {
                var entry = visible[i];
                _entriesById[entry.EntryId] = entry;
                Table.PrependLiveRow(ToRow(entry));
            }

            if (string.IsNullOrWhiteSpace(selectedEntryId))
                return;
            var row = Table.Rows.FirstOrDefault(x => (x.Tag as TrafficLogEntryViewModel)?.EntryId == selectedEntryId);
            if (row is not null)
                Table.SelectedRow = row;
        });
    }


    private bool MatchesFilter(TrafficLogEntryViewModel row)
    {
        if (OnlyLastActiveBrowserTabTraffic && !string.IsNullOrEmpty(LastFocusedBrowserTabId))
        {
            var isProxy = string.Equals(row.BrowserTabId, ProxyTrafficSource.BrowserTabId, StringComparison.Ordinal);
            if (!isProxy && !string.Equals(row.BrowserTabId, LastFocusedBrowserTabId, StringComparison.Ordinal))
                return false;
        }

        var pipeline = new TrafficFilterPipeline(BuildEffectiveFilter(), new TrafficFilterSpec());
        return pipeline.Apply(new[] { row }).Any();
    }

    private TrafficFilterSpec BuildEffectiveFilter()
    {
        if (!_targetScope.HasEntries)
            return AppliedFilter;

        return AppliedFilter with
        {
            ScopeHosts = _targetScope.Hosts.ToList()
        };
    }

    private void RefreshFilterStatus()
    {
        if (AppliedFilter.IsEquivalentToDefault() && !OnlyLastActiveBrowserTabTraffic)
        {
            FilterStatusText = "无筛选";
            return;
        }

        var parts = new List<string>();
        var f = AppliedFilter;
        if (f.ShowOnlyInScope)
            parts.Add("仅范围内");
        if (f.HideWithoutResponse)
            parts.Add("隐藏无响应");
        if (f.ShowOnlyParameterized)
            parts.Add("仅带参数");
        if (!f.MimeImages || !f.MimeCss)
            parts.Add("MIME 筛选");
        if (!f.Status2xx || !f.Status3xx || !f.Status4xx || !f.Status5xx)
            parts.Add("状态码筛选");
        if (!string.IsNullOrWhiteSpace(f.SearchTerm))
            parts.Add($"搜索~{f.SearchTerm}");
        if (f.ExtensionShowOnlyEnabled)
            parts.Add($"仅扩展名={f.ExtensionShowOnly}");
        if (f.ExtensionHideEnabled)
            parts.Add($"隐藏扩展名={f.ExtensionHide}");
        if (!string.IsNullOrWhiteSpace(f.ListenerPort))
            parts.Add($"端口={f.ListenerPort}");
        if (f.ShowOnlyWithNotes)
            parts.Add("仅有备注");
        if (f.ShowOnlyHighlighted)
            parts.Add("仅高亮");

        if (OnlyLastActiveBrowserTabTraffic)
        {
            parts.Add(string.IsNullOrEmpty(LastFocusedBrowserTabId)
                ? "仅当前浏览器标签(未选定)"
                : $"仅浏览器Tab={LastFocusedBrowserTabId}");
        }

        FilterStatusText = parts.Count == 0 ? "无筛选" : string.Join(" | ", parts);
    }

    partial void OnSelectedEntryChanged(TrafficLogEntryViewModel? value)
    {
        if (value is not null)
        {
            var generation = Interlocked.Increment(ref _bodyHydrateGeneration);
            _ = HydrateEntryBodiesIfNeededAsync(value, generation);
        }

        if (value is null)
        {
            _eventBus.Publish(new UiSelectionChangedEvent("none", "未选中记录", "{}"));
            return;
        }

        var dto = new TrafficSelectionPayloadDto
        {
            EntryId = value.EntryId,
            Time = value.Time,
            Tab = value.Tab,
            BrowserTabId = value.BrowserTabId,
            PageSessionId = value.PageSessionId.ToString(),
            TopLevelUrl = value.TopLevelUrl,
            Method = value.Method,
            Url = value.Url,
            Status = value.Status,
            Remark = value.Remark,
            Color = TrafficLogEntryViewModel.ToStorageValue(value.Color),
            RequestHeaders = value.RequestHeaders,
            RequestBody = value.RequestBody,
            RequestBodyRaw = value.RequestBodyRaw,
            ResponseHeaders = value.ResponseHeaders,
            ResponseBody = value.ResponseBody,
            ResponseBodyRaw = value.ResponseBodyRaw
        };
        var payload = JsonSerializer.Serialize(dto, TrafficJsonContext.Default.TrafficSelectionPayloadDto);
        _eventBus.Publish(new UiSelectionChangedEvent(
            "http_traffic",
            $"{value.Method} {value.Url} ({value.Status})",
            payload));
    }

    private static byte[]? CopyBytes(byte[]? value) =>
        value is null or { Length: 0 } ? null : value.ToArray();

    public void Dispose()
    {
        lock (_pendingTrafficGate)
        {
            _pendingTrafficEntries.Clear();
            _trafficFlushScheduled = false;
        }

        Table.PropertyChanged -= OnTablePropertyChanged;
        _trafficSubscription.Dispose();
        _trafficBodySubscription.Dispose();
        _selectEntrySubscription.Dispose();
        _activeContentTabSubscription.Dispose();
        _projectOpenedSubscription.Dispose();
        Table.Dispose();
    }
}
