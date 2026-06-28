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

namespace ZeroFall.DataTable.Views;

public partial class DataTableView : UserControl, ITableGridHost, IDockTabToolPanelProvider
{
    private DataGrid? _dataGrid;
    private ScrollViewer? _scrollViewer;
    private DataTableViewModel? _tableVm;
    private StackPanel? _dockToolPanel;

    public DataTableView()
    {
        InitializeComponent();
        Loaded += (_, _) => EnsureDataGridReady();
    }

    /// <summary>由宿主 Tab 在切换为当前页时调用。</summary>
    public void NotifyTabActivated() => EnsureDataGridReady();

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
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            EnsureDataGridReady();
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

        DataGridLayoutMetrics.RefreshWhenShown(_dataGrid, _tableVm);
        WireScrollViewer(_dataGrid);
    }

    private void WireOpenCsvSaveStream(DataTableViewModel vm)
    {
        vm.OpenCsvSaveStreamAsync = async () =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null) return null;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出 CSV",
                SuggestedFileName = "export.csv",
                DefaultExtension = "csv",
                FileTypeChoices =
                [
                    new FilePickerFileType("CSV (*.csv)") { Patterns = ["*.csv"] }
                ]
            }).ConfigureAwait(true);
            if (file == null) return null;
            return await file.OpenWriteAsync().ConfigureAwait(true);
        };
        vm.RefreshExportCommands();
    }

    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        EnsureDataGridReady();

    private void OnTableVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DataTableViewModel.ShowLineNumberColumn)
            or nameof(DataTableViewModel.DisplayMode)
            or nameof(DataTableViewModel.Columns))
        {
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

    private DateTimeOffset _lastScrollChromeUtc;

    private void OnDataGridLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not DataGrid dg)
            return;
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastScrollChromeUtc).TotalMilliseconds < 80)
            return;
        _lastScrollChromeUtc = now;
        DataGridScrollChrome.Apply(dg);
    }

    private void OnDataGridCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (DataContext is not DataTableViewModel vm) return;
        if (vm.DisableUrlColumns) return;
        if (e.Row is null) return;
        if (e.Column?.Header is not string header || !DataGridColumnBuilder.IsUrlHeader(header)) return;
        if (e.Row.DataContext is not DataRowViewModel row) return;

        var idx = -1;
        for (var i = 0; i < vm.Columns.Count; i++)
        {
            if (string.Equals(vm.Columns[i].Header, header, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0 || idx >= row.Values.Count) return;
        var raw = row.Values[idx]?.ToString()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw)) return;

        vm.OpenUrlCommand.Execute(raw);
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
        if (_scrollViewer != null)
            _scrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
        _scrollViewer = dataGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scrollViewer != null)
            _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
    }
}
