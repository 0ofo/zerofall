using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZeroFall.Platform.Services;
using ZeroFall.Terminal;
using ZeroFall.Terminal.ViewModels;
using Iciclecreek.Terminal;
using IcicleTerminalView = Iciclecreek.Terminal.TerminalView;

namespace ZeroFall.Terminal.Views;

public partial class TerminalView : UserControl, INonReloadableTabHost, ITabContentReleasable
{
    private TerminalViewModel? _currentVm;
    private bool _processLaunched;
    private bool _sizeWatchAttached;
    private readonly TerminalBufferChangeCapture _bufferCapture = new();
    private TerminalControl? _bufferCaptureSource;
    private TerminalSessionStateTracker? _stateTracker;
    private DispatcherTimer? _transcriptTailSyncTimer;
    private int _lastCommandTextOffset = -1;
    private int _lastAiReadTextOffset = -1;
    private bool _aiReadTextOffsetInitialized;
    private int _lastCommandBufferLengthAtSend = -1;

    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        SizeChanged += OnTerminalHostSizeChanged;
    }

    public void OnTabBecameVisible()
    {
        ScheduleShellLaunchAfterHostReady();
        if (_processLaunched)
            ScheduleTerminalSurfaceRefresh();
    }

    public void OnTabBecameHidden()
    {
    }

    public void NotifyHostTabVisible() => ScheduleShellLaunchAfterHostReady();
    private TerminalViewModel? ResolveViewModel()
    {
        if (DataContext is TerminalViewModel vm)
            return vm;
        if (Tag is TerminalViewModel tagVm)
            return tagVm;
        return null;
    }

    private void AttachViewModelIfNeeded()
    {
        var vm = ResolveViewModel();
        if (vm == null)
            return;

        if (!ReferenceEquals(_currentVm, vm))
        {
            if (_currentVm != null)
                _currentVm.PropertyChanged -= OnViewModelPropertyChanged;

            _currentVm = vm;
            _currentVm.PropertyChanged += OnViewModelPropertyChanged;
        }

        SyncTerminalControlFromViewModel();
    }

    private void SyncTerminalControlFromViewModel()
    {
        if (_currentVm == null)
            return;

        var terminal = this.FindControl<TerminalControl>("Terminal");
        if (terminal == null)
            return;

        terminal.Process = _currentVm.ShellPath;
        terminal.StartingDirectory = _currentVm.CurrentDirectory;
        terminal.FontFamily = new FontFamily(_currentVm.FontFamily);
        terminal.FontSize = _currentVm.FontSize;
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => AttachViewModelIfNeeded();

    private async void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.PendingCommand))
        {
            await ExecutePendingCommandAsync();
        }
        else if (e.PropertyName is nameof(TerminalViewModel.CurrentDirectory)
                 or nameof(TerminalViewModel.ShellPath)
                 or nameof(TerminalViewModel.FontFamily)
                 or nameof(TerminalViewModel.FontSize))
        {
            SyncTerminalControlFromViewModel();
        }
    }

    private void OnTerminalHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
            return;

        ScheduleShellLaunchAfterHostReady();
        if (_processLaunched)
            ScheduleTerminalSurfaceRefresh();
    }

    private async Task ExecutePendingCommandAsync()
    {
        if (_currentVm == null || string.IsNullOrEmpty(_currentVm.PendingCommand)) return;

        var command = _currentVm.PendingCommand;
        _currentVm.PendingCommand = null;
        await SendCommandAsync(command);
    }

    public async Task SendCommandAsync(string command)
    {
        if (_currentVm == null || string.IsNullOrWhiteSpace(command))
            return;

        if (_currentVm.IsCmd && string.Equals(command, "clear", StringComparison.OrdinalIgnoreCase))
            command = "cls";

        var terminal = this.FindControl<TerminalControl>("Terminal");
        if (terminal == null)
            return;

        _currentVm.SetLastCommandText(command);
        _currentVm.SetLastCommandStartLine(GetCommandStartLineIndex(terminal));
        if (terminal.Terminal is { } xtermAtSend)
            _lastCommandBufferLengthAtSend = xtermAtSend.Buffer.Length;
        _currentVm.TranscriptService?.MarkCommandStart(_currentVm.Id, command);
        _lastCommandTextOffset = _bufferCapture.GetStrippedText().Length;
        _stateTracker?.NotifyCommandSent();
        await terminal.SendTextAsync(TerminalInputEscape.Unescape(command) + GetShellLineEnding());
    }

    private static int GetCommandStartLineIndex(TerminalControl terminal)
    {
        if (terminal.Terminal is not { } xterm)
            return -1;

        var buffer = xterm.Buffer;
        if (buffer.Length <= 0)
            return 0;

        return Math.Max(0, buffer.Length - 1);
    }

    public async Task SendInterruptAsync()
    {
        var terminal = this.FindControl<TerminalControl>("Terminal");
        if (terminal == null)
            return;

        await terminal.SendTextAsync("\x03");
    }

    private static string GetShellLineEnding() =>
        OperatingSystem.IsWindows() ? "\r" : "\n";

    private void OnAttachedToVisualTree(object? sender, EventArgs e)
    {
        AttachViewModelIfNeeded();

        if (_processLaunched || _currentVm == null)
            return;

        ScheduleShellLaunchAfterHostReady();
    }

    private void ScheduleShellLaunchAfterHostReady()
    {
        if (_processLaunched || _currentVm == null)
            return;

        Dispatcher.UIThread.Post(
            () => Dispatcher.UIThread.Post(
                () => _ = StartTerminalWhenReadyAsync(),
                DispatcherPriority.ApplicationIdle),
            DispatcherPriority.Loaded);
    }

    private async Task StartTerminalWhenReadyAsync()
    {
        if (_processLaunched || _currentVm == null)
            return;

        var terminal = this.FindControl<TerminalControl>("Terminal");
        if (terminal == null)
            return;

        EnsureTerminalSizeWatch(terminal);

        // 底部面板 Height=0 时控件尚未完成有效布局，推迟到可见且有尺寸后再起 shell。
        if (!IsVisible || terminal.Bounds.Width <= 1 || terminal.Bounds.Height <= 1)
        {
            Dispatcher.UIThread.Post(
                () => ScheduleShellLaunchAfterHostReady(),
                DispatcherPriority.Render);
            return;
        }

        try
        {
            SyncTerminalControlFromViewModel();
            terminal.Args = _currentVm.ShellArguments.ToArray();
            terminal.PropertyChanged -= OnTerminalControlPropertyChanged;
            terminal.PropertyChanged += OnTerminalControlPropertyChanged;

            TerminalState.ShellPath = _currentVm.ShellPath;
            TerminalState.WorkingDirectory = _currentVm.CurrentDirectory;

            await terminal.LaunchProcess();
            _currentVm.OnProcessStarted();
            _processLaunched = true;
            AttachBufferCapture(terminal);
            EnsureStateTracker();
        }
        catch
        {
            // LaunchProcess 已在内部向终端写入错误；此处不再抛
        }
    }

    private void EnsureTerminalSizeWatch(TerminalControl terminal)
    {
        if (_sizeWatchAttached)
            return;

        terminal.SizeChanged += OnTerminalControlSizeChanged;
        _sizeWatchAttached = true;
    }

    private void OnTerminalControlSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
            return;

        ScheduleShellLaunchAfterHostReady();
        if (_processLaunched)
            ScheduleTerminalSurfaceRefresh();
    }

    private void ScheduleTerminalSurfaceRefresh()
    {
        Dispatcher.UIThread.Post(RefreshTerminalSurface, DispatcherPriority.Render);
    }

    private void RefreshTerminalSurface()
    {
        if (!_processLaunched)
            return;

        var terminal = this.FindControl<TerminalControl>("Terminal");
        if (terminal == null)
            return;

        var view = terminal.GetVisualDescendants().OfType<IcicleTerminalView>().FirstOrDefault();
        view?.RequestInvalidate();
    }

    private void OnDetachedFromVisualTree(object? sender, EventArgs e) =>
        ReleaseTabResources();

    public void ReleaseTabResources()
    {
        var terminal = this.FindControl<TerminalControl>("Terminal");
        if (terminal != null)
        {
            DetachBufferCapture();
            terminal.PropertyChanged -= OnTerminalControlPropertyChanged;
            terminal.SizeChanged -= OnTerminalControlSizeChanged;
            try { terminal.Kill(); } catch { }
        }

        if (_currentVm != null)
        {
            _currentVm.PropertyChanged -= OnViewModelPropertyChanged;
            _currentVm = null;
        }

        _bufferCapture.Clear();
        _stateTracker?.Dispose();
        _stateTracker = null;
        if (_transcriptTailSyncTimer != null)
        {
            _transcriptTailSyncTimer.Stop();
            _transcriptTailSyncTimer.Tick -= OnTranscriptTailSyncTick;
            _transcriptTailSyncTimer = null;
        }

        _sizeWatchAttached = false;
        _processLaunched = false;
    }

    private void OnTerminalControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TerminalControl.CurrentDirectoryProperty)
        {
            var newDir = (sender as TerminalControl)?.CurrentDirectory;
            if (!string.IsNullOrEmpty(newDir))
            {
                TerminalState.WorkingDirectory = newDir;
                if (_currentVm != null)
                    _currentVm.CurrentDirectory = newDir;
            }
        }
    }

    private void OnTerminalProcessExited(object? sender, ProcessExitedEventArgs e)
    {
        _stateTracker?.Reset();
        _currentVm?.OnProcessExited(e.ExitCode);
        _processLaunched = false;
    }

    public string? ReadVisibleScreen() => ReadTerminalContent();

    public string? ReadSinceLastCommand()
    {
        var startLine = _currentVm?.LastCommandStartLine ?? -1;
        if (startLine < 0 && _lastCommandTextOffset < 0)
            return ReadTerminalContent();

        if (startLine >= 0 && _currentVm != null)
        {
            var terminal = this.FindControl<TerminalControl>("Terminal");
            if (terminal?.Terminal is { } xterm)
            {
                var bufferText = TerminalScreenReader.ReadSinceCommandHint(
                    xterm,
                    startLine,
                    _currentVm.LastCommandText,
                    _lastCommandBufferLengthAtSend);
                if (!string.IsNullOrEmpty(bufferText))
                    return bufferText;
            }
        }

        return ReadCapturedTextSlice(_lastCommandTextOffset);
    }

    public string? ReadSinceLastAiToolRead()
    {
        if (!_aiReadTextOffsetInitialized)
            return string.Empty;

        var slice = ReadCapturedTextSlice(_lastAiReadTextOffset);
        if (slice == null)
            return null;

        return TerminalAiReadMerge.LooksLikeReadScrollbackPollution(slice)
            ? string.Empty
            : slice;
    }

    public string? ReadLastLines(int lineCount)
    {
        lineCount = Math.Clamp(lineCount, 1, TerminalScreenReader.AiMaxLines);
        var fromBuffer = ReadLastBufferLines(lineCount);
        if (!string.IsNullOrEmpty(fromBuffer))
            return fromBuffer;

        var captured = NormalizeCapturedText(_bufferCapture.GetStrippedText());
        if (string.IsNullOrEmpty(captured))
            return string.Empty;

        var lines = captured.Split('\n');
        if (lines.Length <= lineCount)
            return captured;

        return string.Join('\n', lines[^lineCount..]);
    }

    public void CommitAiReadCursor()
    {
        var captured = NormalizeCapturedText(_bufferCapture.GetStrippedText());
        if (string.IsNullOrEmpty(captured))
            return;

        _lastAiReadTextOffset = captured.Length;
        _aiReadTextOffsetInitialized = true;
    }

    /// <summary>AI 读屏前：把 XTerm 尾部与当前行同步进 transcript，避免漏掉提示符/未换行的末行。</summary>
    public void PrepareTranscriptForAiRead()
    {
        if (_currentVm?.TranscriptService == null)
            return;

        _currentVm.TranscriptService.FlushPendingOutput(_currentVm.Id);
        SyncTranscriptTailFromBuffer();

        var terminal = this.FindControl<TerminalControl>("Terminal");
        if (terminal?.Terminal is not { } xterm)
            return;

        var lastY = TerminalScreenReader.GetLastNonEmptyBufferLineIndex(xterm);
        if (lastY < 0)
            return;

        _currentVm.TranscriptService.SyncLastScreenLine(
            _currentVm.Id,
            TerminalScreenReader.ReadLineText(xterm, lastY));
        _currentVm.TranscriptService.SetPhase(_currentVm.Id, _currentVm.CommandPhase);
    }

    /// <summary>终端输出切片：按 send 时字符偏移截取。</summary>
    public string? ReadSinceLastCommandPtySlice() => ReadCapturedTextSlice(_lastCommandTextOffset);

    public string? ReadSinceLastAiToolReadPtySlice()
    {
        if (!_aiReadTextOffsetInitialized)
            return null;

        return ReadCapturedTextSlice(_lastAiReadTextOffset);
    }

    public double GetSecondsSinceLastOutput() =>
        _stateTracker?.GetSecondsSinceLastOutput() ?? 0;

    public string? ReadTerminalContent()
    {
        var bufferText = ReadBufferText();
        if (!string.IsNullOrEmpty(bufferText))
            return bufferText;

        return NormalizeCapturedText(_bufferCapture.GetStrippedText());
    }

    private string? ReadCapturedTextSlice(int offset)
    {
        var captured = NormalizeCapturedText(_bufferCapture.GetStrippedText());
        if (captured == null)
            return null;

        if (offset < 0)
            return captured;

        if (offset >= captured.Length)
            return string.Empty;

        return captured[offset..];
    }

    private string? ReadBufferText()
    {
        var terminal = this.FindControl<TerminalControl>("Terminal");
        if (terminal?.Terminal is not { } xterm)
            return null;

        var text = TerminalScreenReader.ReadFullBuffer(xterm);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private void AttachBufferCapture(TerminalControl terminal)
    {
        if (ReferenceEquals(_bufferCaptureSource, terminal))
            return;

        DetachBufferCapture();
        _bufferCapture.Clear();
        _bufferCaptureSource = terminal;
        if (terminal.Terminal is { } xterm)
            _bufferCapture.Attach(xterm);
        _bufferCapture.OutputAppended += OnBufferOutputAppended;
    }

    private void DetachBufferCapture()
    {
        _bufferCapture.OutputAppended -= OnBufferOutputAppended;
        _bufferCapture.Detach();
        _bufferCaptureSource = null;
    }

    private void OnBufferOutputAppended(string output)
    {
        if (_currentVm?.TranscriptService is { } transcript)
        {
            transcript.AppendOutput(_currentVm.Id, output);
            ScheduleTranscriptTailSync();
        }

        _stateTracker?.NotifyOutput(output);
    }

    private void EnsureTranscriptTailSyncTimer()
    {
        if (_transcriptTailSyncTimer != null)
            return;

        _transcriptTailSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _transcriptTailSyncTimer.Tick += OnTranscriptTailSyncTick;
    }

    private void ScheduleTranscriptTailSync()
    {
        EnsureTranscriptTailSyncTimer();
        _transcriptTailSyncTimer!.Stop();
        _transcriptTailSyncTimer.Start();
    }

    private void OnTranscriptTailSyncTick(object? sender, EventArgs e)
    {
        _transcriptTailSyncTimer?.Stop();
        if (_currentVm?.TranscriptService == null)
            return;

        if (!ProbePromptVisible())
            return;

        SyncTranscriptTailFromBuffer();
    }

    private void SyncTranscriptTailFromBuffer()
    {
        if (_currentVm?.TranscriptService == null)
            return;

        var terminal = this.FindControl<TerminalControl>("Terminal");
        if (terminal?.Terminal is not { } xterm)
            return;

        const int tailLines = 32;
        var lastY = TerminalScreenReader.GetLastNonEmptyBufferLineIndex(xterm);
        if (lastY < 0)
            return;

        var endY = lastY + 1;
        var startY = Math.Max(0, endY - tailLines);
        var lines = new List<string>(endY - startY);
        for (var y = startY; y < endY; y++)
            lines.Add(TerminalScreenReader.ReadLineText(xterm, y));

        _currentVm.TranscriptService.ReplaceTailFromScreen(_currentVm.Id, lines, tailLines);
    }

    private void EnsureStateTracker()
    {
        _stateTracker?.Dispose();
        _stateTracker = new TerminalSessionStateTracker();
        _stateTracker.SetPromptProbe(ProbePromptVisible);
        _stateTracker.PhaseChanged += phase =>
        {
            if (_currentVm == null)
                return;

            _currentVm.SetCommandPhase(phase);
            _currentVm.TranscriptService?.SetPhase(_currentVm.Id, phase);

            if (phase == TerminalCommandPhase.Idle)
                SyncTranscriptTailFromBuffer();
        };
        _stateTracker.Reset();
    }

    private bool ProbePromptVisible()
    {
        if (_currentVm == null)
            return false;

        var line = TerminalPromptDetector.GetLastNonEmptyLine(ReadLastBufferLines(TerminalScreenReader.PromptProbeLines));
        return TerminalPromptDetector.LooksLikePrompt(
            line,
            _currentVm.IsCmd,
            _currentVm.IsPowerShell,
            _currentVm.IsBash);
    }

    private string? ReadLastBufferLines(int lineCount)
    {
        var terminal = this.FindControl<TerminalControl>("Terminal");
        if (terminal?.Terminal is not { } xterm)
            return null;

        var text = TerminalScreenReader.ReadLastLines(xterm, lineCount);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? NormalizeCapturedText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        while (text.EndsWith('\n'))
            text = text[..^1];

        return string.IsNullOrEmpty(text) ? null : text;
    }
}
