using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.AssetRecon.Services;
using ZeroFall.Base.AiTools;

namespace ZeroFall.AssetRecon.Tools;

public sealed class AssetReconAiToolService
{
    private readonly AssetReconQueryService _queryService;

    public AssetReconAiToolService(AssetReconQueryService queryService) => _queryService = queryService;

    [AiTool(
        "asset_recon",
        """
        资产测绘统一工具。action=query 发起付费资产测绘；action=more 在已有 queryTaskId 上继续拉取更多行。
        调用各平台 API，按条数消耗积分。
        执行前会弹窗让用户确认预计积分；无需 UI 反馈，结果写入 .zerofall.db 的 asset_recon_results。
        分析请用 sql：WHERE query_task_id='…' ORDER BY sort_order。
        若库中已有同任务数据，优先 sql；只有用户明确要求新查或继续拉取时才调用本工具。
        maxRows 为本次最多入库条数（默认 20，上限 500）；多源时积分约 maxRows×源数量。
        """)]
    public async Task<string> AssetReconAsync(
        [ToolParam("query|more")] string action,
        [ToolParam("FOFA/Hunter/Quake 查询语句，action=query 必填", Required = false)] string? query = null,
        [ToolParam("测绘任务 Id（query_task_id），action=more 必填", Required = false)] string? queryTaskId = null,
        [ToolParam("情报源：aggregate|fofa|hunter|quake，默认 aggregate", Required = false)] string source = "aggregate",
        [ToolParam("最多入库条数，默认 20", Required = false)] int maxRows = AssetReconQueryService.DefaultAiMaxRows)
    {
        var act = (action ?? string.Empty).Trim().ToLowerInvariant();
        return act switch
        {
            "query" => await QueryAsync(query ?? string.Empty, source, maxRows).ConfigureAwait(false),
            "more" => await FetchMoreAsync(queryTaskId ?? string.Empty, maxRows, source).ConfigureAwait(false),
            _ => ToolResultJson.Error("action 必须是 query 或 more")
        };
    }

    public async Task<string> QueryAsync(
        string query,
        string source = "aggregate",
        int maxRows = AssetReconQueryService.DefaultAiMaxRows)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ToolResultJson.Error("query 不能为空");

        if (!AssetReconSourceFactory.TryParseSourceMode(source, out var sourceMode, out var parseError))
            return ToolResultJson.Error(parseError!);

        maxRows = Math.Clamp(maxRows, 1, AssetReconQueryService.MaxAiMaxRows);

        var result = await _queryService.ExecuteAsync(new AssetReconQueryRequest
        {
            Query = query,
            SourceModeIndex = sourceMode,
            MaxStoredRows = maxRows,
            SyncUi = false,
            RequireUserConfirm = true
        }).ConfigureAwait(false);

        return ToToolJson(result);
    }

    public async Task<string> FetchMoreAsync(
        string queryTaskId,
        int maxRows = AssetReconQueryService.DefaultAiMaxRows,
        string source = "aggregate")
    {
        if (string.IsNullOrWhiteSpace(queryTaskId))
            return ToolResultJson.Error("queryTaskId 不能为空");

        if (!AssetReconSourceFactory.TryParseSourceMode(source, out var sourceMode, out var parseError))
            return ToolResultJson.Error(parseError!);

        maxRows = Math.Clamp(maxRows, 1, AssetReconQueryService.MaxAiMaxRows);

        var result = await _queryService.ExecuteAsync(new AssetReconQueryRequest
        {
            Query = string.Empty,
            SourceModeIndex = sourceMode,
            MaxStoredRows = maxRows,
            ExistingQueryTaskId = queryTaskId.Trim(),
            SyncUi = false,
            RequireUserConfirm = true
        }).ConfigureAwait(false);

        return ToToolJson(result);
    }

    private static string ToToolJson(AssetReconQueryResult result)
    {
        if (result.Cancelled)
        {
            return ToolResultJson.Data(o =>
            {
                o["cancelled"] = true;
                o["message"] = "用户已取消测绘";
            });
        }

        if (!result.Success)
            return ToolResultJson.Error(result.Error ?? "测绘失败");

        return ToolResultJson.Data(o =>
        {
            o["queryTaskId"] = result.QueryTaskId;
            o["query"] = result.Query;
            o["storedCount"] = result.StoredCount;
            o["totalEstimate"] = result.TotalEstimate;
            o["estimatedCredits"] = result.EstimatedCredits;
            o["hint"] = $"sql 示例: SELECT ip,port,title,source FROM asset_recon_results WHERE query_task_id='{result.QueryTaskId}' ORDER BY sort_order LIMIT 50";
        });
    }
}
