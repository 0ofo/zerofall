using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZeroFall.Base.Data;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.DataTable.Views;

public partial class DataTableView : UserControl, ITableGridHost, IDockTabToolPanelProvider, ITabContentReleasable
{
    private DataGrid? _dataGrid;
    private ScrollViewer? _scrollViewer;
    private DataTableViewModel? _tableVm;
    private StackPanel? _dockToolPanel;
    private bool _ensureReadyPosted;
    private bool _gridShownReady;
    private bool _scrollViewerWired;

    public DataTableView()
    {
        InitializeComponent();
        Loaded += (_, _) => EnsureDataGridReady();
    }

    /// <summary>由宿主 Tab 在切换为当前页时调用。</summary>
    public void NotifyTabActivated() => ScheduleEnsureDataGridReady();

    private void ScheduleEnsureDataGridReady()
    {
        if (_ensureReadyPosted)
            return;

        _ensureReadyPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _ensureReadyPosted = false;
            EnsureDataGridReady();
        }, DispatcherPriority.Background);
    }

    public Control? GetDockTabToolPanel()
    {
        if (DataContext is not DataTableViewModel vm || !vm.CanWriteBack)
            return null;

        _dockToolPanel ??= CreateDockToolPanel();
        _dockToolPanel.DataContext = DataContext;
        return _dockToolPanel;
    }

    private static StackPanel CreateDockToolPanel()
    {
        var panel = DockTabToolPanelHelper.CreateHorizontalPanel();
        DockTabToolPanelHelper.AddTextCommandButton(
            panel,
            "保存到源文件",
            nameof(DataTableViewModel.WriteBackCommand));
        DockTabToolPanelHelper.AddTextCommandButton(
            panel,
            "重新索引",
            nameof(DataTableViewModel.RefreshFromSourceCommand));
        return panel;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_tableVm != null)
        {
            _tableVm.PropertyChanged -= OnTableVmPropertyChanged;
            _tableVm.Columns.CollectionChanged -= OnColumnsCollectionChanged;
            _tableVm.OpenCsvSaveStreamAsync = null;
        }

        _tableVm = DataContext as DataTableViewModel;
        if (_tableVm != null)
        {
            _tableVm.PropertyChanged += OnTableVmPropertyChanged;
            _tableVm.Columns.CollectionChanged += OnColumnsCollectionChanged;
        }

        if (TopLevel.GetTopLevel(this) != null && _tableVm != null)
            WireOpenCsvSaveStream(_tableVm);

        EnsureDataGridReady();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !change.GetNewValue<bool>())
        {
            _gridShownReady = false;
            _scrollChromeApplied = false;
        }
        else if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            ScheduleEnsureDataGridReady();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Width > 1 && e.NewSize.Height > 1
            && (e.PreviousSize.Width < 1 || e.PreviousSize.Height < 1))
            EnsureDataGridReady();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_tableVm != null)
            WireOpenCsvSaveStream(_tableVm);
        EnsureDataGridReady();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_tableVm != null)
            _tableVm.OpenCsvSaveStreamAsync = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void EnsureDataGridReady()
    {
        _tableVm ??= DataContext as DataTableViewModel;
        _dataGrid ??= this.FindControl<DataGrid>("TabDataGrid")
                     ?? this.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault();

        if (_dataGrid == null || _tableVm == null)
            return;

        if (_gridShownReady
            && DataGridColumnBuilder.ColumnsMatchDataTable(_dataGrid, _tableVm)
            && _dataGrid.Bounds.Width >= 1
            && _dataGrid.Bounds.Height >= 1)
        {
            WireScrollViewer(_dataGrid);
            return;
        }

        DataGridLayoutMetrics.RefreshWhenShown(_dataGrid, _tableVm);
        _gridShownReady = DataGridColumnBuilder.ColumnsMatchDataTable(_dataGrid, _tableVm)
                          && _dataGrid.Bounds.Width >= 1
                          && _dataGrid.Bounds.Height >= 1;
        WireScrollViewer(_dataGrid);
    }

    private void WireOpenCsvSaveStream(DataTableViewModel vm)
    {
        DataTableCsvExportHost.Attach(vm, this);
    }

    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _gridShownReady = false;
        _scrollChromeApplied = false;
        EnsureDataGridReady();
    }

    private void OnTableVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DataTableViewModel.ShowLineNumberColumn)
            or nameof(DataTableViewModel.DisplayMode)
            or nameof(DataTableViewModel.Columns))
        {
            _gridShownReady = false;
            _scrollChromeApplied = false;
            EnsureDataGridReady();
        }
    }

    private void OnDataGridLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not DataGrid dataGrid) return;
        _dataGrid = dataGrid;
        dataGrid.SelectionChanged += OnDataGridSelectionChanged;
        dataGrid.LayoutUpdated += OnDataGridLayoutUpdated;
        EnsureDataGridReady();
    }

    private void OnDataGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_dataGrid is null || _tableVm is null)
            return;
        if (_dataGrid.SelectedItem is DataRowViewModel row)
            _tableVm.SelectedRow = row;
    }

    private bool _scrollChromeApplied;

    private void OnDataGridLayoutUpdated(object? sender, EventArgs e)
    {
        if (_scrollChromeApplied || sender is not DataGrid dg)
            return;
        _scrollChromeApplied = true;
        DataGridScrollChrome.Apply(dg);
    }

    private void OnDataGridCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (DataContext is not DataTableViewModel vm) return;
        DataGridColumnBuilder.TryHandleUrlCellPointerPress(vm, e);
    }

    private void OnDataGridCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (DataContext is not DataTableViewModel vm) return;
        if (e.EditAction != DataGridEditAction.Commit) return;

        var row = e.Row.DataContext as DataRowViewModel;
        if (row != null)
            _ = vm.SaveRowCommand.ExecuteAsync(row);
    }

    private void OnScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not DataTableViewModel vm) return;
        if (vm.DisplayMode != DataTableDisplayMode.VirtualScroll) return;
        if (_scrollViewer == null) return;

        var visibleStartIndex = (int)_scrollViewer.Offset.Y;
        var visibleEndIndex = visibleStartIndex + (int)_scrollViewer.Viewport.Height;

        vm.CheckPreload(visibleStartIndex, visibleEndIndex);
    }

    private void WireScrollViewer(DataGrid dataGrid)
    {
        if (_scrollViewerWired && _scrollViewer != null)
            return;

        if (_scrollViewer != null)
            _scrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
        _scrollViewer = dataGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
            _scrollViewerWired = true;
        }
    }

    public void ReleaseTabResources()
    {
        if (_tableVm is not null)
        {
            _tableVm.PropertyChanged -= OnTableVmPropertyChanged;
            _tableVm.Columns.CollectionChanged -= OnColumnsCollectionChanged;
            _tableVm.OpenCsvSaveStreamAsync = null;
        }

        if (_scrollViewer is not null)
            _scrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;

        if (DataContext is DataTableViewModel tableViewModel)
            tableViewModel.Dispose();

        DataContext = null;
        _tableVm = null;
        _dataGrid = null;
        _scrollViewer = null;
        _dockToolPanel = null;
    }
}
