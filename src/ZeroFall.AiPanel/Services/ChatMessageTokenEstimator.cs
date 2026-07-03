using System;
using ZeroFall.AiPanel.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Services;

/// <summary>单条消息的 API token 估算与缓存；汇总时按条叠加，避免每次全量重算 JSON。</summary>
public static class ChatMessageTokenEstimator
{
    private static string? _cachedSystemPrompt;
    private static string? _cachedSystemModelId;
    private static int _cachedSystemApiTokens = -1;

    public static void InvalidateSystemTokenCache()
    {
        _cachedSystemApiTokens = -1;
        _cachedSystemPrompt = null;
        _cachedSystemModelId = null;
    }

    /// <summary>与 <see cref="ChatContextCompressionService"/> 中 system 消息节点估算一致。</summary>
    public static int GetOrComputeSystemApiTokens(string systemPrompt, string modelId)
    {
        if (_cachedSystemApiTokens >= 0
            && _cachedSystemPrompt == systemPrompt
            && string.Equals(_cachedSystemModelId, modelId, StringComparison.OrdinalIgnoreCase))
            return _cachedSystemApiTokens;

        _cachedSystemApiTokens = AiChatTokenCounter.EstimateMessageTokens(new ChatMessageTokenPart
        {
            Role = "system",
            Content = systemPrompt
        }, modelId);
        _cachedSystemPrompt = systemPrompt;
        _cachedSystemModelId = modelId;
        return _cachedSystemApiTokens;
    }

    /// <summary>system 提示在裁剪预算中的扣减（与 <see cref="ChatContextCompressor.ComputeStartIndex"/> 一致）。</summary>
    public static int GetSystemBudgetTokens(string systemPrompt, string modelId)
        => AiChatTokenCounter.CountText(systemPrompt, modelId);

    public static int GetOrComputeMessageApiTokens(ChatMessage message, string modelId)
    {
        if (message.TryGetCachedApiTokens(modelId, out var cached))
            return cached;

        var tokens = ComputeMessageApiTokens(message, modelId);
        message.SetCachedApiTokens(modelId, tokens);
        return tokens;
    }

    public static int ComputeMessageApiTokens(ChatMessage message, string modelId)
    {
        if (message.HasToolCall)
        {
            var args = !string.IsNullOrEmpty(message.ToolArgumentsJson)
                ? message.ToolArgumentsJson
                : message.ToolCommand ?? "{}";
            var toolTokens = AiChatTokenCounter.EstimateMessageTokens(new ChatMessageTokenPart
            {
                Role = "assistant",
                ToolName = message.ToolName,
                ToolArgumentsJson = args,
                ReasoningContent = message.ReasoningContent
            }, modelId);

            if (message.IsToolRunning)
                return toolTokens;

            var outputForApi = ToolResultContextProjection.ProjectForApi(
                message.ToolOutput,
                modelId,
                message.Id > 0
                    ? ChatContextCompressionService.BuildToolResultPath(message.Id)
                    : "@tool_result:pending");

            return toolTokens + AiChatTokenCounter.EstimateMessageTokens(new ChatMessageTokenPart
            {
                Role = "tool",
                ToolOutput = outputForApi
            }, modelId);
        }

        var role = message.Role switch
        {
            ChatRole.User => "user",
            ChatRole.System => "system",
            _ => "assistant"
        };

        return AiChatTokenCounter.EstimateMessageTokens(new ChatMessageTokenPart
        {
            Role = role,
            Content = message.Content,
            ReasoningContent = message.ReasoningContent
        }, modelId);
    }
}
