using System.Collections.Generic;
using System.Text;

namespace ZeroFall.Terminal.Services;

internal static class TerminalTextNormalizer
{
    /// <summary>将 PTY 原始文本（可含跨 chunk 的 \\r/\\n）整理为 AI 可读的多行文本。</summary>
    public static string NormalizeForAi(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var lines = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (ch == '\r')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '\n')
                    continue;

                current.Clear();
            }
            else if (ch == '\n')
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.AppendLine(line);

        return TerminalScreenReader.TrimTrailingEmptyLinesStatic(sb.ToString());
    }
}
