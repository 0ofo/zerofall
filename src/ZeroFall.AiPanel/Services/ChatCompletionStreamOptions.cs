using System;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace ZeroFall.AiPanel.Services;

/// <summary>流式 chat/completions 的 <c>stream_options.include_usage</c>：能开则开，不支持则自动降级。</summary>
public static class ChatCompletionStreamOptions
{
    /// <value>false 表示该 Base URL 已确认不支持 include_usage。</value>
    private static readonly ConcurrentDictionary<string, bool> IncludeUsageByBaseUrl = new(StringComparer.OrdinalIgnoreCase);

    public static void Apply(JsonObject requestBody, string apiBaseUrl)
    {
        requestBody["stream"] = true;
        if (ShouldIncludeUsage(apiBaseUrl))
            requestBody["stream_options"] = new JsonObject { ["include_usage"] = true };
        else
            requestBody.Remove("stream_options");
    }

    public static bool ShouldIncludeUsage(string apiBaseUrl)
    {
        var key = NormalizeBaseUrl(apiBaseUrl);
        return !IncludeUsageByBaseUrl.TryGetValue(key, out var include) || include;
    }

    /// <summary>若错误像 stream_options 不被接受，标记该 Base URL 并返回 true（调用方应去掉该字段重试）。</summary>
    public static bool TryDisableUsageForBaseUrl(string apiBaseUrl, int statusCode, string? errorBody)
    {
        if (!ShouldIncludeUsage(apiBaseUrl))
            return false;

        if (!IsLikelyUnsupportedStreamOptionsError(statusCode, errorBody))
            return false;

        IncludeUsageByBaseUrl[NormalizeBaseUrl(apiBaseUrl)] = false;
        return true;
    }

    private static bool IsLikelyUnsupportedStreamOptionsError(int statusCode, string? body)
    {
        if (statusCode is not (400 or 422))
            return false;

        if (string.IsNullOrWhiteSpace(body))
            return false;

        var lower = body.ToLowerInvariant();
        return lower.Contains("stream_options", StringComparison.Ordinal)
               || lower.Contains("include_usage", StringComparison.Ordinal)
               || (lower.Contains("unrecognized", StringComparison.Ordinal)
                   && lower.Contains("stream", StringComparison.Ordinal));
    }

    private static string NormalizeBaseUrl(string apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            return string.Empty;

        return apiBaseUrl.Trim().TrimEnd('/');
    }
}
