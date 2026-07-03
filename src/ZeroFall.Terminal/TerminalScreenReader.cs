using System;
using System.Collections.Generic;
using System.Text;
using XTermTerminal = XTerm.Terminal;

namespace ZeroFall.Terminal;

internal static class TerminalScreenReader
{
    /// <summary>AI 工具读取行数上限（避免大 scrollback 卡死 UI 线程）。</summary>
    public const int AiMaxLines = 400;

    /// <summary>提示符探测仅读末尾行数。</summary>
    public const int PromptProbeLines = 12;

    public static string ReadFullBuffer(XTermTerminal terminal)
    {
        var buffer = terminal.Buffer;
        if (buffer.Length <= 0)
            return string.Empty;

        return ReadLineRange(terminal, 0, buffer.Length);
    }

    public static string ReadVisibleScreen(XTermTerminal terminal)
    {
        var buffer = terminal.Buffer;
        var viewportY = buffer.ViewportY;
        var rows = terminal.Rows;
        if (rows <= 0)
            return string.Empty;

        var endY = Math.Min(buffer.Length, viewportY + rows);
        return ReadLineRange(terminal, viewportY, endY);
    }

    public static string ReadLastLines(XTermTerminal terminal, int lineCount)
    {
        if (lineCount <= 0)
            return string.Empty;

        var buffer = terminal.Buffer;
        if (buffer.Length <= 0)
            return string.Empty;

        var startY = Math.Max(0, buffer.Length - lineCount);
        return ReadLineRange(terminal, startY, buffer.Length);
    }

    public static string ReadFromLine(XTermTerminal terminal, int startLine, int maxLines = AiMaxLines)
    {
        var buffer = terminal.Buffer;
        if (buffer.Length <= 0)
            return string.Empty;

        var y = Math.Clamp(startLine, 0, buffer.Length - 1);
        var endY = Math.Min(buffer.Length, y + Math.Max(1, maxLines));
        return ReadLineRange(terminal, y, endY);
    }

    /// <summary>从指定行读到 buffer 末尾（保留命令行上的提示符等内容）。</summary>
    public static string ReadFromLineToEnd(XTermTerminal terminal, int startLine)
    {
        var buffer = terminal.Buffer;
        if (buffer.Length <= 0)
            return string.Empty;

        var y = Math.Clamp(startLine, 0, buffer.Length - 1);
        return ReadLineRange(terminal, y, buffer.Length);
    }

    /// <summary>结合 hint 行与命令文本，定位命令输入行（含多行提示符）。</summary>
    public static int ResolveCommandStartLine(XTermTerminal terminal, int hintLine, string? commandText)
    {
        var buffer = terminal.Buffer;
        if (buffer.Length <= 0)
            return 0;

        var hint = Math.Clamp(hintLine, 0, buffer.Length - 1);
        var startHint = IncludePromptLineAbove(terminal, hint);
        if (string.IsNullOrWhiteSpace(commandText))
            return startHint;

        for (var y = startHint; y < buffer.Length; y++)
        {
            var line = ReadLineText(terminal, y);
            if (LineContainsCommandInput(line, commandText))
                return IncludePromptLineAbove(terminal, y);
        }

        return startHint;
    }

    /// <summary>从 hint 行读到末尾；若结果疑似只有提示符，在 hint 之后小范围回退，不拉取更早 scrollback。</summary>
    public static string ReadSinceCommandHint(
        XTermTerminal terminal,
        int hintLine,
        string? commandText,
        int bufferLengthAtHint = -1)
    {
        var buffer = terminal.Buffer;
        if (buffer.Length <= 0)
            return string.Empty;

        var hint = Math.Clamp(hintLine, 0, buffer.Length - 1);
        var startHint = IncludePromptLineAbove(terminal, hint);
        var startLine = Math.Max(startHint, ResolveCommandStartLine(terminal, hintLine, commandText));

        var text = ReadFromLineToEnd(terminal, startLine);
        if (!ShouldExpandWithTail(terminal, text, commandText, bufferLengthAtHint))
            return text;

        var linesAdded = bufferLengthAtHint > 0
            ? Math.Max(0, buffer.Length - bufferLengthAtHint)
            : 0;
        var backupLines = Math.Clamp(linesAdded + 6, 6, 28);
        var expandFrom = Math.Max(startHint, startLine - backupLines);
        expandFrom = IncludePromptLineAbove(terminal, expandFrom);
        var expanded = ReadFromLineToEnd(terminal, expandFrom);
        return expanded.Length > text.Length ? expanded : text;
    }

    private static string CommandMatchKey(string commandText)
    {
        var t = commandText.Trim();
        var chain = t.IndexOf("&&", StringComparison.Ordinal);
        if (chain > 8)
            t = t[..chain].Trim();

        return t.Length > 40 ? t[..40] : t;
    }

