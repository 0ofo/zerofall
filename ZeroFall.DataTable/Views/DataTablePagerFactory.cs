using Avalonia.Controls;
using ZeroFall.DataTable.ViewModels;

namespace ZeroFall.DataTable.Views;

/// <summary>
/// 生成分页工具条控件，供表格 View 内嵌挂载。
/// </summary>
public static class DataTablePagerFactory
{
    public static Control CreateControl(DataTableViewModel table) =>
        new DataTablePagerView { DataContext = table };
}
