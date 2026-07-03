using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace ZeroFall.Platform.Services;

/// <summary>常见模型上下文窗口回退（API /models 未带 context 字段时使用）。</summary>
public static class AiModelContextHints
{
    /// <summary>无法推断且未手动填写时的默认上下文窗口。</summary>
    public const int DefaultContextTokens = 100_000;

    private static readonly FrozenDictionary<string, int> Exact = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o"] = 128_000,
        ["gpt-4o-mini"] = 128_000,
        ["gpt-4o-2024-08-06"] = 128_000,
        ["gpt-4-turbo"] = 128_000,
        ["gpt-4-turbo-preview"] = 128_000,
        ["gpt-4"] = 8_192,
        ["gpt-4-0613"] = 8_192,
        ["gpt-3.5-turbo"] = 16_385,
        ["gpt-3.5-turbo-16k"] = 16_385,
        ["o1"] = 200_000,
        ["o1-mini"] = 128_000,
        ["o1-preview"] = 128_000,
        ["o3-mini"] = 200_000,
        ["deepseek-chat"] = 64_000,
        ["deepseek-reasoner"] = 64_000,
        ["claude-3-5-sonnet-20241022"] = 200_000,
        ["claude-3-5-sonnet-latest"] = 200_000,
        ["claude-3-opus-20240229"] = 200_000,
        ["claude-3-haiku-20240307"] = 200_000,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static int? Infer(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var id = modelId.Trim();
        if (Exact.TryGetValue(id, out var exact))
            return exact;

        var lower = id.ToLowerInvariant();
        if (lower.Contains("128k", StringComparison.Ordinal))
            return 128_000;
        if (lower.Contains("32k", StringComparison.Ordinal))
            return 32_768;
        if (lower.Contains("16k", StringComparison.Ordinal))
            return 16_384;
        if (lower.Contains("8k", StringComparison.Ordinal))
            return 8_192;
        if (lower.StartsWith("gpt-4o", StringComparison.Ordinal))
            return 128_000;
        if (lower.StartsWith("gpt-4", StringComparison.Ordinal))
            return 128_000;
        if (lower.StartsWith("gpt-3.5", StringComparison.Ordinal))
            return 16_385;
        if (lower.StartsWith("o1", StringComparison.Ordinal) || lower.StartsWith("o3", StringComparison.Ordinal))
            return 200_000;

        return null;
    }

    /// <summary>推断上下文窗口；未知模型回退 <see cref="DefaultContextTokens"/>。</summary>
    public static int Resolve(string? modelId) => Infer(modelId) ?? DefaultContextTokens;
}
