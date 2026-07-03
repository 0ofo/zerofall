namespace ZeroFall.DataTable.Views;

/// <summary>内嵌自有 DataGrid 的面板在 Tab 激活时刷新列与数据源。</summary>
public interface ITableGridHost
{
    void NotifyTabActivated();
}
