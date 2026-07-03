using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ZeroFall.AiPanel.Models;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Services;

/// <summary>统一管理 AI 聊天上下文 token 口径：始终按实际 API messages 载荷计算。</summary>
public sealed class ChatContextUsageService
{
    public int ResolveContextTokenLimit(AiSettings config, string modelId, out string limitSource)
    {
        var entry = AiEndpointCatalog.FindModel(config, config.ApiBaseUrl, modelId);
        if (entry?.ContextTokens is int explicitLimit and > 0)
        {
            limitSource = AiModelContextHints.Infer(modelId) == explicitLimit ? "推断（已保存）" : "模型目录";
            return explicitLimit;
        }

        if (AiModelContextHints.Infer(modelId) is int inferredLimit)
        {
            limitSource = "推断";
            return inferredLimit;
        }

        limitSource = "默认";
        return AiEndpointCatalog.ResolveContextTokens(config, config.ApiBaseUrl, modelId);
    }

    public SessionTokenUsageState? CreateSnapshot(TokenUsageSnapshotRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelId))
            return CloneTokenUsageState(request.Fallback);

        var lastMessageTokenCount = request.Messages.LastOrDefault()?.ContextTokenCount ?? 0;
        if (lastMessageTokenCount > 0)
        {
            return new SessionTokenUsageState
            {
                PromptTokens = lastMessageTokenCount,
                IsApiMeasured = request.Fallback is { IsApiMeasured: true }
                    && request.Fallback.MessageCount == request.Messages.Count,
                MessageCount = request.Messages.Count,
                ModelId = request.ModelId,
                CapturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        var estimate = ChatContextCompressionService.EstimateApiContextTokens(
            request.Messages,
            request.SystemPrompt,
            request.ModelId,
            request.ContextLimit,
            request.ApiStartMessageId).UsedTokens;
        if (estimate <= 0)
            return null;

        return new SessionTokenUsageState
        {
            PromptTokens = estimate,
            IsApiMeasured = false,
            MessageCount = request.Messages.Count,
            ModelId = request.ModelId,
            CapturedAtUtc = DateTime.UtcNow.ToString("O")
        };
    }

    public TokenUsageUiResult ComputeUiResult(TokenUsageUiState state)
    {
        var usageSource = ResolveUsageSource(state, out var used);
        var included = ChatContextCompressionService.CountIncludedApiMessages(
            state.Messages,
            state.SystemPrompt,
            state.ModelId,
            state.Limit,
            state.ApiStartMessageId);
        var rawPercent = state.Limit > 0 ? 100.0 * used / state.Limit : 0.0;
        var percent = Math.Min(100.0, rawPercent);
        var skip = ChatContextCompressionService.GetUncompressedApiSkipIndex(
            state.Messages,
            state.SystemPrompt,
            state.ModelId,
            state.Limit,
            state.ApiStartMessageId);
        var canCompress = ChatContextCompressionService.HasCompressibleApiHistory(
            state.Messages,
            state.SystemPrompt,
            state.ModelId,
            state.Limit,
            state.ApiStartMessageId);

        var details = new StringBuilder();
        details.AppendLine(CultureInfo.InvariantCulture, $"上下文总量：{state.Limit:N0} tokens（{state.LimitSource}）");
        if (usageSource == TokenUsageValueSource.ApiMeasured)
        {
            details.AppendLine(CultureInfo.InvariantCulture, $"已用 tokens：{used:N0}（API 返回值快照）");
            var cached = state.StoredTokenUsage?.CachedPromptTokens ?? state.LastApiCachedPromptTokens;
            if (cached is int cachedTokens and > 0)
            {
                details.AppendLine(CultureInfo.InvariantCulture,
                    $"缓存命中：{cachedTokens:N0} tokens（prompt_cache_hit_tokens）");
            }

        }
        else
        {
            var estimateNote = AiChatTokenCounter.UsesDeepSeekCharEstimate(state.ModelId)
                ? "本地估算（DeepSeek 字符换算）"
                : "本地估算";
            var sourceNote = usageSource == TokenUsageValueSource.StoredEstimate
                ? $"消息快照，{estimateNote}"
                : estimateNote;
            details.AppendLine(CultureInfo.InvariantCulture, $"已用 tokens：{used:N0}（{sourceNote}）");
            if (usageSource == TokenUsageValueSource.LocalEstimate && state.LastApiRoundCompleted)
            {
                details.AppendLine(state.LastRequestIncludedUsage
                    ? "上次请求已成功，但响应中无可用 token usage（请确认 API 支持 include_usage）"
                    : "上次请求已成功，但当前 API 地址已降级为不请求 usage（此前 stream_options 被拒绝）");
            }
        }

        if (state.Limit > 0)
        {
            details.AppendLine(CultureInfo.InvariantCulture, $"剩余可用：{Math.Max(0, state.Limit - used):N0} tokens");
            details.AppendLine(CultureInfo.InvariantCulture,
                $"占用比例：{rawPercent.ToString("F1", CultureInfo.InvariantCulture)}%");
            var thresholdPercent = ChatContextCompressionService.NormalizeCompressionThresholdPercent(
                state.CompressionThresholdPercent);
            if (rawPercent >= thresholdPercent)
            {
                if (state.IsCompressingContext)
                    details.AppendLine("正在摘要压缩较早对话…");
                else if (canCompress)
                    details.AppendLine(CultureInfo.InvariantCulture,
                        $"占用达 {thresholdPercent}% 时将在本轮对话结束后自动摘要压缩");
                else if (state.ApiStartMessageId > 0)
                    details.AppendLine("已摘要压缩；继续对话后占用再次达标时可二次压缩");
                else
                    details.AppendLine("暂无可摘要的对话内容");
            }
        }

        details.AppendLine(CultureInfo.InvariantCulture, $"送入 API 消息：{included} 条");
        if (state.ApiStartMessageId > 0)
        {
            details.AppendLine(CultureInfo.InvariantCulture,
                $"已摘要压缩：API 从 message id {state.ApiStartMessageId} 起");
        }
        else if (skip > 0)
        {
            details.AppendLine(CultureInfo.InvariantCulture,
                $"因 token 预算暂省略前 {skip} 条（仅统计当前 API 载荷）");
        }

        details.AppendLine(CultureInfo.InvariantCulture, $"会话消息总数：{state.Messages.Count} 条（UI 当前展示）");
        details.AppendLine(CultureInfo.InvariantCulture, $"模型：{state.ModelId}");
        return new TokenUsageUiResult(percent, details.ToString().TrimEnd());
    }

    public static SessionTokenUsageState? CloneTokenUsageState(SessionTokenUsageState? source)
    {
        if (source is null)
            return null;

        return new SessionTokenUsageState
        {
            PromptTokens = source.PromptTokens,
            IsApiMeasured = source.IsApiMeasured,
            CachedPromptTokens = source.CachedPromptTokens,
            CompletionTokens = source.CompletionTokens,
            MessageCount = source.MessageCount,
            ModelId = source.ModelId,
            CapturedAtUtc = source.CapturedAtUtc
        };
    }

    private static TokenUsageValueSource ResolveUsageSource(
        TokenUsageUiState state,
        out int used)
    {
        var lastMessageTokenCount = state.Messages.LastOrDefault()?.ContextTokenCount ?? 0;
        if (lastMessageTokenCount > 0)
        {
            used = lastMessageTokenCount;
            return state.StoredTokenUsage is { IsApiMeasured: true }
                   && state.StoredTokenUsage.MessageCount == state.Messages.Count
                ? TokenUsageValueSource.ApiMeasured
                : TokenUsageValueSource.StoredEstimate;
        }

        if (state.StoredTokenUsage is { } stored
            && stored.TryGetUsablePromptTokens(state.ModelId, out used))
        {
            return stored.IsApiMeasured ? TokenUsageValueSource.ApiMeasured : TokenUsageValueSource.StoredEstimate;
        }

        if (state.LastApiPromptTokens is int prompt and > 0)
        {
            used = prompt;
            return TokenUsageValueSource.ApiMeasured;
        }

        used = ChatContextCompressionService.EstimateUsedApiTokens(
            state.Messages,
            state.SystemPrompt,
            state.ModelId,
            state.Limit,
            state.ApiStartMessageId);

        return TokenUsageValueSource.LocalEstimate;
    }

    private enum TokenUsageValueSource
    {
        ApiMeasured,
        StoredEstimate,
        LocalEstimate
    }
}

public sealed record TokenUsageSnapshotRequest(
    IReadOnlyList<ChatMessage> Messages,
    string ModelId,
    int ContextLimit,
    string SystemPrompt,
    long ApiStartMessageId,
    SessionTokenUsageState? Fallback);

public sealed record TokenUsageUiState(
    IReadOnlyList<ChatMessage> Messages,
    string ModelId,
    int Limit,
    string LimitSource,
    long ApiStartMessageId,
    bool IsCompressingContext,
    string SystemPrompt,
    int CompressionThresholdPercent,
    SessionTokenUsageState? StoredTokenUsage,
    int? LastApiPromptTokens,
    int? LastApiCachedPromptTokens,
    int? LastApiCompletionTokens,
    bool LastApiRoundCompleted,
    bool LastRequestIncludedUsage);

public sealed record TokenUsageUiResult(double Percent, string Details);
