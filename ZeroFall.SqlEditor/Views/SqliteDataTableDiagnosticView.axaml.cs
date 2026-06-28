using System;
using Avalonia.Controls;
using Avalonia.Threading;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Views;
using ZeroFall.Platform.Registries;

namespace ZeroFall.SqlEditor.Views;

/// <summary>排查用：SQLite Content Tab 直接挂 DataGrid，不经过 <see cref="DataTableView"/>。</summary>
public partial class SqliteDataTableDiagnosticView : UserControl, ITableGridHost, IDockTabToolPanelProvider
{
    private DataTablePagerView? _dockToolPanel;

    public SqliteDataTableDiagnosticView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshGrid();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        RefreshGrid();
    }

    public void NotifyTabActivated() => RefreshGrid();

    public Control? GetDockTabToolPanel()
    {
        if (DataContext is not DataTableViewModel vm)
            return null;

        _dockToolPanel ??= new DataTablePagerView();
        _dockToolPanel.DataContext = vm;
        return _dockToolPanel;
    }

    private void RefreshGrid()
    {
        if (DataContext is not DataTableViewModel vm)
            return;
        OwnedDataGridTableHost.Refresh(this, vm, "SqliteDiagGrid");
    }
}
