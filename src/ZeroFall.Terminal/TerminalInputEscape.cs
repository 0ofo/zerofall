using System;
using System.Text;
using System.Text.RegularExpressions;

namespace ZeroFall.Terminal;

/// <summary>将 AI 工具传入的转义文本还原为 PTY 实际按键字节。</summary>
internal static partial class TerminalInputEscape
{
    [GeneratedRegex(@"\\u([0-9A-Fa-f]{4})", RegexOptions.Compiled)]
    private static partial Regex UnicodeEscapeRegex();

    [GeneratedRegex(@"\\x([0-9A-Fa-f]{2})", RegexOptions.Compiled)]
    private static partial Regex HexEscapeRegex();

    public static string Unescape(string command)
    {
        if (string.IsNullOrEmpty(command))
            return command;

        var trimmed = command.Trim();
        if (trimmed is "<Esc>" or "<ESC>" or "^[" or "\\e" or "\\E")
            return "\x1b";

        var sb = new StringBuilder(command.Length);
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '\\' && i + 1 < command.Length)
            {
                var next = command[i + 1];
                if (next is 'e' or 'E')
                {
                    sb.Append('\x1b');
                    i++;
                    continue;
                }

                if (next is 'n')
                {
                    sb.Append('\n');
                    i++;
                    continue;
                }

                if (next is 't')
                {
                    sb.Append('\t');
                    i++;
                    continue;
                }

                if (next is 'r')
                {
                    sb.Append('\r');
                    i++;
                    continue;
                }
            }

            sb.Append(c);
        }

        var text = UnicodeEscapeRegex().Replace(sb.ToString(), m =>
            ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

        return HexEscapeRegex().Replace(text, m =>
            ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }
}
