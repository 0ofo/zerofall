using System;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.ML.Tokenizers;

namespace ZeroFall.Platform.Services;

/// <summary>基于 Microsoft.ML.Tokenizers（AOT 友好）的聊天 token 估算。</summary>
public static class AiChatTokenCounter
{
    private const int MessageOverheadTokens = 4;
    private const int ToolCallOverheadTokens = 3;
    private static readonly ConcurrentDictionary<string, TiktokenTokenizer> Tokenizers = new(StringComparer.OrdinalIgnoreCase);

    public static int CountText(string? text, string? modelId)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        if (UsesDeepSeekCharEstimate(modelId))
            return EstimateDeepSeekTextTokens(text);

        return ResolveTokenizer(modelId).CountTokens(text);
    }

    /// <summary>DeepSeek 模型用官方文档给出的字符换算粗估，不嵌入 tokenizer。</summary>
    public static bool UsesDeepSeekCharEstimate(string? modelId)
        => !string.IsNullOrWhiteSpace(modelId)
           && modelId.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

    /// <summary>DeepSeek 文档：英文约 0.3 token/字符，中文约 0.6 token/字符。</summary>
    private static int EstimateDeepSeekTextTokens(string text)
    {
        double sum = 0;
        foreach (var ch in text)
        {
            if (IsCjkChar(ch))
                sum += 0.6;
            else if (ch <= 127)
                sum += 0.3;
            else
                sum += 0.45;
        }

        return (int)Math.Round(sum, MidpointRounding.AwayFromZero);
    }

    private static bool IsCjkChar(char ch)
        => ch is >= '\u4E00' and <= '\u9FFF'
           or >= '\u3400' and <= '\u4DBF'
           or >= '\uF900' and <= '\uFAFF';

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

    /// <summary>按 OpenAI Chat Completions 消息语义计 token（不序列化 JSON，避免键名/转义虚高）。</summary>
    public static int EstimateApiMessageNodeTokens(JsonNode? node, string? modelId)
    {
        if (node is not JsonObject obj)
            return 0;

        var tokens = MessageOverheadTokens;

        if (GetStringProperty(obj, "role") is { Length: > 0 } role)
            tokens += CountText(role, modelId);

        if (GetStringProperty(obj, "content") is { Length: > 0 } content)
            tokens += CountText(content, modelId);

        if (GetStringProperty(obj, "reasoning_content") is { Length: > 0 } reasoning)
            tokens += CountText(reasoning, modelId);

        if (GetStringProperty(obj, "tool_call_id") is { Length: > 0 } toolCallId)
            tokens += CountText(toolCallId, modelId);

        if (obj["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var item in toolCalls)
            {
                if (item is not JsonObject tc)
                    continue;

                tokens += ToolCallOverheadTokens;

                if (GetStringProperty(tc, "id") is { Length: > 0 } id)
                    tokens += CountText(id, modelId);

                if (tc["function"] is JsonObject fn)
                {
                    if (GetStringProperty(fn, "name") is { Length: > 0 } name)
                        tokens += CountText(name, modelId);

                    if (GetStringProperty(fn, "arguments") is { Length: > 0 } args)
                        tokens += CountText(args, modelId);
                }
            }
        }

        return tokens;
    }

    private static string? GetStringProperty(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
            return null;

        return node is JsonValue { } jv && jv.TryGetValue<string>(out var s) ? s : null;
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
