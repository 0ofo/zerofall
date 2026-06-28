using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ZeroFall.Base.Data;
using ZeroFall.Platform.Services;

namespace ZeroFall.Platform.Providers;

public class ApiPagedDataProvider : IDataProvider
{
    private readonly ISqliteService _sqliteService;
    private readonly IApiIndexService _apiIndexService;
    private readonly string _dbPath;
    private readonly string _queryTaskId;
    private readonly string _sourceName;
    private readonly string _target;
    private readonly string[] _displayColumns;
    private readonly string _whereClause;
    private readonly Func<string, int, int, Task<ApiPageResult>> _fetchPage;
    private readonly object _lock = new();

    private int _fetchedCount;
    private readonly int _apiTotalCount;
    private int _currentApiPage;
    private readonly int _apiPageSize;
    private bool _allFetched;
    private bool _disposed;

    public string Title { get; init; } = "";
    public string DatabasePath => _dbPath;
    public string QuerySource => "asset_recon_results";
    public bool SupportsTotalCount => true;
    public bool CanEdit => false;
    public bool CanWriteBack => false;
    public bool IsDirty => false;

    private IReadOnlyList<string>? _columns;
    public IReadOnlyList<string> Columns
    {
        get
        {
            _columns ??= _displayColumns;
            return _columns;
        }
    }

    private ApiPagedDataProvider(
        ISqliteService sqliteService,
        IApiIndexService apiIndexService,
        string dbPath,
        string queryTaskId,
        string sourceName,
        string target,
        int apiTotalCount,
        int initialFetchedCount,
        int currentApiPage,
        int apiPageSize,
        string[] displayColumns,
        string whereClause,
        Func<string, int, int, Task<ApiPageResult>> fetchPage)
    {
        _sqliteService = sqliteService;
        _apiIndexService = apiIndexService;
        _dbPath = dbPath;
        _queryTaskId = queryTaskId;
        _sourceName = sourceName;
        _target = target;
        _apiTotalCount = apiTotalCount;
        _fetchedCount = initialFetchedCount;
        _currentApiPage = currentApiPage;
        _apiPageSize = apiPageSize;
        _displayColumns = displayColumns;
        _whereClause = whereClause;
        _fetchPage = fetchPage;
        _allFetched = _fetchedCount >= _apiTotalCount;
    }

    public static async Task<ApiPagedDataProvider> CreateAsync(
        ISqliteService sqliteService,
        IApiIndexService apiIndexService,
        string dbPath,
        string queryTaskId,
        string sourceName,
        string target,
        string title,
        string[] displayColumns,
        string whereClause,
        Func<string, int, int, Task<ApiPageResult>> fetchPage,
        int apiPageSize = 10)
    {
        var firstPage = await fetchPage(target, 1, apiPageSize);

        if (!firstPage.Success)
            throw new InvalidOperationException($"API 查询失败: {firstPage.ErrorMessage}");

        if (firstPage.Rows.Count > 0)
        {
            await apiIndexService.IndexAssetReconAsync(
                dbPath, queryTaskId, sourceName, target, firstPage.Rows, firstPage.TotalCount);
        }

        return new ApiPagedDataProvider(
            sqliteService, apiIndexService,
            dbPath, queryTaskId, sourceName, target,
            firstPage.TotalCount, firstPage.Rows.Count, 1, apiPageSize,
            displayColumns, whereClause, fetchPage)
        { Title = title };
    }

    public async Task<DataPageResult> GetPageAsync(int offset, int limit)
    {
        await EnsureDataAsync(offset + limit);

        var cols = string.Join(", ", _displayColumns);
        var sql = $"SELECT {cols} FROM \"asset_recon_results\" WHERE {_whereClause} LIMIT {limit} OFFSET {offset}";
        var result = await _sqliteService.ExecuteQueryPagedAsync(_dbPath, sql, offset, limit);

        return new DataPageResult { Rows = result.Rows, Offset = offset, Count = (int)result.RowCount };
    }

    public Task<long> GetTotalCountAsync() => Task.FromResult((long)_apiTotalCount);

    private async Task EnsureDataAsync(int neededUpTo)
    {
        if (_allFetched || _fetchedCount >= neededUpTo) return;

        lock (_lock)
        {
            if (_allFetched || _fetchedCount >= neededUpTo) return;
        }

        while (_fetchedCount < neededUpTo && !_allFetched)
        {
            _currentApiPage++;
            var page = await _fetchPage(_target, _currentApiPage, _apiPageSize);

            if (!page.Success || page.Rows.Count == 0)
            {
                _allFetched = true;
                break;
            }

            await _apiIndexService.IndexAssetReconAsync(
                _dbPath, _queryTaskId, _sourceName, _target, page.Rows, _apiTotalCount);

            _fetchedCount += page.Rows.Count;

            if (_currentApiPage * _apiPageSize >= _apiTotalCount)
            {
                _allFetched = true;
                break;
            }
        }
    }

    public Task<int> UpdateRowAsync(long rowId, IReadOnlyList<object?> values) => Task.FromResult(0);
    public Task<int> InsertRowAsync(IReadOnlyList<object?> values) => Task.FromResult(0);
    public Task<int> DeleteRowAsync(long rowId) => Task.FromResult(0);
    public Task WriteBackAsync() => Task.CompletedTask;
    public Task RefreshFromSourceAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

public class ApiPageResult
{
    public bool Success { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public IReadOnlyList<UnifiedAssetRow> Rows { get; init; } = Array.Empty<UnifiedAssetRow>();
    public int TotalCount { get; init; }
}
