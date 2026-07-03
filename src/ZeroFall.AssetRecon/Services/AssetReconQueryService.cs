using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.AssetRecon.Providers;
using ZeroFall.AssetRecon.Services;
using ZeroFall.Platform.Services;

namespace ZeroFall.AssetRecon.Services;

public sealed class AssetReconQueryRequest
{
    public required string Query { get; init; }

    /// <summary>0 聚合，1 FOFA，2 Hunter，3 Quake。</summary>
    public int SourceModeIndex { get; init; }

    /// <summary>最多写入 SQLite 的行数（付费上界）。UI 手动查询时为 null 表示不限制 lazy 拉取。</summary>
    public int? MaxStoredRows { get; init; }

    public bool SyncUi { get; init; }

    /// <summary>为 true 时经 <see cref="IReconPaidOperationGate"/> 弹窗确认（AI 路径）。</summary>
    public bool RequireUserConfirm { get; init; }

    /// <summary>继续拉取已有任务时使用；为 null 则新建任务。</summary>
    public string? ExistingQueryTaskId { get; init; }
}

public sealed class AssetReconQueryResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string? Error { get; init; }
    public string QueryTaskId { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public int StoredCount { get; init; }
    public long TotalEstimate { get; init; }
    public int EstimatedCredits { get; init; }
    public AssetReconPagedDataProvider? Provider { get; init; }
}

public sealed class AssetReconQueryService
{
    public const int UserResultsPageSize = 20;
    public const int AggregatedPerSourceApiPageSize = 10;
    public const int DefaultAiMaxRows = 20;
    public const int MaxAiMaxRows = 500;

    private readonly ISettingsService _settingsService;
    private readonly IApiIndexService _apiIndexService;
    private readonly ISqliteService _sqliteService;
    private readonly IWorkspaceService _workspaceService;
    private readonly IOutboundHttpClientFactory _httpClientFactory;
    private readonly IReconPaidOperationGate? _paidOperationGate;

    public AssetReconQueryService(
        ISettingsService settingsService,
        IApiIndexService apiIndexService,
        ISqliteService sqliteService,
        IWorkspaceService workspaceService,
        IOutboundHttpClientFactory httpClientFactory,
        IReconPaidOperationGate? paidOperationGate = null)
    {
        _settingsService = settingsService;
        _apiIndexService = apiIndexService;
        _sqliteService = sqliteService;
        _workspaceService = workspaceService;
        _httpClientFactory = httpClientFactory;
        _paidOperationGate = paidOperationGate;
    }

