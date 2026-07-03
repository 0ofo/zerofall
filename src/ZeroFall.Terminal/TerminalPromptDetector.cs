using System;
using System.Text.RegularExpressions;

namespace ZeroFall.Terminal;

internal static partial class TerminalPromptDetector
{
    [GeneratedRegex(@"^[A-Za-z]:\\[^>]*>$", RegexOptions.Compiled)]
    private static partial Regex CmdPromptRegex();

    [GeneratedRegex(@"^PS [^>]+>$", RegexOptions.Compiled)]
    private static partial Regex PowerShellPromptRegex();

    [GeneratedRegex(@"^[\w@:/\-\.~\\]+[$#]\s*$", RegexOptions.Compiled)]
    private static partial Regex BashPromptRegex();

    public static bool LooksLikePrompt(string? line, bool isCmd, bool isPowerShell, bool isBash)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        line = line.TrimEnd();
        if (LooksLikePromptPrefixLine(line))
            return true;

        if (isCmd)
            return CmdPromptRegex().IsMatch(line) || line.EndsWith('>');

        if (isPowerShell)
            return PowerShellPromptRegex().IsMatch(line);

        if (isBash)
            return BashPromptRegex().IsMatch(line);

        return line.EndsWith('>') || line.EndsWith('$') || line.EndsWith('#');
    }

    /// <summary>Kali/zsh 多行提示符的上半行，或不含命令的 <c>└─$</c> 行。</summary>
    public static bool LooksLikePromptPrefixLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        line = line.TrimEnd();
        if (line.Contains("┌──(", StringComparison.Ordinal))
            return true;

        if (TryGetTextAfterKaliPromptMarker(line, out var afterMarker))
            return string.IsNullOrEmpty(afterMarker);

        if (line.Contains('(') && line.Contains(')') && (line.Contains('@') || line.Contains('㉿')))
            return true;

        return false;
    }

    /// <summary>等待结束检测用：末行必须是完整提示符，不能是 <c>└─$ e</c> 这类半截回显。</summary>
    public static bool IsCompletePromptLineForWait(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        line = line.TrimEnd();
        if (line.Contains(';', StringComparison.Ordinal) || line.Contains('&', StringComparison.Ordinal))
            return false;

        if (TryGetTextAfterKaliPromptMarker(line, out var afterMarker))
            return string.IsNullOrEmpty(afterMarker);

        if (line.Contains("┌──(", StringComparison.Ordinal))
            return true;

        if (CmdPromptRegex().IsMatch(line) || PowerShellPromptRegex().IsMatch(line) || BashPromptRegex().IsMatch(line))
            return true;

        if (line is "$" or "#" or ">" or "└─$")
            return true;

        if (line.EndsWith('>') && !line.Contains('<') && line.Contains(":\\", StringComparison.Ordinal))
        {
            var gt = line.LastIndexOf('>');
            return gt == line.Length - 1;
        }

        return false;
    }

    public static string? GetLastNonEmptyLine(string? bufferText)
    {
        if (string.IsNullOrWhiteSpace(bufferText))
            return null;

        var lines = bufferText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].TrimEnd();
            if (!string.IsNullOrWhiteSpace(line))
                return line;
        }

        return null;
    }

    /// <summary>是否为 shell 提示符行（排除 HTML 等以 <c>&gt;</c> 结尾的非提示符文本）。</summary>
    public static bool LooksLikeShellPromptLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.TrimEnd();
        if (LooksLikePromptPrefixLine(trimmed))
            return true;

        if (CmdPromptRegex().IsMatch(trimmed) || PowerShellPromptRegex().IsMatch(trimmed) || BashPromptRegex().IsMatch(trimmed))
            return true;

        if (trimmed is "$" or "#" or ">" or "└─$")
            return true;

        if (trimmed.EndsWith("└─$", StringComparison.Ordinal) && TryGetTextAfterKaliPromptMarker(trimmed, out var after) && string.IsNullOrEmpty(after))
            return true;

        if (trimmed.StartsWith("cd ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("CD ", StringComparison.Ordinal))
            return false;

        if (trimmed.EndsWith('>') && !trimmed.Contains('<') && trimmed.Contains(":\\", StringComparison.Ordinal))
        {
            var gt = trimmed.LastIndexOf('>');
            if (gt == trimmed.Length - 1)
            {
                var prefix = trimmed[..gt].Trim();
                if (prefix.Length > 0 && !prefix.Contains('"'))
                    return true;
            }
        }

        return false;
    }

    /// <summary><c>└─$</c> 之后若还有非空文本，则为命令回显行而非裸提示符。</summary>
    internal static bool TryGetTextAfterKaliPromptMarker(string line, out string afterMarker)
    {
        afterMarker = string.Empty;
        var idx = line.IndexOf("└─$", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        afterMarker = line[(idx + 3)..].TrimStart();
        return true;
    }
}
