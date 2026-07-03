using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ZeroFall.AssetRecon.ViewModels;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Views;
using ZeroFall.Platform.Registries;

namespace ZeroFall.AssetRecon.Views;

public partial class AssetReconResultsView : UserControl, ITableGridHost, IDockTabToolPanelProvider
{
    private AssetReconViewModel? _vm;
    private DataTableViewModel? _resultsTable;
    private AssetReconAssetRowFlyoutBinder? _binder;
    private DataTablePagerView? _dockToolPanel;

    public AssetReconResultsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        WireFromDataContext();
        RefreshResultsGrid();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e) =>
        Unwire();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        WireFromDataContext();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Width > 1 && e.NewSize.Height > 1
            && (e.PreviousSize.Width < 1 || e.PreviousSize.Height < 1))
            RefreshResultsGrid();
    }

    private void WireFromDataContext()
    {
        Unwire();
        _vm = DataContext as AssetReconViewModel;
        if (_vm is null)
            return;

        WireResultsTable(_vm.ResultsTable);

        var leftVm = _vm.LeftPanelViewModel;
        if (leftVm is null)
            return;

        _binder = new AssetReconAssetRowFlyoutBinder(
            this,
            _vm.ResultsTable,
            leftVm,
            () => _vm.TryGetReconAssetRowSource(out var db, out var tid) ? (db, tid) : null);
    }

    private void WireResultsTable(DataTableViewModel table)
    {
        UnwireResultsTable();
        _resultsTable = table;
        _resultsTable.PropertyChanged += OnResultsTablePropertyChanged;
        _resultsTable.Columns.CollectionChanged += OnResultsTableColumnsChanged;
    }

    private void UnwireResultsTable()
    {
        if (_resultsTable is null)
            return;

        _resultsTable.PropertyChanged -= OnResultsTablePropertyChanged;
        _resultsTable.Columns.CollectionChanged -= OnResultsTableColumnsChanged;
        _resultsTable = null;
    }

    private void OnResultsTablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DataTableViewModel.ShowLineNumberColumn)
            or nameof(DataTableViewModel.DisplayMode)
            or nameof(DataTableViewModel.Columns))
        {
            RefreshResultsGrid();
        }
    }

    private void OnResultsTableColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshResultsGrid();

    private void Unwire()
    {
        UnwireResultsTable();
        _binder?.Dispose();
        _binder = null;
        _vm = null;
    }

    public void NotifyTabActivated() => RefreshResultsGrid();

    public Control? GetDockTabToolPanel()
    {
        if (DataContext is not AssetReconViewModel vm)
            return null;

        _dockToolPanel ??= new DataTablePagerView();
        _dockToolPanel.DataContext = vm.ResultsTable;
        return _dockToolPanel;
    }

    private void RefreshResultsGrid()
    {
        var table = _resultsTable ?? _vm?.ResultsTable;
        if (table is null)
            return;
        OwnedDataGridTableHost.Refresh(this, table, "ResultsDiagGrid");
    }
}
