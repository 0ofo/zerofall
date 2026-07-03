using System;
using Avalonia.Controls;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Views;
using ZeroFall.Platform.Registries;

namespace ZeroFall.SqlEditor.Views;

/// <summary>SQLite / 关系型数据源表：Dock Tab 内嵌 DataGrid + 标签栏分页/导出工具。</summary>
public partial class SqliteDataTableDiagnosticView : UserControl, ITableGridHost, IDockTabToolPanelProvider
{
    private DataTablePagerView? _dockToolPanel;

    public SqliteDataTableDiagnosticView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshGrid();
    }

    protected override void OnDataContextChanged(EventArgs e)    {
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
