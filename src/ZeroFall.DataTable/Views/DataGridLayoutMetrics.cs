using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ZeroFall.DataTable.ViewModels;

namespace ZeroFall.DataTable.Views;

/// <summary>
/// DataGrid 行高/字号等布局常量。PersistTab 隐藏再显示时 Semi 主题与 DynamicResource 可能未重新套用，故在代码中可重复施加。
/// </summary>
public static class DataGridLayoutMetrics
{
    public const double RowHeight = 24;
    public const double FallbackFontSize = 14;

    public static void ApplyChrome(DataGrid grid)
    {
        grid.RowHeight = RowHeight;
        grid.MinHeight = RowHeight;
        grid.FontSize = ResolveGridFontSize();
    }

    private static double ResolveGridFontSize()
    {
        var app = Application.Current;
        if (app?.TryGetResource("SemiFontSizeRegular", app.ActualThemeVariant, out var value) == true
            && value is double size)
            return size;
        return FallbackFontSize;
    }

    /// <summary>
    /// 清除单元格 TextBlock 的 LineHeight（Avalonia 设 LineHeight 会导致贴顶），并强制垂直居中。
    /// </summary>
    public static void ApplyCellTextAlignment(DataGrid grid)
    {
        foreach (var cell in grid.GetVisualDescendants().OfType<DataGridCell>())
        {
            cell.Padding = new Thickness(0);
            cell.VerticalContentAlignment = VerticalAlignment.Center;
            cell.HorizontalContentAlignment = HorizontalAlignment.Stretch;

            foreach (var presenter in cell.GetVisualDescendants().OfType<ContentPresenter>())
            {
                presenter.Padding = new Thickness(2);
                presenter.VerticalContentAlignment = VerticalAlignment.Center;
                presenter.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            }

            foreach (var text in cell.GetVisualDescendants().OfType<TextBlock>())
            {
                text.ClearValue(TextBlock.LineHeightProperty);
                text.VerticalAlignment = VerticalAlignment.Center;
                text.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        }

        foreach (var header in grid.GetVisualDescendants().OfType<DataGridColumnHeader>())
        {
            header.Padding = new Thickness(0);
            header.VerticalContentAlignment = VerticalAlignment.Center;
            header.HorizontalContentAlignment = HorizontalAlignment.Stretch;

            foreach (var presenter in header.GetVisualDescendants().OfType<ContentPresenter>())
            {
                presenter.Padding = new Thickness(2);
                presenter.VerticalContentAlignment = VerticalAlignment.Center;
            }

            foreach (var text in header.GetVisualDescendants().OfType<TextBlock>())
            {
                text.ClearValue(TextBlock.LineHeightProperty);
                text.VerticalAlignment = VerticalAlignment.Center;
            }
        }
    }

    public static void RefreshWhenShown(DataGrid grid, DataTableViewModel? table)
    {
        var columnsMatch = DataGridColumnBuilder.ColumnsMatchDataTable(grid, table);
        if (columnsMatch && grid.Bounds.Width >= 1 && grid.Bounds.Height >= 1)
            return;

        ApplyChrome(grid);

        var rebuilt = !columnsMatch;
        DataGridColumnBuilder.RebuildFromDataTable(grid, table);
        if (rebuilt)
            ApplyCellTextAlignment(grid);

        if (!rebuilt && grid.Bounds.Width >= 1 && grid.Bounds.Height >= 1)
            return;

        if (grid.Bounds.Width >= 1 && grid.Bounds.Height >= 1)
        {
            FinishLayoutRefresh(grid, scrollChrome: rebuilt);
            return;
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (grid.Bounds.Width < 1 || grid.Bounds.Height < 1)
                return;
            grid.LayoutUpdated -= handler;
            FinishLayoutRefresh(grid, scrollChrome: rebuilt);
        };
        grid.LayoutUpdated += handler;
    }

    private static void FinishLayoutRefresh(DataGrid grid, bool scrollChrome)
    {
        if (scrollChrome)
            DataGridScrollChrome.Apply(grid);
        grid.InvalidateMeasure();
        grid.InvalidateArrange();
    }
}
