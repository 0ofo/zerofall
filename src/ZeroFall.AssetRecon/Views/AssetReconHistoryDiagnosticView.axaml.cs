using System;
using Avalonia.Controls;
using Avalonia.Threading;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Views;

namespace ZeroFall.AssetRecon.Views;

/// <summary>排查用：侦察历史 Bottom Tab 直接挂 DataGrid。</summary>
public partial class AssetReconHistoryDiagnosticView : UserControl, ITableGridHost
{
    public AssetReconHistoryDiagnosticView()
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

    private void RefreshGrid()
    {
        if (DataContext is not DataTableViewModel vm)
            return;
        OwnedDataGridTableHost.Refresh(this, vm, "HistoryDiagGrid");
    }
}
