using System;

namespace ZeroFall.Terminal;

/// <summary>合并 transcript 行读屏与 PTY 字节流切片，互补 XTerm 同步缺口。</summary>
internal static class TerminalAiReadMerge
{
    public static string? PickSinceCommand(string? transcript, string? ptySlice, string? sentCommand = null)
    {
        if (string.IsNullOrEmpty(transcript))
            return ptySlice;
        if (string.IsNullOrEmpty(ptySlice))
            return transcript;

        var t = transcript.TrimEnd();
        var p = ptySlice.TrimEnd();

        if (!string.IsNullOrEmpty(sentCommand)
            && t.Length < 48
            && p.Length > t.Length + 24
            && p.Contains(sentCommand, StringComparison.Ordinal))
        {
            return ptySlice;
        }

        if (p.Length > t.Length + 32)
        {
            // vim 退出等场景 PTY 会膨胀为 scrollback；短命令或 transcript 已够用时不采纳 PTY
            if (!string.IsNullOrEmpty(t)
                && (ContainsCmdEnd(t)
                    || IsShortInteractiveInput(sentCommand)
                    || !LooksLikePollutedPtyScrollback(p, sentCommand)))
                return transcript;
            return ptySlice;
        }

        if (ContainsCmdEnd(p) && !ContainsCmdEnd(t))
            return ptySlice;

        // 两侧均有结束标记时 transcript 经 tail 整理更干净；PTY 流常含重复回显。
        if (ContainsCmdEnd(t) && ContainsCmdEnd(p))
            return transcript;

        if (EndsWithShellPrompt(p) && !EndsWithShellPrompt(t) && p.Length >= t.Length)
            return ptySlice;

        return t.Length >= p.Length ? transcript : ptySlice;
    }

    /// <summary>read_terminal 增量读：仅 transcript 行游标（PTY 不参与）。</summary>
    public static string PickSinceAiRead(string? transcript) =>
        transcript ?? string.Empty;

    /// <summary>无 transcript 时 PTY 回退：拒绝疑似全量 scrollback 的切片。</summary>
    internal static bool LooksLikeReadScrollbackPollution(string? slice)
    {
        if (string.IsNullOrEmpty(slice) || slice.Length < 400)
            return false;

        if (CountOccurrences(slice, TerminalCommandWait.CmdEndMarker) >= 2)
            return true;

        return slice.Contains("Last login:", StringComparison.OrdinalIgnoreCase)
               && slice.Contains("echo ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShortInteractiveInput(string? sentCommand)
    {
        if (string.IsNullOrWhiteSpace(sentCommand))
            return false;

        var t = sentCommand.Trim();
        return t.Length <= 16
               || t.StartsWith(":", StringComparison.Ordinal)
               || string.Equals(t, "i", StringComparison.Ordinal)
               || string.Equals(t, "a", StringComparison.Ordinal)
               || string.Equals(t, "o", StringComparison.Ordinal);
    }

    private static bool LooksLikePollutedPtyScrollback(string ptySlice, string? sentCommand)
    {
        if (ptySlice.Length < 512)
            return false;

        var markerCount = CountOccurrences(ptySlice, TerminalCommandWait.CmdEndMarker);
        if (markerCount >= 2)
            return true;

        if (!string.IsNullOrEmpty(sentCommand)
            && CountOccurrences(ptySlice, sentCommand.Trim()) > 1)
            return true;

        return ptySlice.Contains("Last login:", StringComparison.OrdinalIgnoreCase)
               && ptySlice.Contains("echo ", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }

    private static bool ContainsCmdEnd(string text) =>
        text.Contains(TerminalCommandWait.CmdEndMarker, StringComparison.Ordinal);

    private static bool EndsWithShellPrompt(string text)
    {
        var last = TerminalPromptDetector.GetLastNonEmptyLine(text);
        return TerminalPromptDetector.LooksLikeShellPromptLine(last);
    }
}
