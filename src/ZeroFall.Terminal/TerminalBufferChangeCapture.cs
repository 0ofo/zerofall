using System;
using XT = global::XTerm;
using XTermTerminal = XTerm.Terminal;

namespace ZeroFall.Terminal;

/// <summary>
/// 官方 Iciclecreek 无 PTY 原始输出事件；通过 XTerm buffer 增量近似捕获终端输出。
/// </summary>
internal sealed class TerminalBufferChangeCapture : IDisposable
{
    private XTermTerminal? _terminal;
    private string _snapshot = string.Empty;

    public event Action<string>? OutputAppended;

    public void Attach(XTermTerminal terminal)
    {
        Detach();
        _terminal = terminal;
        _snapshot = TerminalScreenReader.ReadFullBuffer(terminal);
        terminal.BufferChanged += OnBufferChanged;
    }

    public void Clear()
    {
        _snapshot = _terminal != null ? TerminalScreenReader.ReadFullBuffer(_terminal) : string.Empty;
    }

    public string GetStrippedText()
    {
        if (_terminal == null)
            return string.Empty;

        return TerminalAnsiText.Strip(TerminalScreenReader.ReadFullBuffer(_terminal));
    }

    private void OnBufferChanged(object? sender, XT.Events.TerminalEvents.BufferChangedEventArgs e)
    {
        if (_terminal == null)
            return;

        var full = TerminalScreenReader.ReadFullBuffer(_terminal);
        if (string.IsNullOrEmpty(full))
        {
            _snapshot = full;
            return;
        }

        string delta;
        if (full.Length >= _snapshot.Length && full.StartsWith(_snapshot, StringComparison.Ordinal))
            delta = full[_snapshot.Length..];
        else
            delta = full;

        _snapshot = full;
        if (!string.IsNullOrEmpty(delta))
            OutputAppended?.Invoke(delta);
    }

    public void Detach()
    {
        if (_terminal == null)
            return;

        _terminal.BufferChanged -= OnBufferChanged;
        _terminal = null;
        _snapshot = string.Empty;
    }

    public void Dispose() => Detach();
}
