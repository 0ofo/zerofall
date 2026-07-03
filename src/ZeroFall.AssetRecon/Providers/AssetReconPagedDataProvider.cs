using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.AssetRecon.Services;
using ZeroFall.Base.Data;
using ZeroFall.Platform.Services;

namespace ZeroFall.AssetRecon.Providers;

/// <summary>
/// 多源资产测绘：按页从 SQLite 读取，不足时按需并行拉取各源 API；同一 API 页内<strong>先返回的数据源先写入</strong>（<c>sort_order</c> 递增）。
/// </summary>
public sealed class AssetReconPagedDataProvider : IDataProvider, INotifyTotalCountEstimateChanged
{
    private const int MaxApiPageIndex = 500;

    private static readonly string[] SqlColumns =
    {
        "source", "ip", "port", "protocol", "title", "domain", "country",
        "province", "city", "os", "server", "icp", "link", "status_code"
    };

    public static readonly string[] ColumnHeaders =
    {
        "数据源", "IP", "端口", "协议", "标题", "域名", "国家",
        "省份", "城市", "OS", "Server", "ICP", "链接", "状态码"
    };

    private readonly IApiIndexService _apiIndexService;
    private readonly ISqliteService _sqliteService;
    private readonly string _dbPath;
    private readonly string _queryTaskId;
    private readonly List<IReconSource> _activeSources;
    private readonly string _lastQuery;
    private readonly bool _useUnifiedQuery;
    private readonly UnifiedQuery _unifiedQuery;
    /// <summary>与结果表 <see cref="DataTableViewModel.UserPageSize"/> 一致。</summary>
    private readonly int _pageSize;
    /// <summary>各情报源 <c>QueryAsync</c> 单次请求的 size 参数。</summary>
    private readonly int _apiPageSize;
    private readonly int? _maxStoredRows;
    private readonly Dictionary<string, HashSet<int>> _fetchedApiPagesBySource;
    private readonly Dictionary<string, int> _sourceTotalCounts;
    private int _nextApiPage = 1;
    private int _storedResultCount;
    private int _totalCount;
    private bool _remoteListsExhausted;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private bool _disposed;

    public event EventHandler<TotalCountEstimateChangedEventArgs>? TotalCountEstimateChanged;

    private AssetReconPagedDataProvider(
        IApiIndexService apiIndexService,
        ISqliteService sqliteService,
        string dbPath,
        string queryTaskId,
        List<IReconSource> activeSources,
        string lastQuery,
        bool useUnifiedQuery,
        UnifiedQuery unifiedQuery,
        int uiPageSize,
        int apiPageSize,
        int? maxStoredRows,
        Dictionary<string, HashSet<int>> fetchedApiPagesBySource,
        Dictionary<string, int> sourceTotalCounts,
        int nextApiPage,
        int storedResultCount,
        int totalCount)
    {
        _apiIndexService = apiIndexService;
        _sqliteService = sqliteService;
        _dbPath = dbPath;
        _queryTaskId = queryTaskId;
        _activeSources = activeSources;
        _lastQuery = lastQuery;
        _useUnifiedQuery = useUnifiedQuery;
        _unifiedQuery = unifiedQuery;
        _pageSize = uiPageSize;
        _apiPageSize = apiPageSize;
        _maxStoredRows = maxStoredRows is > 0 ? maxStoredRows : null;
        _fetchedApiPagesBySource = fetchedApiPagesBySource;
        _sourceTotalCounts = sourceTotalCounts;
        _nextApiPage = nextApiPage;
        _storedResultCount = storedResultCount;
        _totalCount = totalCount;
        _remoteListsExhausted = false;
    }

    /// <summary>与 UI 用户分页每页行数一致。</summary>
    public int PageSize => _pageSize;

    public string QueryTaskId => _queryTaskId;

