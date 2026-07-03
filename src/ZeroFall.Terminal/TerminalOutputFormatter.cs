using System;
using System.Collections.Generic;
using System.Text;

namespace ZeroFall.Terminal;

/// <summary>整理 AI 工具返回的终端 output（去尾部重复块等）。</summary>
internal static class TerminalOutputFormatter
{
    /// <summary>send 返回前整理 output。</summary>
    public static string? FormatSendOutput(string? output, string? sentCommand)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        output = TrimToFirstCmdEnd(output, sentCommand);
        if (sentCommand?.TrimStart().StartsWith(":", StringComparison.Ordinal) == true)
            output = TrimTuiExitScrollback(output);
        else
            output = TrimFromLastCommandEcho(output, sentCommand);
        output = TrimLeadingBarePromptLines(output);
        output = CollapseConsecutiveDuplicateLines(output);
        return output;
    }

    /// <summary>命令含 <c>_cmd_end_</c> 时，截到第一个有效结束标记行（含该行），丢弃后续重复回显。</summary>
    public static string? TrimToFirstCmdEnd(string? output, string? sentCommand)
    {
        if (string.IsNullOrEmpty(output)
            || string.IsNullOrEmpty(sentCommand)
            || !sentCommand.Contains(TerminalCommandWait.CmdEndMarker, StringComparison.Ordinal))
        {
            return output;
        }

        if (!TerminalCommandWait.OutputHasCmdEndMarker(output, sentCommand))
            return output;

        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var sb = new StringBuilder(output.Length);
        var lineEnding = output.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var requireAfterCommandEcho = TerminalCommandWait.SentCommandExpectsCmdEndMarker(sentCommand);
        var sawCommandEcho = !requireAfterCommandEcho;

        foreach (var raw in normalized.Split('\n'))
        {
            if (requireAfterCommandEcho && !sawCommandEcho)
            {
                if (TerminalCommandWait.LineLooksLikeSentCommandEcho(raw, sentCommand))
                    sawCommandEcho = true;
                continue;
            }

            if (sb.Length > 0)
                sb.Append(lineEnding);
            sb.Append(raw);

            var line = raw.Trim();
            if (!string.Equals(line, TerminalCommandWait.CmdEndMarker, StringComparison.Ordinal))
                continue;

            if (string.Equals(sentCommand?.Trim(), TerminalCommandWait.CmdEndMarker, StringComparison.Ordinal))
                continue;

            break;
        }

        return sb.Length == 0 ? output : sb.ToString();
    }

    /// <summary>vim :wq 等：保留 TUI 退出时的前几行，截掉随后恢复的重复 scrollback。</summary>
    public static string? TrimTuiExitScrollback(string? output)
    {
        if (string.IsNullOrEmpty(output) || output.Length < 400)
            return output;

        var lineEnding = output.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder(output.Length);
        var lastLoginCount = 0;
        var cmdEndCount = 0;

        foreach (var raw in lines)
        {
            if (raw.Contains("Last login:", StringComparison.OrdinalIgnoreCase))
                lastLoginCount++;
            if (string.Equals(raw.Trim(), TerminalCommandWait.CmdEndMarker, StringComparison.Ordinal))
                cmdEndCount++;

            if (lastLoginCount > 1 || cmdEndCount > 1)
                break;

            if (sb.Length > 0)
                sb.Append(lineEnding);
            sb.Append(raw);
        }

        return sb.Length == 0 ? output : sb.ToString();
    }

    /// <summary>从最后一次命令回显起截断（适用于非 TUI 短命令）。</summary>
    public static string? TrimFromLastCommandEcho(string? output, string? sentCommand)
    {
        if (string.IsNullOrEmpty(output) || string.IsNullOrWhiteSpace(sentCommand))
            return output;

        var sent = sentCommand.Trim();
        if (sent.Length > 48 || sent.Contains(TerminalCommandWait.CmdEndMarker, StringComparison.Ordinal))
            return output;

        if (output.Length < 400)
            return output;

        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lineEnding = output.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = normalized.Split('\n');
        var lastEcho = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!TerminalCommandWait.LineLooksLikeSentCommandEcho(lines[i], sent))
                continue;
            lastEcho = i;
            break;
        }

        if (lastEcho < 0)
            return output;

        var sb = new StringBuilder(output.Length);
        for (var i = lastEcho; i < lines.Length; i++)
        {
            if (sb.Length > 0)
                sb.Append(lineEnding);
            sb.Append(lines[i]);
        }

        return sb.Length == 0 ? output : sb.ToString();
    }

    /// <summary>去掉 Kali 等多行提示符后残留的裸 <c>└─$</c> 行（命令输出尚未到达时）。</summary>
    private static string? TrimLeadingBarePromptLines(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        var lineEnding = output.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var start = 0;
        while (start < lines.Length && IsBarePromptLine(lines[start]))
            start++;

        if (start == 0)
            return output;

        var sb = new StringBuilder(output.Length);
        for (var i = start; i < lines.Length; i++)
        {
            if (sb.Length > 0)
                sb.Append(lineEnding);
            sb.Append(lines[i]);
        }

        return sb.Length == 0 ? string.Empty : sb.ToString();
    }

    private static bool IsBarePromptLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return true;

        if (trimmed is "└─$" or "$" or "#" or ">")
            return true;

        return TerminalPromptDetector.TryGetTextAfterKaliPromptMarker(trimmed, out var after)
               && string.IsNullOrEmpty(after);
    }

    private static string? CollapseConsecutiveDuplicateLines(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        var lineEnding = output.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var kept = new List<string>(lines.Length);
        string? prev = null;

        foreach (var line in lines)
        {
            var key = line.TrimEnd();
            if (prev != null && string.Equals(prev, key, StringComparison.Ordinal))
                continue;

            kept.Add(line);
            prev = key;
        }

        if (kept.Count == lines.Length)
            return output;

        var sb = new StringBuilder(output.Length);
        for (var i = 0; i < kept.Count; i++)
        {
            if (i > 0)
                sb.Append(lineEnding);
            sb.Append(kept[i]);
        }

        return sb.ToString();
    }
}
