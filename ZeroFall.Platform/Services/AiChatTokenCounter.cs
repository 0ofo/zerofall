using System;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.ML.Tokenizers;

namespace ZeroFall.Platform.Services;

/// <summary>基于 Microsoft.ML.Tokenizers（AOT 友好）的聊天 token 估算。</summary>
public static class AiChatTokenCounter
{
    private const int MessageOverheadTokens = 4;
    private static readonly ConcurrentDictionary<string, TiktokenTokenizer> Tokenizers = new(StringComparer.OrdinalIgnoreCase);

    public static int CountText(string? text, string? modelId)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return ResolveTokenizer(modelId).CountTokens(text);
    }

    public static int EstimateMessageTokens(ChatMessageTokenPart message, string? modelId)
    {
        var tokens = MessageOverheadTokens;
        if (!string.IsNullOrEmpty(message.Role))
            tokens += CountText(message.Role, modelId);
        if (!string.IsNullOrEmpty(message.Content))
            tokens += CountText(message.Content, modelId);
        if (!string.IsNullOrEmpty(message.ReasoningContent))
            tokens += CountText(message.ReasoningContent, modelId);
        if (!string.IsNullOrEmpty(message.ToolName))
            tokens += CountText(message.ToolName, modelId);
        if (!string.IsNullOrEmpty(message.ToolArgumentsJson))
            tokens += CountText(message.ToolArgumentsJson, modelId);
        if (!string.IsNullOrEmpty(message.ToolOutput))
            tokens += CountText(message.ToolOutput, modelId);
        return tokens;
    }

    public static int EstimateJsonNodeTokens(JsonNode? node, string? modelId)
    {
        if (node is null)
            return 0;

        return CountText(node.ToJsonString(), modelId);
    }

    private static TiktokenTokenizer ResolveTokenizer(string? modelId)
    {
        var key = string.IsNullOrWhiteSpace(modelId) ? "gpt-4o-mini" : modelId.Trim();
        return Tokenizers.GetOrAdd(key, static model =>
        {
            try
            {
                return TiktokenTokenizer.CreateForModel(model);
            }
            catch
            {
                try
                {
                    return TiktokenTokenizer.CreateForModel("gpt-4o-mini");
                }
                catch
                {
                    return TiktokenTokenizer.CreateForModel("gpt-4");
                }
            }
        });
    }
}

public readonly struct ChatMessageTokenPart
{
    public string? Role { get; init; }
    public string? Content { get; init; }
    public string? ReasoningContent { get; init; }
    public string? ToolName { get; init; }
    public string? ToolArgumentsJson { get; init; }
    public string? ToolOutput { get; init; }
}