    public static async Task<AssetReconPagedDataProvider?> TryCreateAfterFirstBatchAsync(
        IApiIndexService apiIndexService,
        ISqliteService sqliteService,
        string dbPath,
        string queryTaskId,
        IReadOnlyList<IReconSource> sources,
        string lastQuery,
        bool useUnifiedQuery,
        UnifiedQuery unifiedQuery,
        int uiPageSize,
        int apiPageSize,
        int? maxStoredRows = null)
    {
        var activeSources = sources.ToList();
        var fetched = activeSources.ToDictionary(s => s.Name, _ => new HashSet<int>());
        var sourceTotals = new Dictionary<string, int>();

        var inst = new AssetReconPagedDataProvider(
            apiIndexService, sqliteService, dbPath, queryTaskId,
            activeSources, lastQuery, useUnifiedQuery, unifiedQuery, uiPageSize,
            apiPageSize, maxStoredRows,
            fetched, sourceTotals, nextApiPage: 1, storedResultCount: 0, totalCount: 0);

        var first = await inst.FetchMultiSourceBatchOnceAsync().ConfigureAwait(false);
        if (first.NewStoredCount == 0)
        {
            inst.Dispose();
            return null;
        }

        inst._nextApiPage = first.NextApiPage;
        inst._storedResultCount = first.NewStoredCount;
        inst.RefreshTotalCountUpperBound(inst._storedResultCount);

        inst.Title = lastQuery.Length > 40 ? lastQuery[..40] + "…" : lastQuery;
        return inst;
    }

    /// <summary>在已有任务上继续 lazy 拉取（等同后台多次「下一页」）。</summary>
    public static async Task<AssetReconPagedDataProvider?> TryAttachToExistingTaskAsync(
        IApiIndexService apiIndexService,
        ISqliteService sqliteService,
        string dbPath,
        string queryTaskId,
        IReadOnlyList<IReconSource> sources,
        string lastQuery,
        bool useUnified,
        UnifiedQuery unifiedQuery,
        int uiPageSize,
        int apiPageSize,
        int? maxStoredRows)
    {
        var activeSources = sources.ToList();
        if (activeSources.Count == 0)
            return null;

        var escapedTaskId = queryTaskId.Replace("'", "''");
        var countSql = $"SELECT COUNT(*) FROM \"asset_recon_results\" WHERE query_task_id = '{escapedTaskId}'";
        var countResult = await sqliteService.ExecuteQueryAsync(dbPath, countSql).ConfigureAwait(false);
        var storedCount = 0;
        if (countResult.Rows.Count > 0 && countResult.Rows[0].Count > 0 && countResult.Rows[0][0] != null)
            storedCount = Convert.ToInt32(countResult.Rows[0][0]);

        if (storedCount <= 0)
            return null;

        var fetched = activeSources.ToDictionary(s => s.Name, _ => new HashSet<int>());
        var nextApiPage = ComputeNextApiPage(storedCount, apiPageSize, activeSources.Count);
        for (var p = 1; p < nextApiPage; p++)
        {
            foreach (var source in activeSources)
                fetched[source.Name].Add(p);
        }

        var sourceTotals = new Dictionary<string, int>();
        var inst = new AssetReconPagedDataProvider(
            apiIndexService, sqliteService, dbPath, queryTaskId,
            activeSources, lastQuery, useUnified, unifiedQuery, uiPageSize,
            apiPageSize, maxStoredRows,
            fetched, sourceTotals, nextApiPage, storedCount, storedCount);

        inst.Title = lastQuery.Length > 40 ? lastQuery[..40] + "…" : lastQuery;
        return inst;
    }

