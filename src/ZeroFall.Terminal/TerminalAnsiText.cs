using System.Text.RegularExpressions;

namespace ZeroFall.Terminal;

internal static partial class TerminalAnsiText
{
    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled)]
    private static partial Regex CsiEscapeRegex();

    /// <summary>OSC：如 Windows 标题 <c>\x1b]0;cmd.exe\x07</c>。</summary>
    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", RegexOptions.Compiled)]
    private static partial Regex OscEscapeRegex();

    [GeneratedRegex(@"[\x00-\x08\x0B-\x0C\x0E-\x1A\x1C-\x1F\x7F]", RegexOptions.Compiled)]
    private static partial Regex ControlCharRegex();

    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        text = OscEscapeRegex().Replace(text, string.Empty);
        text = CsiEscapeRegex().Replace(text, string.Empty);
        text = ControlCharRegex().Replace(text, string.Empty);
        return text;
    }
}
