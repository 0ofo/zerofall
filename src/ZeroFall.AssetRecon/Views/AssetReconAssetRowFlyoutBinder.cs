using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZeroFall.AssetRecon.ViewModels;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Views;

namespace ZeroFall.AssetRecon.Views;

/// <summary>
/// 将某结果表选中行映射到左侧条目的资产详情填充，并用 Flyout（<see cref="AssetReconDetailPanelView"/>）展示。
/// </summary>
internal sealed class AssetReconAssetRowFlyoutBinder : IDisposable
{
    private readonly Control _placementTarget;
    private readonly DataTableViewModel _table;
    private readonly AssetReconLeftPanelViewModel _leftVm;
    private readonly Func<(string DbPath, string TaskId)?> _resolveContext;

    private PropertyChangedEventHandler? _tableHandler;
    private PropertyChangedEventHandler? _leftHandler;

    private Flyout? _detailFlyout;
    private AssetReconDetailPanelView? _detailPanel;
    private Border? _detailHost;
    private DataGrid? _dataGrid;
    private EventHandler<DataGridCellPointerPressedEventArgs>? _cellPressedHandler;
    private int _flyoutMeasureGeneration;
    private int _detailLoadGeneration;
    private bool _disposed;

    public AssetReconAssetRowFlyoutBinder(
        Control placementTarget,
        DataTableViewModel table,
        AssetReconLeftPanelViewModel leftVm,
        Func<(string DbPath, string TaskId)?> resolveContext)
    {
        _placementTarget = placementTarget;
        _table = table;
        _leftVm = leftVm;
        _resolveContext = resolveContext;

        _tableHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(DataTableViewModel.SelectedRow))
                QueuePushDetail();
        };
        _leftHandler = (_, args) =>
        {
            if (args.PropertyName is nameof(AssetReconLeftPanelViewModel.DetailGeneration)
                or nameof(AssetReconLeftPanelViewModel.DetailScope))
                PostSyncDetailFlyout();
        };

        _table.PropertyChanged += _tableHandler;
        _leftVm.PropertyChanged += _leftHandler;
        _placementTarget.Loaded += OnPlacementTargetLoaded;
        _placementTarget.Unloaded += OnPlacementTargetUnloaded;

        if (_placementTarget.IsLoaded)
            TryHookDataGridCellPointer();

        QueuePushDetail();
    }

    private void OnPlacementTargetLoaded(object? sender, RoutedEventArgs e) =>
        TryHookDataGridCellPointer();

    private void OnPlacementTargetUnloaded(object? sender, RoutedEventArgs e) =>
        Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _placementTarget.Loaded -= OnPlacementTargetLoaded;
        _placementTarget.Unloaded -= OnPlacementTargetUnloaded;
        UnhookDataGridCellPointer();

        if (_tableHandler != null)
            _table.PropertyChanged -= _tableHandler;
        _tableHandler = null;

        if (_leftHandler != null)
            _leftVm.PropertyChanged -= _leftHandler;
        _leftHandler = null;

        _detailFlyout?.Hide();
        _detailFlyout = null;
        _detailHost = null;
        _detailPanel = null;
    }

    private void TryHookDataGridCellPointer()
    {
        if (_disposed)
            return;

        UnhookDataGridCellPointer();

        _dataGrid = OwnedDataGridTableHost.FindGrid(_placementTarget);
        if (_dataGrid is null)
            return;

        _cellPressedHandler = OnDataGridCellPointerPressed;
        _dataGrid.CellPointerPressed += _cellPressedHandler;
    }

    private void UnhookDataGridCellPointer()
    {
        if (_dataGrid is not null && _cellPressedHandler is not null)
            _dataGrid.CellPointerPressed -= _cellPressedHandler;
        _cellPressedHandler = null;
        _dataGrid = null;
    }

    /// <summary>
    /// 已选中行再次点击时 <see cref="DataTableViewModel.SelectedRow"/> 不变，需靠单元格点击重新弹出 Flyout。
    /// </summary>
    private void OnDataGridCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (_disposed || e.Row is null || e.Row.DataContext is not DataRowViewModel row)
            return;

        if (DataGridColumnBuilder.TryHandleUrlCellPointerPress(_table, e))
            return;

        _table.SelectedRow = row;
        QueuePushDetail();
    }

    private void QueuePushDetail()
    {
        Dispatcher.UIThread.Post(() => _ = PushDetailFromSelectionAsync(), DispatcherPriority.Normal);
    }

    private async Task PushDetailFromSelectionAsync()
    {
        if (_disposed)
            return;

        var loadGen = ++_detailLoadGeneration;

        var row = _table.SelectedRow;
        if (row is null)
        {
            _leftVm.ClearDetailIfResultRow();
            if (loadGen == _detailLoadGeneration)
                PostSyncDetailFlyout();
            return;
        }

        var ctx = _resolveContext();
        if (!ctx.HasValue)
            return;

        var (db, taskId) = ctx.Value;
        if (string.IsNullOrEmpty(db) || string.IsNullOrEmpty(taskId))
            return;

        var sortOrder = row.LineNumber - 1;
        if (sortOrder < 0)
            return;

        try
        {
            await _leftVm.ShowAssetDetailAsync(db, taskId, sortOrder).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AssetRecon] ShowAssetDetailAsync failed: {ex}");
            return;
        }

        if (_disposed || loadGen != _detailLoadGeneration)
            return;

        PostSyncDetailFlyout();
    }

    private void PostSyncDetailFlyout()
    {
        Dispatcher.UIThread.Post(SyncDetailFlyout, DispatcherPriority.Background);
    }

    private void EnsureDetailFlyout()
    {
        if (_detailFlyout is not null)
            return;

        _detailPanel = new AssetReconDetailPanelView { DataContext = _leftVm };
        _detailHost = new Border
        {
            Background = Brushes.Transparent,
            Padding = default(Thickness),
            ClipToBounds = true,
            Child = _detailPanel
        };

        _detailFlyout = new Flyout
        {
            Content = _detailHost,
            Placement = PlacementMode.Top,
            ShowMode = FlyoutShowMode.Transient
        };
        _detailFlyout.FlyoutPresenterClasses.Add(AssetReconDetailFlyoutChrome.PresenterClass);
    }

    private void SyncDetailFlyout()
    {
        if (_disposed || !_placementTarget.IsLoaded)
            return;

        try
        {
            if (_leftVm.DetailScope != AssetReconDetailScope.ResultRow ||
                _table.SelectedRow is null ||
                _leftVm.DetailProperties.Count == 0)
            {
                _detailFlyout?.Hide();
                return;
            }

            EnsureDetailFlyout();
            if (_detailFlyout is null || _detailHost is null || _detailPanel is null)
                return;

            var measureGen = ++_flyoutMeasureGeneration;
            ApplyFlyoutContentSize(useFallbackIfNotAttached: true);
            _detailFlyout.ShowAt(_placementTarget, showAtPointer: true);
            ScheduleRemeasureFlyoutSize(measureGen);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AssetRecon] SyncDetailFlyout failed: {ex}");
            _detailFlyout?.Hide();
        }
    }

    private void ApplyFlyoutContentSize(bool useFallbackIfNotAttached = false)
    {
        if (_detailHost is null || _detailPanel is null)
            return;

        var inset = AssetReconDetailFlyoutChrome.HostLayoutInset;
        var minW = AssetReconDetailFlyoutChrome.MinWidth - inset;
        var minH = AssetReconDetailFlyoutChrome.MinHeight - inset;
        var maxW = AssetReconDetailFlyoutChrome.MaxWidth - inset;
        var maxH = AssetReconDetailFlyoutChrome.MaxHeight - inset;

        _detailHost.MinWidth = minW;
        _detailHost.MinHeight = minH;
        _detailHost.MaxWidth = maxW;
        _detailHost.MaxHeight = maxH;

        var attached = _detailPanel.IsAttachedToVisualTree();
        if (!attached && useFallbackIfNotAttached)
        {
            var fallbackW = AssetReconFlyoutLayout.Sanitize(maxW * 0.85, minW, maxW);
            var fallbackH = AssetReconFlyoutLayout.Sanitize(160, minH, maxH);
            SetHostSize(fallbackW, fallbackH);
            return;
        }

        var itemCount = _leftVm.DetailProperties.Count;
        var measured = attached
            ? _detailPanel.TryReadLayoutContentSize(maxW, maxH)
              ?? _detailPanel.EstimateFlyoutContentSize(maxW, maxH, itemCount)
            : _detailPanel.EstimateFlyoutContentSize(maxW, maxH, itemCount);

        var w = AssetReconFlyoutLayout.Sanitize(measured.Width, minW, maxW);
        var h = AssetReconFlyoutLayout.Sanitize(measured.Height, minH, maxH);
        SetHostSize(w, h);
    }

    private void SetHostSize(double w, double h)
    {
        if (_detailHost is null || _detailPanel is null)
            return;

        _detailHost.Width = w;
        _detailHost.Height = h;
        _detailPanel.Width = w;
        _detailPanel.Height = h;
    }

    private void ScheduleRemeasureFlyoutSize(int generation)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || generation != _flyoutMeasureGeneration)
                return;
            if (_detailFlyout is null || !_detailFlyout.IsOpen)
                return;

            try
            {
                ApplyFlyoutContentSize(useFallbackIfNotAttached: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AssetRecon] Remeasure flyout failed: {ex}");
            }
        }, DispatcherPriority.Loaded);
    }
}
