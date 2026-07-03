using System;

namespace ZeroFall.AssetRecon.Services;

public static class AssetReconCostEstimator
{
    /// <summary>按「约 1 条结果 ≈ 1 积分/源」估算（实际以各平台扣费为准）。</summary>
    public static int EstimateCredits(int maxRows, int activeSourceCount) =>
        Math.Max(0, maxRows) * Math.Max(1, activeSourceCount);

    public static string BuildConfirmSummary(int maxRows, int activeSourceCount, int estimatedCredits)
    {
        var sourceHint = activeSourceCount switch
        {
            1 => "单情报源",
            _ => $"{activeSourceCount} 个情报源（多源时积分按源累加）"
        };

        return
            "资产测绘会调用 FOFA / Hunter / Quake 等付费 API，按返回条数消耗积分。\n\n" +
            $"计划入库：最多 {maxRows} 条（{sourceHint}）\n" +
            $"预计消费：约 {estimatedCredits} 积分\n\n" +
            "说明：此为估算，实际扣费以各平台为准；结果写入项目 .zerofall.db，可用 sql 工具分析。";
    }
}
