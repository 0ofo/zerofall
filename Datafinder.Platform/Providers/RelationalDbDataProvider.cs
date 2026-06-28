using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Datafinder.Base.Data;

namespace Datafinder.Platform.Providers;

public sealed class RelationalDbDataProvider : IDataProvider
{
    private readonly IRelationalDbBrowser _browser;
    private readonly bool _isTableMode;
    private IReadOnlyList<string>? _columns;
    private long? _cachedTotalCount;
    private bool _disposed;

    private RelationalDbDataProvider(
        IRelationalDbBrowser browser,
        string connectionReference,
        string querySource,
        bool isTableMode,
        string title)
    {
        _browser = browser;
        _isTableMode = isTableMode;
        DatabasePath = connectionReference;
        QuerySource = querySource;
        Title = title;
    }

    public string Title { get; }
    public string DatabasePath { get; }
    public string QuerySource { get; }
    public bool SupportsTotalCount => true;
    public bool CanEdit => false;
    public bool CanWriteBack => false;
    public bool IsDirty => false;

    public IReadOnlyList<string> Columns => _columns ?? Array.Empty<string>();

    public async Task<IReadOnlyList<string>> GetColumnsAsync()
    {
        if (_columns != null)
            return _columns;

        var page = _isTableMode
            ? await _browser.GetTablePageAsync(DatabasePath, QuerySource, 0, 1).ConfigureAwait(false)
            : await _browser.ExecuteQueryPageAsync(DatabasePath, QuerySource, 0, 1).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(page.Error))
            throw new InvalidOperationException(page.Error);

        _columns = page.Columns;
        return _columns;
    }

    public static RelationalDbDataProvider ForTable(
        IRelationalDbBrowser browser,
        string connectionReference,
        string tableName) =>
        new(browser, connectionReference, tableName, isTableMode: true, tableName);

    public static RelationalDbDataProvider ForQuery(
        IRelationalDbBrowser browser,
        string connectionReference,
        string sql,
        string title) =>
        new(browser, connectionReference, sql, isTableMode: false, title);

    public async Task<DataPageResult> GetPageAsync(int offset, int limit)
    {
        var page = _isTableMode
            ? await _browser.GetTablePageAsync(DatabasePath, QuerySource, offset, limit)
            : await _browser.ExecuteQueryPageAsync(DatabasePath, QuerySource, offset, limit);

        if (!string.IsNullOrEmpty(page.Error))
            throw new InvalidOperationException(page.Error);

        _columns ??= page.Columns;
        return new DataPageResult
        {
            Rows = page.Rows,
            Offset = offset,
            Count = (int)page.RowCount
        };
    }

    public async Task<long> GetTotalCountAsync()
    {
        if (_cachedTotalCount.HasValue) return _cachedTotalCount.Value;
        _cachedTotalCount = _isTableMode
            ? await _browser.GetTableRowCountAsync(DatabasePath, QuerySource)
            : await _browser.ExecuteQueryRowCountAsync(DatabasePath, QuerySource);
        return _cachedTotalCount.Value;
    }

    public Task<int> UpdateRowAsync(long rowId, IReadOnlyList<object?> values) => Task.FromResult(0);

    public Task<int> InsertRowAsync(IReadOnlyList<object?> values) => Task.FromResult(0);

    public Task<int> DeleteRowAsync(long rowId) => Task.FromResult(0);

    public Task WriteBackAsync() => Task.CompletedTask;

    public Task RefreshFromSourceAsync()
    {
        _cachedTotalCount = null;
        _columns = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
