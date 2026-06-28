using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Data;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Providers;
using ZeroFall.Platform.Services.RelationalDb;

namespace ZeroFall.SqlEditor.ViewModels;

public partial class SqlEditorViewModel : ViewModelBase
{
    private readonly IRelationalDbBrowserRegistry _relationalDbRegistry;
    private readonly IEventBus _eventBus;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _sql = string.Empty;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    public SqlEditorViewModel(IRelationalDbBrowserRegistry relationalDbRegistry, IEventBus eventBus)
    {
        _relationalDbRegistry = relationalDbRegistry;
        _eventBus = eventBus;
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (string.IsNullOrWhiteSpace(Sql)) return;
        if (string.IsNullOrEmpty(FilePath)) return;

        var browser = _relationalDbRegistry.Resolve(FilePath);
        if (browser == null)
        {
            HasError = true;
            ErrorMessage = "不支持的数据源";
            StatusText = ErrorMessage;
            return;
        }

        IsExecuting = true;
        HasError = false;
        ErrorMessage = string.Empty;
        StatusText = "执行中...";

        try
        {
            var sql = Sql.TrimEnd(';', ' ', '\n', '\r').Trim();
            var isQuery = RelationalSqlHelper.IsReadOnlyQuery(sql);

            if (isQuery)
            {
                var probe = await browser.ExecuteQueryPageAsync(FilePath, sql, 0, 1);

                if (probe.Error != null)
                {
                    HasError = true;
                    ErrorMessage = probe.Error;
                    StatusText = $"错误: {probe.Error}";
                }
                else
                {
                    var provider = RelationalDbDataProvider.ForQuery(browser, FilePath, sql, "查询结果");
                    _eventBus.Publish(new DataResultEvent(provider, "sql-result", "SQL查询"));

                    long totalRows = -1;
                    try { totalRows = await browser.ExecuteQueryRowCountAsync(FilePath, sql); }
                    catch { /* ignore */ }

                    StatusText = $"查询完成: {(totalRows >= 0 ? totalRows : probe.RowCount):N0} 行";
                }
            }
            else
            {
                var affected = await browser.ExecuteNonQueryAsync(FilePath, Sql);
                StatusText = $"执行完成: {affected} 行受影响";
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            StatusText = $"错误: {ex.Message}";
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public async Task LoadTableDataAsync(string tableName)
    {
        if (string.IsNullOrEmpty(FilePath)) return;

        var browser = _relationalDbRegistry.Resolve(FilePath);
        if (browser == null)
        {
            HasError = true;
            ErrorMessage = "不支持的数据源";
            StatusText = ErrorMessage;
            return;
        }

        IsExecuting = true;
        StatusText = $"加载表 {tableName}...";

        try
        {
            var provider = RelationalDbDataProvider.ForTable(browser, FilePath, tableName);
            _eventBus.Publish(new DataResultEvent(provider, "sql-table", tableName));

            long totalRows = -1;
            try { totalRows = await browser.GetTableRowCountAsync(FilePath, tableName); }
            catch { /* ignore */ }

            StatusText = $"{tableName}: {totalRows:N0} 行";
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            StatusText = $"错误: {ex.Message}";
        }
        finally
        {
            IsExecuting = false;
        }
    }
}
