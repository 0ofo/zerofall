using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZeroFall.Browser.Services;
using ZeroFall.Browser.ViewModels;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Views;
using ZeroFall.Dock.Services;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Browser.Views;

public partial class TrafficMonitorView : UserControl, ITableGridHost, IDockTabToolPanelProvider
{
    private const string TrafficGridContextMenuKey = "TrafficGridContextMenu";

    private DataGrid? _trafficDataGrid;
    private EventHandler<DataGridCellPointerPressedEventArgs>? _cellPointerPressedHandler;
    private EventHandler<SelectionChangedEventArgs>? _selectionChangedHandler;
    private EventHandler<DataGridRowEventArgs>? _loadingRowHandler;
    private bool _highlightMenuBuilt;
    private bool _handlingGridSelection;
    private int _trafficContextValueColumnIndex = -1;
    private TrafficHttpDetailFlyoutHost? _httpDetailFlyoutHost;
    private TrafficMonitorTabViewModel? _httpDetailVm;
    private StackPanel? _dockToolPanel;
    /// <summary>下一次因选中行触发的同步若来自右键校正选中，则不打开 HTTP 详情 Flyout。</summary>
    private bool _suppressHttpDetailFlyoutOnceForPointer;

    public TrafficMonitorView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public Control? GetDockTabToolPanel()
    {
        _dockToolPanel ??= CreateDockToolPanel();
        _dockToolPanel.DataContext = DataContext;
        return _dockToolPanel;
    }

    private StackPanel CreateDockToolPanel()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        var filterButton = new Button
        {
            Classes = { "Small" },
            Padding = new Thickness(2),
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new PathIcon
            {
                Data = ResolveGeometry("SemiIconFilter"),
                Width = 14,
                Height = 14
            }
        };
        filterButton.Bind(Button.CommandProperty, new Binding(nameof(TrafficMonitorTabViewModel.OpenTrafficFilterDialogCommand)));
        ToolTip.SetTip(filterButton, "流量筛选");

