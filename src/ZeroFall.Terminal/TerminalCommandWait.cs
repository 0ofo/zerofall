using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Platform.Services;

namespace ZeroFall.Terminal;

/// <summary>AI 工具发送命令后的轮询等待：结束标记 / 提示符正则 / state=idle / 超时。</summary>
internal static class TerminalCommandWait
{
    public const int MaxWaitSeconds = 30;

    private const int PollIntervalMs = 100;

    /// <summary>命令结束时 echo 的标记；可为单独一行，也可紧接在前序输出同一行末尾（前序无换行时）。</summary>
    public const string CmdEndMarker = "_cmd_end_";

    /// <summary>Windows cmd（<c>D:\proj&gt;</c>）与 PowerShell（<c>PS C:\path&gt;</c>）提示符。</summary>
    public const string WindowsShellPromptPattern =
        @"(?:[A-Za-z]:[/\\][^>\r\n]*>|PS [^>\r\n]+>)\s*$";

    /// <summary>解析等待结束正则：显式参数优先；Windows 未指定时用 cmd/PS 提示符；Linux/macOS 不设默认。</summary>
    public static (Regex? Pattern, string? DisplayLabel) ResolveEndPattern(string? waitEndPattern, out string? error)
    {
        if (!string.IsNullOrWhiteSpace(waitEndPattern))
        {
            var regex = TryCompileEndPattern(waitEndPattern, out error);
            return (regex, waitEndPattern.Trim());
        }

        if (OperatingSystem.IsWindows())
        {
            var regex = TryCompileEndPattern(WindowsShellPromptPattern, out error);
            return (regex, WindowsShellPromptPattern);
        }

        error = null;
        return (null, null);
    }

    public static Regex? TryCompileEndPattern(string? waitEndPattern, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(waitEndPattern))
            return null;

        try
        {
            return new Regex(
                waitEndPattern.Trim(),
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            error = $"waitEndPattern 不是合法正则: {ex.Message}";
            return null;
        }
    }

