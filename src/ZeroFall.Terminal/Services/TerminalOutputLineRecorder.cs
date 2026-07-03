using System.Collections.Generic;
using System.Text;

namespace ZeroFall.Terminal.Services;

internal readonly record struct TerminalFeedResult(IReadOnlyList<string> CompletedLines, bool RewindLastLine);

internal sealed class TerminalOutputLineRecorder
{
    private readonly StringBuilder _current = new();
    /// <summary>上一 token 是否为 \\n（\\r 后不应再 rewind 已提交行，避免 cmd 的 \\r\\n 空行删掉 whoami 输出）。</summary>
    private bool _justCompletedLine;

    public TerminalFeedResult Feed(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return new TerminalFeedResult([], false);

        var lines = new List<string>();
        var rewindLastLine = false;
        for (var i = 0; i < chunk.Length; i++)
        {
            var ch = chunk[i];
            if (ch == '\r')
            {
                if (i + 1 < chunk.Length && chunk[i + 1] == '\n')
                    continue;

                if (_current.Length == 0 && !_justCompletedLine)
                    rewindLastLine = true;

                _current.Clear();
                _justCompletedLine = false;
            }
            else if (ch == '\n')
            {
                lines.Add(_current.ToString());
                _current.Clear();
                _justCompletedLine = true;
            }
            else
            {
                _current.Append(ch);
                _justCompletedLine = false;
            }
        }

        return new TerminalFeedResult(lines, rewindLastLine);
    }

    public string? PeekPartial() => _current.Length > 0 ? _current.ToString() : null;

    public void ClearPartial() => _current.Clear();

    public void ReplacePartial(string text)
    {
        _current.Clear();
        if (!string.IsNullOrEmpty(text))
            _current.Append(text);
    }
}
