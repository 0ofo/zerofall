using System;
using System.Collections.Generic;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Services;

/// <summary>按 token 预算裁剪聊天历史（保留 system + 最近可放入预算的消息）。</summary>
internal static class ChatContextCompressor
{
    private const double InputBudgetRatio = 0.72;

    public static int ComputeStartIndex(
        IReadOnlyList<ChatMessage> messages,
        string systemPrompt,
        string modelId,
        int? modelContextTokens)
    {
        if (modelContextTokens is not int window || window <= 0)
            return 0;

        var budget = Math.Max(512, (int)(window * InputBudgetRatio));
        budget -= ChatMessageTokenEstimator.GetSystemBudgetTokens(systemPrompt, modelId);
        if (budget <= 0)
            return Math.Max(0, messages.Count - 1);

        var total = 0;
        var start = messages.Count;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (ChatApiErrorHelper.IsUiOnlyAssistantMessage(m))
                continue;

            var cost = ChatMessageTokenEstimator.GetOrComputeMessageApiTokens(m, modelId);
            if (start < messages.Count && total + cost > budget)
                break;

            total += cost;
            start = i;
        }

        return start;
    }
}
