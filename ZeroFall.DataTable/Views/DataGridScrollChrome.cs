using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ZeroFall.DataTable.Views;

/// <summary>
/// Semi/Fluent 主题会在 <see cref="ScrollBar"/> 上启用 <c>AllowAutoHide</c>（细滚动条），
/// 仅设置外层 <see cref="ScrollViewer"/> 往往不够；需在可视树内对每个 <see cref="ScrollBar"/> 关闭自动隐藏并保留最小厚度。
/// </summary>
public static class DataGridScrollChrome
{
    private const double ScrollBarMinThickness = 12;

    public static void Apply(DataGrid grid)
    {
        foreach (var viewer in grid.GetVisualDescendants().OfType<ScrollViewer>())
            viewer.AllowAutoHide = false;

        foreach (var bar in grid.GetVisualDescendants().OfType<ScrollBar>())
        {
            bar.AllowAutoHide = false;
            if (bar.Orientation == Orientation.Vertical)
                bar.MinWidth = Math.Max(bar.MinWidth, ScrollBarMinThickness);
            else
                bar.MinHeight = Math.Max(bar.MinHeight, ScrollBarMinThickness);
        }
    }

    /// <summary>模板与列布局完成后 Semi 会再写一次 ScrollBar，延迟多拍应用更稳。</summary>
    public static void ApplyDeferred(DataGrid grid)
    {
        Apply(grid);
        Dispatcher.UIThread.Post(() => Apply(grid), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() => Apply(grid), DispatcherPriority.Render);
        Dispatcher.UIThread.Post(() => Apply(grid), DispatcherPriority.Background);
    }
}
