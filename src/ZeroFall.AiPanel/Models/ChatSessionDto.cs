using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ZeroFall.AiPanel.Models;

public class ChatSessionDto
{
    [JsonPropertyName("messages")]
    public List<ChatMessageDto> Messages { get; set; } = [];
}

/// <summary>落盘一行 = 一条逻辑消息。</summary>
public class ChatMessageDto
{
    public const string TypeText = "text";
    public const string TypeThinking = "thinking";
    public const string TypeBody = "body";
    public const string TypeTool = "tool";

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("type")]
    public string Type { get; set; } = TypeText;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("reasoningContent")]
    public string ReasoningContent { get; set; } = string.Empty;

    [JsonPropertyName("contentHtml")]
    public string? ContentHtml { get; set; }

    [JsonPropertyName("contextTokenCount")]
    public int ContextTokenCount { get; set; }

    [JsonPropertyName("visual")]
    public ChatMessageVisual Visual { get; set; } = ChatMessageVisual.Visible;

    [JsonIgnore]
    public string EffectiveType => NormalizeType();

    public string NormalizeType()
    {
        if (!string.IsNullOrWhiteSpace(Type))
            return Type.Trim().ToLowerInvariant();

        return string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase) ? TypeText : TypeBody;
    }

    public string ResolveContent() => Content;
}