    public static async Task<int> WaitAsync(
        ITerminalScreenService screen,
        ITerminalSessionStateService state,
        string? sessionId,
        int maxWaitSeconds,
        Regex? endPattern,
        string? sentCommand = null,
        CancellationToken cancellationToken = default)
    {
        var maxMs = Math.Clamp(maxWaitSeconds, 0, MaxWaitSeconds) * 1000;
        if (maxMs <= 0)
            return 0;

        var elapsed = 0;
        var sawExecuting = false;
        while (elapsed < maxMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var phase = await state.GetPhaseAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (phase == TerminalCommandPhase.Executing)
                sawExecuting = true;

            var since = await screen.ReadSinceLastCommandAsync(sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (OutputHasCmdEndMarker(since, sentCommand))
                break;

            if (sawExecuting && phase == TerminalCommandPhase.Idle)
                break;

            if (endPattern != null
                && !LooksLikeFullScreenTui(since)
                && await ScreenMatchesEndPatternAsync(screen, sessionId, endPattern, since, cancellationToken).ConfigureAwait(false)
                && CanCompleteOnPromptMatch(since, sentCommand, sawExecuting, phase))
                break;

            var step = Math.Min(PollIntervalMs, maxMs - elapsed);
            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
            elapsed += step;
        }

        return elapsed == 0 ? 0 : Math.Max(1, (elapsed + 999) / 1000);
    }

    /// <summary>输出中是否出现结束标记（非发送命令行的 echo 回显）。</summary>
    public static bool OutputHasCmdEndMarker(string? output, string? sentCommand = null)
    {
        if (string.IsNullOrEmpty(output))
            return false;

        var sent = sentCommand?.Trim();
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var requireAfterCommandEcho = SentCommandExpectsCmdEnd(sent);
        var sawCommandEcho = !requireAfterCommandEcho;

        foreach (var raw in normalized.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (requireAfterCommandEcho && !sawCommandEcho)
            {
                if (LineLooksLikeSentCommandEcho(raw, sent))
                    sawCommandEcho = true;
                continue;
            }

            if (!string.Equals(line, CmdEndMarker, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrEmpty(sent)
                && string.Equals(sent, CmdEndMarker, StringComparison.Ordinal))
                continue;

            return true;
        }

        if (requireAfterCommandEcho && !sawCommandEcho)
            return false;

        var trimmed = normalized.TrimEnd();
        if (!trimmed.EndsWith(CmdEndMarker, StringComparison.Ordinal))
            return false;

        var lineStart = trimmed.LastIndexOf('\n') + 1;
        var lastLine = trimmed[lineStart..];
        if (string.Equals(lastLine.Trim(), CmdEndMarker, StringComparison.Ordinal))
            return false;

        return !IsCommandEchoLineWithMarker(lastLine, sent);
    }

    private static bool SentCommandExpectsCmdEnd(string? sent) =>
        !string.IsNullOrEmpty(sent) && sent.Contains(CmdEndMarker, StringComparison.Ordinal);

    /// <inheritdoc cref="SentCommandExpectsCmdEnd"/>
    internal static bool SentCommandExpectsCmdEndMarker(string? sent) => SentCommandExpectsCmdEnd(sent);

    /// <summary>输出行是否像本次 send 的命令回显（用于 _cmd_end_ 须出现在回显之后）。</summary>
    internal static bool LineLooksLikeSentCommandEcho(string rawLine, string? sent)
    {
        if (string.IsNullOrEmpty(sent))
            return true;

        var line = rawLine.Trim();
        if (line.Length == 0)
            return false;

        if (line.Contains(sent, StringComparison.Ordinal))
            return true;

        if (sent.Length <= 48)
            return false;

        var key = sent[..48];
        return line.Contains(key, StringComparison.Ordinal);
    }

    /// <summary>发送行回显（如 <c>python x.py &amp; echo _cmd_end_</c>）末尾含标记，但不是 echo 真正打印的结束标记。</summary>
    private static bool IsCommandEchoLineWithMarker(string line, string? sentCommand)
    {
        if (string.IsNullOrEmpty(sentCommand))
            return false;

        var sent = sentCommand.Trim();
        if (!sent.Contains(CmdEndMarker, StringComparison.Ordinal))
            return false;

        var trimmed = line.Trim();
        if (!trimmed.EndsWith(CmdEndMarker, StringComparison.Ordinal))
            return false;

        var beforeMarker = trimmed[..^CmdEndMarker.Length].TrimEnd();
        if (beforeMarker.Length == 0)
            return false;

        if (trimmed.Contains(sent, StringComparison.Ordinal))
            return true;

        var key = sent.Length > 48 ? sent[..48] : sent;
        return key.Length >= 8 && trimmed.Contains(key, StringComparison.Ordinal);
    }

    private static async Task<bool> ScreenMatchesEndPatternAsync(
        ITerminalScreenService screen,
        string? sessionId,
        Regex regex,
        string? sinceCommand,
        CancellationToken cancellationToken)
    {
        if (OutputTailMatches(regex, sinceCommand))
            return true;

        if (!string.IsNullOrEmpty(sinceCommand))
            return false;

        var visible = await screen.ReadVisibleScreenAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return OutputTailMatches(regex, visible);
    }

    private static bool OutputTailMatches(Regex regex, string? output)
    {
        if (string.IsNullOrEmpty(output))
            return false;

        var lastLine = TerminalPromptDetector.GetLastNonEmptyLine(output);
        if (lastLine != null && regex.IsMatch(lastLine))
            return TerminalPromptDetector.IsCompletePromptLineForWait(lastLine);

        var tail = output.TrimEnd();
        if (!regex.IsMatch(tail))
            return false;

        lastLine = TerminalPromptDetector.GetLastNonEmptyLine(tail);
        return lastLine != null && TerminalPromptDetector.IsCompletePromptLineForWait(lastLine);
    }

    public static bool MatchesOutput(Regex regex, string? output) =>
        OutputTailMatches(regex, output);

    private static bool CanCompleteOnPromptMatch(
        string? output,
        string? sentCommand,
        bool sawExecuting,
        TerminalCommandPhase? phase)
    {
        if (string.IsNullOrWhiteSpace(sentCommand))
            return true;

        if (OutputHasCmdEndMarker(output, sentCommand))
            return true;

        if (IsInteractiveSentCommand(sentCommand))
        {
            if (output?.Contains("password", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            if (output?.Contains("passphrase", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            if (output?.Contains("yes/no", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            if (sawExecuting && phase == TerminalCommandPhase.Idle)
                return true;
            return false;
        }

        if (sawExecuting && phase == TerminalCommandPhase.Idle)
            return true;

        return CountSubstantiveLines(output, sentCommand) > 0;
    }

    private static bool LooksLikeFullScreenTui(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return false;

        if (output.Contains("-- INSERT --", StringComparison.Ordinal))
            return true;

        if (output.Contains("[New]", StringComparison.Ordinal)
            && output.Contains('~')
            && output.Contains("All", StringComparison.Ordinal))
            return true;

        return output.Contains("Press ENTER", StringComparison.OrdinalIgnoreCase)
               || output.Contains("--More--", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInteractiveSentCommand(string sentCommand)
    {
        var t = sentCommand.Trim();
        return t.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("sudo ", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("mysql ", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("psql ", StringComparison.OrdinalIgnoreCase)
               || t.Contains(" ssh ", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountSubstantiveLines(string? output, string sentCommand)
    {
        if (string.IsNullOrEmpty(output))
            return 0;

        var count = 0;
        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.Contains(sentCommand, StringComparison.Ordinal))
                continue;

            if (TerminalPromptDetector.IsCompletePromptLineForWait(line))
                continue;

            count++;
        }

        return count;
    }
}