    /// <summary>触发与 UI「下一页」相同的 lazy API 拉取，直至库中至少 <paramref name="requiredStoredCount"/> 行（受 maxStoredRows 限制）。</summary>
    public async Task PrefetchStoredCountAsync(int requiredStoredCount)
    {
        if (requiredStoredCount <= _storedResultCount)
            return;

        await _fetchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureStoredCountAtLeastAsync(requiredStoredCount).ConfigureAwait(false);
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private static int ComputeNextApiPage(int storedCount, int apiPageSize, int sourceCount)
    {
        if (storedCount <= 0)
            return 1;

        if (sourceCount <= 1)
            return Math.Max(1, (storedCount + apiPageSize - 1) / apiPageSize + 1);

        var batchCapacity = Math.Max(1, apiPageSize * sourceCount);
        return Math.Max(1, (storedCount + batchCapacity - 1) / batchCapacity + 1);
    }

    public string Title { get; private set; } = "";

    public string DatabasePath => _dbPath;

    public string QuerySource => "asset_recon_results";

    public IReadOnlyList<string> Columns => ColumnHeaders;

    public bool SupportsTotalCount => true;

    public bool CanEdit => false;

    public bool CanWriteBack => false;

    public bool IsDirty => false;

    public async Task<DataPageResult> GetPageAsync(int offset, int limit)
    {
        await _fetchLock.WaitAsync().ConfigureAwait(false);
        long totalSnapshot = 0;
        var pageResult = new DataPageResult
        {
            Rows = Array.Empty<IReadOnlyList<object?>>(),
            Offset = offset,
            Count = 0
        };
        try
        {
            await EnsureStoredCountAtLeastAsync(offset + limit).ConfigureAwait(false);

            var endOrder = offset + limit - 1;
            var cols = string.Join(", ", SqlColumns.Select(c => $"\"{c}\""));
            var sql = $"SELECT {cols} FROM \"asset_recon_results\" " +
                      $"WHERE query_task_id = '{_queryTaskId}' " +
                      $"AND sort_order >= {offset} AND sort_order <= {endOrder} " +
                      "ORDER BY sort_order";

            var result = await _sqliteService.ExecuteQueryPagedAsync(_dbPath, sql, 0, limit).ConfigureAwait(false);
            pageResult = new DataPageResult
            {
                Rows = result.Rows,
                Offset = offset,
                Count = (int)result.RowCount
            };
            totalSnapshot = _totalCount;
        }
        finally
        {
            _fetchLock.Release();
        }

        TotalCountEstimateChanged?.Invoke(this, new TotalCountEstimateChangedEventArgs { NewTotal = totalSnapshot });
        return pageResult;
    }

    public async Task<long> GetTotalCountAsync()
    {
        await _fetchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return _totalCount;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    public Task<int> UpdateRowAsync(long rowId, IReadOnlyList<object?> values) => Task.FromResult(0);

    public Task<int> InsertRowAsync(IReadOnlyList<object?> values) => Task.FromResult(0);

    public Task<int> DeleteRowAsync(long rowId) => Task.FromResult(0);

    public Task WriteBackAsync() => Task.CompletedTask;

    public async Task RefreshFromSourceAsync()
    {
        await _fetchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var escapedTaskId = _queryTaskId.Replace("'", "''");
            var sql = $"SELECT COUNT(*) FROM \"asset_recon_results\" WHERE query_task_id = '{escapedTaskId}'";
            var result = await _sqliteService.ExecuteQueryAsync(_dbPath, sql).ConfigureAwait(false);
            var dbCount = 0;
            if (result.Rows.Count > 0 && result.Rows[0].Count > 0 && result.Rows[0][0] != null)
                dbCount = Convert.ToInt32(result.Rows[0][0]);

            _storedResultCount = dbCount;
            _totalCount = dbCount;
            TotalCountEstimateChanged?.Invoke(this,
                new TotalCountEstimateChangedEventArgs { NewTotal = dbCount });
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fetchLock.Dispose();
    }

    private async Task EnsureStoredCountAtLeastAsync(int requiredCount)
    {
        if (_maxStoredRows is int cap)
            requiredCount = Math.Min(requiredCount, cap);

        while (_storedResultCount < requiredCount && !_remoteListsExhausted)
        {
            if (_maxStoredRows is int max && _storedResultCount >= max)
                return;

            var batch = await FetchMultiSourceBatchOnceAsync().ConfigureAwait(false);
            _nextApiPage = batch.NextApiPage;

            switch (batch.Outcome)
            {
                case FetchBatchOutcome.InsertedRows:
                    _storedResultCount = batch.NewStoredCount;
                    RefreshTotalCountUpperBound(_storedResultCount);
                    break;
                case FetchBatchOutcome.AdvanceApiPage:
                    break;
                case FetchBatchOutcome.RemoteExhausted:
                    RefreshTotalCountUpperBound(_storedResultCount);
                    _remoteListsExhausted = true;
                    _totalCount = Math.Max(_totalCount, _storedResultCount);
                    return;
            }
        }
    }

    private int DeclaredSourcesTotal() =>
        _sourceTotalCounts.Count == 0 ? 0 : _sourceTotalCounts.Values.Sum();

    private void RefreshTotalCountUpperBound(int storedFloor)
    {
        if (_remoteListsExhausted)
            return;
        var declared = DeclaredSourcesTotal();
        _totalCount = Math.Max(Math.Max(declared, storedFloor), _totalCount);
    }

    /// <summary>
    /// 若任一已上报 total 的情报源在指定 API 页之后仍可能有数据，则返回 true；
    /// 若某源 total 未知（0），视为仍可能有后续页，直至 <see cref="MaxApiPageIndex"/>。
    /// </summary>
    private bool AnySourceMayHavePageAfter(int apiPageJustFetched)
    {
        if (apiPageJustFetched >= MaxApiPageIndex)
            return false;

        foreach (var s in _activeSources)
        {
            if (!_sourceTotalCounts.TryGetValue(s.Name, out var total) || total <= 0)
                return true;

            var pages = Math.Max(1, (total + _apiPageSize - 1) / _apiPageSize);
            if (apiPageJustFetched < pages)
                return true;
        }

        return false;
    }

    private async Task<FetchBatchResult> FetchMultiSourceBatchOnceAsync()
    {
        var apiPage = _nextApiPage;
        var incrementedNext = _nextApiPage + 1;

        if (_activeSources.Count == 0)
        {
            return new FetchBatchResult(FetchBatchOutcome.RemoteExhausted, incrementedNext, _storedResultCount);
        }

        var pending = new List<Task<(string SourceName, List<UnifiedAssetRow> Rows)>>();
        foreach (var source in _activeSources)
        {
            if (_fetchedApiPagesBySource[source.Name].Contains(apiPage))
                continue;
            pending.Add(RunSingleSourceQueryAsync(source, apiPage));
        }

        if (pending.Count == 0)
        {
            if (apiPage >= MaxApiPageIndex)
                return new FetchBatchResult(FetchBatchOutcome.RemoteExhausted, incrementedNext, _storedResultCount);

            return new FetchBatchResult(FetchBatchOutcome.AdvanceApiPage, incrementedNext, _storedResultCount);
        }

        var baseStored = _storedResultCount;
        var running = baseStored;

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            var ix = pending.IndexOf(finished);
            pending.RemoveAt(ix);
            var (sourceName, rows) = await finished.ConfigureAwait(false);
            if (rows.Count == 0)
                continue;

            if (_maxStoredRows is int maxRows && running >= maxRows)
                continue;

            if (_maxStoredRows is int limit && running + rows.Count > limit)
                rows = rows.Take(limit - running).ToList();

            for (var i = 0; i < rows.Count; i++)
                rows[i].SortOrder = running + i;

            await _apiIndexService.IndexAssetReconAsync(
                _dbPath, _queryTaskId, sourceName, _lastQuery, rows,
                _sourceTotalCounts.GetValueOrDefault(sourceName, 0)).ConfigureAwait(false);

            running += rows.Count;
            RefreshTotalCountUpperBound(running);
        }

        if (running == baseStored)
        {
            var more = AnySourceMayHavePageAfter(apiPage);
            if (!more || apiPage >= MaxApiPageIndex)
                return new FetchBatchResult(FetchBatchOutcome.RemoteExhausted, incrementedNext, baseStored);

            return new FetchBatchResult(FetchBatchOutcome.AdvanceApiPage, incrementedNext, baseStored);
        }

        return new FetchBatchResult(FetchBatchOutcome.InsertedRows, incrementedNext, running);
    }

    private async Task<(string SourceName, List<UnifiedAssetRow> Rows)> RunSingleSourceQueryAsync(
        IReconSource source,
        int apiPage)
    {
        try
        {
            var translatedQuery = _useUnifiedQuery
                ? source.TranslateQuery(_unifiedQuery)
                : _lastQuery;
            var result = await source.QueryAsync(translatedQuery, apiPage, _apiPageSize).ConfigureAwait(false);
            _fetchedApiPagesBySource[source.Name].Add(apiPage);

            if (result.Success)
            {
                if (!_sourceTotalCounts.TryGetValue(source.Name, out var prev) || result.TotalCount > prev)
                    _sourceTotalCounts[source.Name] = result.TotalCount;
                return (source.Name, result.Rows);
            }

            if (!_sourceTotalCounts.ContainsKey(source.Name))
                _sourceTotalCounts[source.Name] = 0;
            return (source.Name, new List<UnifiedAssetRow>());
        }
        catch
        {
            if (!_sourceTotalCounts.ContainsKey(source.Name))
                _sourceTotalCounts[source.Name] = 0;
            if (!_fetchedApiPagesBySource[source.Name].Contains(apiPage))
                _fetchedApiPagesBySource[source.Name].Add(apiPage);
            return (source.Name, new List<UnifiedAssetRow>());
        }
    }

    private readonly struct FetchBatchResult
    {
        public FetchBatchResult(FetchBatchOutcome outcome, int nextApiPage, int newStoredCount)
        {
            Outcome = outcome;
            NextApiPage = nextApiPage;
            NewStoredCount = newStoredCount;
        }

        public FetchBatchOutcome Outcome { get; }
        public int NextApiPage { get; }
        public int NewStoredCount { get; }
    }

    private enum FetchBatchOutcome
    {
        /// <summary>至少写入一行，<see cref="FetchBatchResult.NewStoredCount"/> 为新的已存行数。</summary>
        InsertedRows,
        /// <summary>本回合未写入，但应继续尝试下一 API 页。</summary>
        AdvanceApiPage,
        /// <summary>远端已无可拉取页。</summary>
        RemoteExhausted
    }
}
