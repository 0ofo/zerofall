using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ZeroFall.DataTable.ViewModels;

namespace ZeroFall.DataTable.Views;

/// <summary>
/// 各面板内嵌自有 DataGrid（绕过 <see cref="DataTableView"/>）时的列重建与查找。
/// </summary>
public static class OwnedDataGridTableHost
{
    public static DataGrid? FindGrid(Control host, string? gridName = null)
    {
        if (!string.IsNullOrEmpty(gridName) && host.FindControl<DataGrid>(gridName) is { } named)
            return named;
        return host.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault();
    }

    public static void Refresh(Control host, DataTableViewModel? table, string? gridName = null)
    {
        var grid = FindGrid(host, gridName);
        if (grid is null || table is null)
            return;

        if (!ReferenceEquals(grid.DataContext, table))
            grid.DataContext = table;

        DataGridLayoutMetrics.RefreshWhenShown(grid, table);
    }
}