    public async Task<AssetReconQueryResult> ExecuteAsync(
        AssetReconQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query) && string.IsNullOrWhiteSpace(request.ExistingQueryTaskId))
            return Fail("查询内容不能为空");

        var dbPath = _workspaceService.GetDatabasePath();
        if (string.IsNullOrEmpty(dbPath))
            return Fail("工作区未打开，无法写入 .zerofall.db");

        var config = _settingsService.Load().AssetRecon;
        var sources = AssetReconSourceFactory.CreateSources(config, _httpClientFactory, request.SourceModeIndex);
        if (sources.Count == 0)
            return Fail("请先在设置中启用并配置至少一个情报源");

        var lastQuery = request.Query.Trim();
        var unified = QueryParser.Parse(lastQuery);
        var useUnified = sources.Count > 1
                         || unified.Expression != null
                         || unified.Fields.Count > 0;
        var apiPageSize = useUnified ? AggregatedPerSourceApiPageSize : UserResultsPageSize;

        var maxStored = request.MaxStoredRows;
        if (maxStored is > MaxAiMaxRows)
            maxStored = MaxAiMaxRows;

        var additionalRows = maxStored;
        var isContinuation = !string.IsNullOrWhiteSpace(request.ExistingQueryTaskId);

        if (isContinuation)
        {
            var escaped = request.ExistingQueryTaskId!.Replace("'", "''");
            var metaSql =
                $"SELECT query FROM \"asset_recon_results\" WHERE query_task_id = '{escaped}' LIMIT 1";
            var meta = await _sqliteService.ExecuteQueryAsync(dbPath, metaSql).ConfigureAwait(false);
            if (meta.Rows.Count == 0 || meta.Rows[0].Count == 0 || meta.Rows[0][0] is null)
                return Fail($"未找到任务 {request.ExistingQueryTaskId}");

            lastQuery = meta.Rows[0][0]?.ToString() ?? lastQuery;

            var countSql = $"SELECT COUNT(*) FROM \"asset_recon_results\" WHERE query_task_id = '{escaped}'";
            var countRes = await _sqliteService.ExecuteQueryAsync(dbPath, countSql).ConfigureAwait(false);
            var currentStored = 0;
            if (countRes.Rows.Count > 0 && countRes.Rows[0][0] != null)
                currentStored = Convert.ToInt32(countRes.Rows[0][0]);

            if (maxStored is null or <= 0)
                return Fail("继续拉取需要指定 maxRows（新增入库行数上限）");

            maxStored = currentStored + maxStored.Value;
            additionalRows = maxStored.Value - currentStored;
        }
        else if (request.RequireUserConfirm && maxStored is null)
        {
            maxStored = DefaultAiMaxRows;
            additionalRows = maxStored;
        }

        if (request.RequireUserConfirm)
        {
            if (_paidOperationGate == null)
                return Fail("付费确认门未就绪，无法发起测绘");

            var credits = AssetReconCostEstimator.EstimateCredits(additionalRows ?? maxStored ?? DefaultAiMaxRows,
                sources.Count);
            var summary = isContinuation
                ? AssetReconCostEstimator.BuildConfirmSummary(
                      additionalRows ?? 0,
                      sources.Count,
                      credits) + $"\n\n（在任务 {request.ExistingQueryTaskId} 上追加拉取）"
                : AssetReconCostEstimator.BuildConfirmSummary(
                      maxStored ?? DefaultAiMaxRows,
                      sources.Count,
                      credits);

            var ok = await _paidOperationGate.ConfirmAsync(
                summary,
                lastQuery,
                cancellationToken).ConfigureAwait(false);

            if (!ok)
                return new AssetReconQueryResult { Cancelled = true, Query = lastQuery };
        }

        AssetReconPagedDataProvider? provider;

        if (isContinuation)
        {
            provider = await AssetReconPagedDataProvider.TryAttachToExistingTaskAsync(
                _apiIndexService, _sqliteService, dbPath,
                request.ExistingQueryTaskId!.Trim(),
                sources, lastQuery, useUnified, unified,
                UserResultsPageSize, apiPageSize, maxStored).ConfigureAwait(false);

            if (provider == null)
                return Fail("无法继续该任务（可能已被删除）");

            await provider.PrefetchStoredCountAsync(maxStored!.Value).ConfigureAwait(false);
        }
        else
        {
            var queryTaskId = Guid.NewGuid().ToString("N")[..16];
            provider = await AssetReconPagedDataProvider.TryCreateAfterFirstBatchAsync(
                _apiIndexService, _sqliteService, dbPath, queryTaskId,
                sources, lastQuery, useUnified, unified,
                UserResultsPageSize, apiPageSize, maxStored).ConfigureAwait(false);

            if (provider == null)
                return Fail("无结果");

            if (maxStored is int cap && cap > provider.PageSize)
                await provider.PrefetchStoredCountAsync(cap).ConfigureAwait(false);
        }

        var stored = await CountStoredAsync(dbPath, provider.QueryTaskId).ConfigureAwait(false);
        var total = await provider.GetTotalCountAsync().ConfigureAwait(false);
        var estimated = AssetReconCostEstimator.EstimateCredits(stored, sources.Count);

        var queryResult = new AssetReconQueryResult
        {
            Success = true,
            QueryTaskId = provider.QueryTaskId,
            Query = lastQuery,
            StoredCount = stored,
            TotalEstimate = total,
            EstimatedCredits = estimated,
            Provider = request.SyncUi ? provider : null
        };

        if (!request.SyncUi)
            provider.Dispose();

        return queryResult;
    }

    private async Task<int> CountStoredAsync(string dbPath, string queryTaskId)
    {
        var escaped = queryTaskId.Replace("'", "''");
        var sql = $"SELECT COUNT(*) FROM \"asset_recon_results\" WHERE query_task_id = '{escaped}'";
        var result = await _sqliteService.ExecuteQueryAsync(dbPath, sql).ConfigureAwait(false);
        if (result.Rows.Count == 0 || result.Rows[0].Count == 0 || result.Rows[0][0] == null)
            return 0;
        return Convert.ToInt32(result.Rows[0][0]);
    }

    private static AssetReconQueryResult Fail(string error) =>
        new() { Error = error };
}
