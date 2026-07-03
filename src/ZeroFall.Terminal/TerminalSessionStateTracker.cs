using System;
using Avalonia.Threading;

using ZeroFall.Platform.Services;

namespace ZeroFall.Terminal;

internal sealed class TerminalSessionStateTracker : IDisposable
{
    private static readonly TimeSpan PromptQuietTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ForceIdleTimeout = TimeSpan.FromSeconds(15);

    private readonly DispatcherTimer _fallbackTimer;
    private DateTime _lastOutputUtc = DateTime.UtcNow;
    private Func<bool>? _promptProbe;
    private TerminalCommandPhase _phase = TerminalCommandPhase.Unknown;

    public TerminalCommandPhase Phase => _phase;

    public event Action<TerminalCommandPhase>? PhaseChanged;

    public TerminalSessionStateTracker()
    {
        _fallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _fallbackTimer.Tick += OnFallbackTick;
        _fallbackTimer.Start();
    }

    public void Reset()
    {
        SetPhase(TerminalCommandPhase.Unknown);
        _lastOutputUtc = DateTime.UtcNow;
    }

    public void SetPromptProbe(Func<bool> probe) => _promptProbe = probe;

    public void NotifyOutput(string chunk)
    {
        if (!string.IsNullOrEmpty(chunk))
            _lastOutputUtc = DateTime.UtcNow;
    }

    public double GetSecondsSinceLastOutput() =>
        Math.Round((DateTime.UtcNow - _lastOutputUtc).TotalSeconds, 1, MidpointRounding.AwayFromZero);

    public void NotifyCommandSent() => SetPhase(TerminalCommandPhase.Executing);

    private void OnFallbackTick(object? sender, EventArgs e)
    {
        if (_phase == TerminalCommandPhase.Executing)
        {
            var silent = DateTime.UtcNow - _lastOutputUtc;
            if (silent >= ForceIdleTimeout)
            {
                SetPhase(TerminalCommandPhase.Idle);
                return;
            }

            // 提示符探测较贵，静默未满 3s 时不探测
            if (silent < PromptQuietTimeout)
                return;

            if (_promptProbe?.Invoke() == true)
                SetPhase(TerminalCommandPhase.Idle);
            return;
        }

        if (_phase == TerminalCommandPhase.Unknown && _promptProbe?.Invoke() == true)
            SetPhase(TerminalCommandPhase.Idle);
    }

    private void SetPhase(TerminalCommandPhase phase)
    {
        if (_phase == phase)
            return;

        _phase = phase;
        PhaseChanged?.Invoke(phase);
    }

    public void Dispose()
    {
        _fallbackTimer.Stop();
        _fallbackTimer.Tick -= OnFallbackTick;
    }
}