    internal static int FindLastCommandLineIndex(IReadOnlyList<string> lines, string? commandText)
    {
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(commandText))
            return -1;

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (LineContainsCommandInput(lines[i], commandText))
                return i;
        }

        return -1;
    }

    private static bool LineContainsCommandInput(string line, string commandText)
    {
        if (string.IsNullOrEmpty(line) || string.IsNullOrWhiteSpace(commandText))
            return false;

        if (MatchesCommandOnLine(line, commandText))
            return true;

        if (commandText.Length <= 32)
            return false;

        var key = CommandMatchKey(commandText);
        return key.Length >= 12 && MatchesCommandOnLine(line, key);
    }

    private static bool MatchesCommandOnLine(string line, string fragment)
    {
        if (!line.Contains(fragment, StringComparison.Ordinal))
            return false;

        if (TerminalPromptDetector.TryGetTextAfterKaliPromptMarker(line, out var afterKali) && afterKali.Length > 0)
            return true;

        if (TerminalPromptDetector.LooksLikePromptPrefixLine(line))
            return false;

        var trimmed = line.Trim();
        if (trimmed.Length <= fragment.Length + 4)
            return true;

        if (fragment.Length <= 4 && trimmed.Length > fragment.Length + 32)
            return false;

        return !LooksLikePromptOnlyLine(trimmed);
    }

    private static bool LooksLikePromptOnlyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        line = line.TrimEnd();
        if (TerminalPromptDetector.TryGetTextAfterKaliPromptMarker(line, out var afterKali) && afterKali.Length > 0)
            return false;

        if (TerminalPromptDetector.LooksLikePromptPrefixLine(line))
            return true;

        return line is "└─$" or "$" or "#" or ">" || line.EndsWith("└─$", StringComparison.Ordinal);
    }

    private static bool ShouldExpandWithTail(
        XTermTerminal terminal,
        string text,
        string? commandText,
        int bufferLengthAtHint)
    {
        if (string.IsNullOrWhiteSpace(text))
            return terminal.Buffer.Length > 0;

        if (text.Length >= 200)
            return false;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return true;

        var substantive = 0;
        foreach (var line in lines)
        {
            if (!LooksLikePromptOnlyLine(line))
                substantive++;
        }

        if (substantive == 0)
            return true;

        if (!string.IsNullOrWhiteSpace(commandText)
            && commandText.Length >= 8
            && !text.Contains(commandText, StringComparison.Ordinal)
            && substantive <= 1)
            return true;

        if (bufferLengthAtHint > 0 && terminal.Buffer.Length - bufferLengthAtHint > 4 && text.Length < 64)
            return true;

        return false;
    }

    private static int IncludePromptLineAbove(XTermTerminal terminal, int lineIndex)
    {
        if (lineIndex <= 0)
            return lineIndex;

        var prev = ReadLineText(terminal, lineIndex - 1);
        return TerminalPromptDetector.LooksLikePromptPrefixLine(prev) ? lineIndex - 1 : lineIndex;
    }

    /// <summary>buffer 末尾常有光标下方的空白行，同步 transcript 时应截止到最后一条非空行。</summary>
    internal static int GetLastNonEmptyBufferLineIndex(XTermTerminal terminal)
    {
        var buffer = terminal.Buffer;
        for (var y = buffer.Length - 1; y >= 0; y--)
        {
            if (!string.IsNullOrWhiteSpace(ReadLineText(terminal, y)))
                return y;
        }

        return -1;
    }

    internal static string ReadLineText(XTermTerminal terminal, int y)
    {
        var buffer = terminal.Buffer;
        if (y < 0 || y >= buffer.Length)
            return string.Empty;

        var line = buffer.GetLine(y);
        if (line == null)
            return string.Empty;

        var lineSb = new StringBuilder();
        for (var x = 0; x < line.Length; x++)
        {
            var cell = line[x];
            lineSb.Append(string.IsNullOrEmpty(cell.Content) ? " " : cell.Content);
        }

        TrimTrailingSpaces(lineSb);
        return lineSb.ToString();
    }

    private static string ReadLineRange(XTermTerminal terminal, int startY, int endY)
    {
        var buffer = terminal.Buffer;
        var sb = new StringBuilder();
        for (var y = startY; y < endY; y++)
        {
            var line = buffer.GetLine(y);
            if (line == null)
            {
                sb.AppendLine();
            }
            else
            {
                var lineSb = new StringBuilder();
                for (var x = 0; x < line.Length; x++)
                {
                    var cell = line[x];
                    lineSb.Append(string.IsNullOrEmpty(cell.Content) ? " " : cell.Content);
                }

                TrimTrailingSpaces(lineSb);
                sb.AppendLine(lineSb.ToString());
            }

        }

        return TrimTrailingEmptyLines(sb.ToString());
    }

    private static void TrimTrailingSpaces(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
    }

    private static string TrimTrailingEmptyLines(string text)
    {
        while (text.EndsWith("\r\n", StringComparison.Ordinal) || text.EndsWith('\n'))
            text = text.TrimEnd('\r', '\n');
        return text;
    }

    internal static string TrimTrailingEmptyLinesStatic(string text) => TrimTrailingEmptyLines(text);
}
