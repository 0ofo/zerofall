using System;
using System.Text;

namespace ZeroFall.AiPanel.Services;

/// <summary>主会话与子 Agent 共用的流式正文拼接逻辑。</summary>
public static class ChatCompletionStreamText
{
    /// <summary>
    /// 向流式累积器追加文本：支持增量 delta 与整段重复的 message.content（仅追加未出现过的后缀）。
    /// </summary>
    public static string? AppendSuffix(StringBuilder accumulator, string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var existing = accumulator.ToString();
        if (existing.Length == 0)
        {
            accumulator.Append(text);
            return text;
        }

        if (text.StartsWith(existing, StringComparison.Ordinal))
        {
            if (text.Length <= existing.Length)
                return null;
            var suffix = text[existing.Length..];
            accumulator.Append(suffix);
            return suffix;
        }

        if (existing.StartsWith(text, StringComparison.Ordinal))
            return null;

        accumulator.Append(text);
        return text;
    }

    /// <summary>流结束合并正文：优先保留 Flush 结果；若 sb 为 2～3 遍重复拼接则折叠。</summary>
    public static string MergeFinalContent(string flushed, string fromAccumulator)
    {
        fromAccumulator = CollapseRepeated(fromAccumulator);
        if (string.IsNullOrEmpty(flushed))
            return fromAccumulator;
        if (string.IsNullOrEmpty(fromAccumulator) || flushed == fromAccumulator)
            return flushed;
        if (fromAccumulator.StartsWith(flushed, StringComparison.Ordinal))
            return fromAccumulator;
        if (flushed.StartsWith(fromAccumulator, StringComparison.Ordinal))
            return flushed;
        return fromAccumulator;
    }

    private static string CollapseRepeated(string text)
    {
        if (text.Length < 2)
            return text;

        for (var copies = 2; copies <= 3; copies++)
        {
            if (text.Length % copies != 0)
                continue;
            var unitLen = text.Length / copies;
            var unit = text[..unitLen];
            var allMatch = true;
            for (var i = 1; i < copies; i++)
            {
                if (!text.AsSpan(i * unitLen, unitLen).SequenceEqual(unit))
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
                return unit;
        }

        return text;
    }
}