        var currentTabCheckBox = new CheckBox
        {
            Content = "仅当前站",
            Classes = { "Small" },
            VerticalAlignment = VerticalAlignment.Center
        };
        currentTabCheckBox.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(TrafficMonitorTabViewModel.OnlyLastActiveBrowserTabTraffic))
        {
            Mode = BindingMode.TwoWay
        });
        ToolTip.SetTip(currentTabCheckBox, "按 Content 区最后选中的浏览器标签页过滤；请先选中浏览器标签再开启");

        panel.Children.Add(filterButton);
        panel.Children.Add(currentTabCheckBox);
        return panel;
    }

    private static StreamGeometry? ResolveGeometry(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as StreamGeometry : null;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrafficMonitorTabViewModel vm)
        {
            vm.ShowTrafficFilterDialogAsync = ShowTrafficFilterDialogAsync;
            vm.RefreshEntryRowVisual = RefreshEntryRowVisual;
        }

        Dispatcher.UIThread.Post(WireTrafficDataGrid, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(WireHttpDetailFlyoutObserver, DispatcherPriority.Loaded);
        RefreshTrafficGrid();
        Dispatcher.UIThread.Post(() =>
        {
            RefreshTrafficGrid();
            WireTrafficDataGrid();
        }, DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrafficMonitorTabViewModel vm)
        {
            vm.ShowTrafficFilterDialogAsync = null;
            vm.RefreshEntryRowVisual = null;
        }

        UnwireTrafficDataGrid();
        UnwireHttpDetailFlyoutObserver();
        _httpDetailFlyoutHost?.Hide();
        _httpDetailFlyoutHost = null;
    }

    private void WireHttpDetailFlyoutObserver()
    {
        if (_httpDetailVm is not null)
            return;

        if (DataContext is TrafficMonitorTabViewModel vm)
        {
            _httpDetailVm = vm;
            _httpDetailFlyoutHost = new TrafficHttpDetailFlyoutHost(vm);
        }
    }

    private void UnwireHttpDetailFlyoutObserver()
    {
        _httpDetailVm = null;
        _httpDetailFlyoutHost = null;
    }

    private void PostSyncHttpDetailFlyout()
    {
        Dispatcher.UIThread.Post(SyncHttpDetailFlyout, DispatcherPriority.Background);
    }

    private void SyncHttpDetailFlyout()
    {
        if (!IsLoaded || _httpDetailFlyoutHost is null)
            return;

        var suppressOpen = _suppressHttpDetailFlyoutOnceForPointer;
        _suppressHttpDetailFlyoutOnceForPointer = false;

        if (suppressOpen || _httpDetailVm?.SelectedEntry is null)
        {
            _httpDetailFlyoutHost.Hide();
            return;
        }

        _httpDetailFlyoutHost.Sync(this, showAtPointer: true);
    }

    private void WireTrafficDataGrid()
    {
        UnwireTrafficDataGridHandlers();

        _trafficDataGrid = OwnedDataGridTableHost.FindGrid(this, "TrafficDiagGrid");
        if (_trafficDataGrid is null)
            return;

        _cellPointerPressedHandler = OnTrafficGridCellPointerPressed;
        _trafficDataGrid.CellPointerPressed += _cellPointerPressedHandler;

        _selectionChangedHandler = OnTrafficGridSelectionChanged;
        _trafficDataGrid.SelectionChanged += _selectionChangedHandler;

        _loadingRowHandler = OnTrafficGridLoadingRow;
        _trafficDataGrid.LoadingRow += _loadingRowHandler;

        EnsureHighlightContextMenu();
    }

    private void UnwireTrafficDataGridHandlers()
    {
        if (_trafficDataGrid is null)
            return;

        if (_cellPointerPressedHandler is not null)
            _trafficDataGrid.CellPointerPressed -= _cellPointerPressedHandler;
        _cellPointerPressedHandler = null;

        if (_selectionChangedHandler is not null)
            _trafficDataGrid.SelectionChanged -= _selectionChangedHandler;
        _selectionChangedHandler = null;

        if (_loadingRowHandler is not null)
            _trafficDataGrid.LoadingRow -= _loadingRowHandler;
        _loadingRowHandler = null;
    }

    private void UnwireTrafficDataGrid()
    {
        UnwireTrafficDataGridHandlers();
        _trafficDataGrid = null;
    }

    /// <summary>
    /// 与侦察历史表一致：用 <see cref="DataGrid.CellPointerPressed"/> 在代码里更新选中行。
    /// AOT 下 <c>SelectedItem</c> 反射绑定可能失效，不能只依赖 XAML TwoWay。
    /// </summary>
    private void OnTrafficGridCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.DataContext is not DataTableViewModel tableVm)
            return;
        if (e.Row?.DataContext is not DataRowViewModel row)
            return;

        if (e.PointerPressedEventArgs is not { } pointerArgs)
            return;
        var pt = pointerArgs.GetCurrentPoint(grid);

        if (pt.Properties.IsRightButtonPressed)
        {
            _suppressHttpDetailFlyoutOnceForPointer = true;
            tableVm.SelectedRow = row;
            PostClearSuppressIfSelectionUnchanged();
            _trafficContextValueColumnIndex = MapColumnHeaderToValueIndex(tableVm, e.Column);
            return;
        }

        if (!pt.Properties.IsLeftButtonPressed)
            return;

        _suppressHttpDetailFlyoutOnceForPointer = false;
        tableVm.SelectedRow = row;
        QueueSyncHttpDetailFlyout();
    }

    /// <summary>键盘上下键改选中时同步 VM（勿回写 <see cref="DataGrid.SelectedItem"/>，避免重入闪退）。</summary>
    private void OnTrafficGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_handlingGridSelection || _suppressHttpDetailFlyoutOnceForPointer)
            return;
        if (sender is not DataGrid grid || grid.DataContext is not DataTableViewModel tableVm)
            return;

        if (grid.SelectedItem is not DataRowViewModel row)
            return;

        try
        {
            _handlingGridSelection = true;
            if (!ReferenceEquals(tableVm.SelectedRow, row))
                tableVm.SelectedRow = row;
            QueueSyncHttpDetailFlyout();
        }
        finally
        {
            _handlingGridSelection = false;
        }
    }

    private void QueueSyncHttpDetailFlyout() =>
        Dispatcher.UIThread.Post(SyncHttpDetailFlyout, DispatcherPriority.Background);

    private void TrafficGrid_ContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu cm || DataContext is not TrafficMonitorTabViewModel vm)
            return;

        var copyUrl = FindTrafficMenuItem(cm, "复制 URL");
        var replay = FindTrafficMenuItem(cm, "发送到 HTTP 重放");
        var intruder = FindTrafficMenuItem(cm, "发送到 Intruder");
        var openUrl = FindTrafficMenuItem(cm, "在浏览器中打开 URL");
        var copyCell = FindTrafficMenuItem(cm, "复制单元格文本");
        var portScan = FindTrafficMenuItem(cm, "端口扫描（此请求主机）");
        var clearAll = FindTrafficMenuItem(cm, "清除所有记录");
        var highlightMenu = FindTrafficMenuItem(cm, "高亮颜色");
        var clearHighlight = FindTrafficMenuItem(cm, "清除高亮");
        var editRemark = FindTrafficMenuItem(cm, "编辑备注…");
        if (copyUrl is null || replay is null || intruder is null || openUrl is null || copyCell is null || portScan is null || clearAll is null)
            return;

        var hasRow = vm.SelectedEntry is not null;
        var hasUrl = hasRow && !string.IsNullOrWhiteSpace(vm.SelectedEntry!.Url);
        copyUrl.IsEnabled = hasUrl;
        replay.IsEnabled = hasRow;
        intruder.IsEnabled = hasRow;
        EnableDecodeAndDiffMenus(cm, vm, hasRow);
        openUrl.IsEnabled = hasUrl && Uri.TryCreate(vm.SelectedEntry!.Url, UriKind.Absolute, out _);
        portScan.IsEnabled = hasUrl &&
            TrafficMonitorTabViewModel.TryGetTrafficRequestHost(vm.SelectedEntry!.Url, out _);
        copyCell.IsEnabled = hasRow && _trafficContextValueColumnIndex >= 0 &&
            vm.GetSelectedTrafficCellText(_trafficContextValueColumnIndex) is { Length: > 0 };
        clearAll.IsEnabled = vm.Table.Rows.Count > 0;

        if (highlightMenu is not null)
            highlightMenu.IsEnabled = hasRow;
        if (clearHighlight is not null)
            clearHighlight.IsEnabled = hasRow && vm.SelectedEntry!.HasHighlight;
        if (editRemark is not null)
            editRemark.IsEnabled = hasRow;

        if (highlightMenu is not null && hasRow)
            UpdateHighlightMenuChecks(highlightMenu, vm.SelectedEntry!.Color);
    }

    private static void UpdateHighlightMenuChecks(MenuItem highlightMenu, TrafficHighlightColor current)
    {
        foreach (var item in highlightMenu.Items)
        {
            if (item is not MenuItem menuItem || menuItem.Tag is not TrafficHighlightColor color)
                continue;

            var label = TrafficHighlightBrushes.GetDisplayName(color);
            var display = current == color ? $"✓ {label}" : label;
            if (menuItem.Header is StackPanel panel
                && panel.Children.OfType<TextBlock>().LastOrDefault() is { } textBlock)
            {
                textBlock.Text = display;
            }
            else
            {
                menuItem.Header = display;
            }
        }
    }

    private static MenuItem? FindTrafficMenuItem(ContextMenu cm, string header)
        => FindTrafficMenuItem((ItemsControl)cm, header);

    private static MenuItem? FindTrafficMenuItem(ItemsControl parent, string header)
    {
        foreach (var item in parent.Items)
        {
            if (item is MenuItem menuItem
                && string.Equals(menuItem.Header?.ToString(), header, StringComparison.Ordinal))
                return menuItem;
        }

        return null;
    }

    private async void TrafficGridMenu_CopyUrl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TrafficMonitorTabViewModel vm || vm.SelectedEntry is null)
            return;

        var url = vm.SelectedEntry.Url;
        if (string.IsNullOrWhiteSpace(url))
            return;

        var clipboard = ResolveClipboardFromMenu(sender);
        if (clipboard is null)
            return;

        await clipboard.SetTextAsync(url);
        await clipboard.FlushAsync();
    }

    private static void EnableDecodeAndDiffMenus(ContextMenu cm, TrafficMonitorTabViewModel vm, bool hasRow)
    {
        var decodeMenu = FindTrafficMenuItem(cm, "发送到 Decoder");
        var diffMenu = FindTrafficMenuItem(cm, "发送到 Comparer");
        if (decodeMenu is not null)
            decodeMenu.IsEnabled = hasRow;

        if (diffMenu is not null)
        {
            diffMenu.IsEnabled = hasRow;
            var reqVsResp = FindTrafficMenuItem(diffMenu, "请求 vs 响应");
            var prevResp = FindTrafficMenuItem(diffMenu, "与上一条响应对比");
            if (reqVsResp is not null)
                reqVsResp.IsEnabled = hasRow;
            if (prevResp is not null)
                prevResp.IsEnabled = hasRow && vm.GetPreviousEntry(vm.SelectedEntry!) is not null;
        }
    }

    private void TrafficGridMenu_SendReplay(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrafficMonitorTabViewModel vm)
            vm.SendSelectedToReplayCommand.Execute(null);
    }

    private void TrafficGridMenu_SendIntruder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrafficMonitorTabViewModel vm)
            vm.SendSelectedToIntruderCommand.Execute(null);
    }

    private void TrafficGridMenu_SendDecodeRequest(object? sender, RoutedEventArgs e)
        => SendSelectedToDecode(HttpTrafficTextPart.Request);

    private void TrafficGridMenu_SendDecodeResponse(object? sender, RoutedEventArgs e)
        => SendSelectedToDecode(HttpTrafficTextPart.Response);

    private void TrafficGridMenu_SendDecodeRequestBody(object? sender, RoutedEventArgs e)
        => SendSelectedToDecode(HttpTrafficTextPart.RequestBody);

    private void TrafficGridMenu_SendDecodeResponseBody(object? sender, RoutedEventArgs e)
        => SendSelectedToDecode(HttpTrafficTextPart.ResponseBody);

    private void SendSelectedToDecode(HttpTrafficTextPart part)
    {
        if (DataContext is not TrafficMonitorTabViewModel vm || vm.SelectedEntry is null)
            return;

        vm.SendEntryToDecode(vm.SelectedEntry, part);
    }

    private void TrafficGridMenu_SendDiffRequestResponse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TrafficMonitorTabViewModel vm || vm.SelectedEntry is null)
            return;

        vm.SendEntryRequestVsResponseDiff(vm.SelectedEntry);
    }

    private void TrafficGridMenu_SendDiffPreviousResponse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TrafficMonitorTabViewModel vm || vm.SelectedEntry is null)
            return;

        vm.SendEntryVsPreviousResponseDiff(vm.SelectedEntry);
    }

    private void TrafficGridMenu_OpenUrl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrafficMonitorTabViewModel vm)
            vm.OpenSelectedTrafficUrlCommand.Execute(null);
    }

    private async void TrafficGridMenu_CopyCell(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TrafficMonitorTabViewModel vm || _trafficContextValueColumnIndex < 0)
            return;
        var text = vm.GetSelectedTrafficCellText(_trafficContextValueColumnIndex);
        if (string.IsNullOrEmpty(text))
            return;
        var clipboard = ResolveClipboardFromMenu(sender);
        if (clipboard is null)
            return;
        await clipboard.SetTextAsync(text);
        await clipboard.FlushAsync();
    }

    private void TrafficGridMenu_PortScan(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrafficMonitorTabViewModel vm)
            vm.OpenPortScanForSelectedTrafficHostCommand.Execute(null);
    }

    private void TrafficGridMenu_ClearAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrafficMonitorTabViewModel vm)
            vm.ClearCommand.Execute(null);
    }

    private async void TrafficGridMenu_ClearHighlight(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TrafficMonitorTabViewModel vm)
            return;

        await vm.SetSelectedHighlightAsync(TrafficHighlightColor.None);
    }

    private async void TrafficGridMenu_SetHighlight(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TrafficHighlightColor color })
            return;
        if (DataContext is not TrafficMonitorTabViewModel vm)
            return;

        await vm.SetSelectedHighlightAsync(color);
    }

    private async void TrafficGridMenu_EditRemark(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TrafficMonitorTabViewModel vm || vm.SelectedEntry is null)
            return;

        var remark = await ShowRemarkDialogAsync(vm.SelectedEntry.Remark);
        if (remark is null)
            return;

        await vm.SetSelectedRemarkAsync(remark);
    }

    private void OnTrafficGridLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row?.DataContext is DataRowViewModel rowVm
            && rowVm.Tag is TrafficLogEntryViewModel entry)
        {
            ApplyRowHighlight(e.Row, entry);
        }
    }

    private static void ApplyRowHighlight(DataGridRow row, TrafficLogEntryViewModel entry)
    {
        row.Background = TrafficHighlightBrushes.GetRowBackground(entry.Color);
    }

    private void RefreshEntryRowVisual(TrafficLogEntryViewModel entry)
    {
        if (_trafficDataGrid is null)
            return;

        foreach (var row in _trafficDataGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (row.DataContext is DataRowViewModel rowVm && ReferenceEquals(rowVm.Tag, entry))
            {
                ApplyRowHighlight(row, entry);
                break;
            }
        }
    }

    private void EnsureHighlightContextMenu()
    {
        if (_highlightMenuBuilt)
            return;

        if (!Resources.TryGetValue(TrafficGridContextMenuKey, out var value) || value is not ContextMenu cm)
            return;

        var highlightMenu = FindTrafficMenuItem(cm, "高亮颜色");
        if (highlightMenu is null)
            return;

        highlightMenu.Items.Clear();
        foreach (var color in TrafficHighlightBrushes.SelectableColors)
        {
            var item = new MenuItem
            {
                Header = CreateHighlightMenuHeader(color),
                Tag = color
            };
            item.Click += TrafficGridMenu_SetHighlight;
            highlightMenu.Items.Add(item);
        }

        _highlightMenuBuilt = true;
    }

    private static Control CreateHighlightMenuHeader(TrafficHighlightColor color)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    Width = 12,
                    Height = 12,
                    CornerRadius = new CornerRadius(2),
                    Background = TrafficHighlightBrushes.GetMenuSwatch(color),
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1)
                },
                new TextBlock
                {
                    Text = TrafficHighlightBrushes.GetDisplayName(color),
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
    }

    private async Task<string?> ShowRemarkDialogAsync(string currentRemark)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            owner = desktop.MainWindow;
        if (owner is null)
            return null;

        return await AppDialogService.PromptAsync(
            owner,
            title: "编辑备注",
            initialValue: currentRemark,
            multiline: true);
    }

    private IClipboard? ResolveClipboardFromMenu(object? eventSource)
    {
        if (TopLevel.GetTopLevel(this) is { Clipboard: { } rootClip })
            return rootClip;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { Clipboard: { } mwClip })
            return mwClip;

        return eventSource is MenuItem mi ? ResolveClipboardFromLogicalAncestors(mi) : null;
    }

    /// Popup 里 menu item 上用 GetTopLevel 常为 null，沿 Logical / Visual 祖先再找 TopLevel Clipboard。
    private static IClipboard? ResolveClipboardFromLogicalAncestors(MenuItem item)
    {
        for (ILogical? l = item; l != null; l = l.LogicalParent)
        {
            if (l is Visual v && TopLevel.GetTopLevel(v) is { Clipboard: { } c })
                return c;
        }

        foreach (var v in item.GetVisualAncestors())
        {
            if (v is Visual vis && TopLevel.GetTopLevel(vis) is { Clipboard: { } c })
                return c;
        }

        return null;
    }

    /// <summary>
    /// 右键点在已选中的行上时不会触发 SelectedEntry 变更，需在队列末尾丢弃抑制位，避免影响后续左键/键盘选中。
    /// </summary>
    private void PostClearSuppressIfSelectionUnchanged()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_suppressHttpDetailFlyoutOnceForPointer)
                    _suppressHttpDetailFlyoutOnceForPointer = false;
            },
            DispatcherPriority.Background);
    }

    private static int MapColumnHeaderToValueIndex(DataTableViewModel tableVm, DataGridColumn? col)
    {
        if (col?.Header is not string header)
            return -1;
        for (var i = 0; i < tableVm.Columns.Count; i++)
        {
            if (string.Equals(tableVm.Columns[i].Header, header, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    public void NotifyTabActivated()
    {
        RefreshTrafficGrid();
        WireTrafficDataGrid();
    }

    private void RefreshTrafficGrid()
    {
        if (DataContext is TrafficMonitorTabViewModel vm)
            OwnedDataGridTableHost.Refresh(this, vm.Table, "TrafficDiagGrid");
    }

    private async Task ShowTrafficFilterDialogAsync()
    {
        var vm = DataContext as TrafficMonitorTabViewModel;
        if (vm is null)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            owner = desktop.MainWindow;

        var dialogVm = new TrafficFilterDialogViewModel(vm);
        var dialog = new TrafficFilterDialog { DataContext = dialogVm };
        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }
}
