using System;

namespace ZeroFall.AiPanel.Models;

/// <summary>当前会话上下文 token 总量快照（落库；有值时 UI 优先于本地估算）。</summary>
public sealed class SessionTokenUsageState
{
    /// <summary>到当前最后一条消息为止的上下文 token 总量。</summary>
    public int PromptTokens { get; set; }

    /// <summary>true 表示该快照来自 API usage；false 表示来自本地估算。</summary>
    public bool IsApiMeasured { get; set; }

    public int? CachedPromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    /// <summary>记录用量时的消息条数（仅存档，不参与校验）。</summary>
    public int MessageCount { get; set; }

    public string? ModelId { get; set; }

    public string? CapturedAtUtc { get; set; }

    public bool TryGetUsablePromptTokens(string? modelId, out int tokens)
    {
        tokens = PromptTokens;
        return tokens > 0
            && !string.IsNullOrWhiteSpace(ModelId)
            && !string.IsNullOrWhiteSpace(modelId)
            && string.Equals(ModelId, modelId, StringComparison.OrdinalIgnoreCase);
    }
}
