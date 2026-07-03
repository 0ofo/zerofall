using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.Terminal.ViewModels;

public partial class TerminalViewModel : ViewModelBase
{
    private static readonly IList<string> BashInteractiveArgs = new[] { "--login", "-i" };
    private static readonly IList<string> CmdInteractiveArgs = new[] { "/K" };
    private static readonly IList<string> PowerShellNoLogoArgs = new[] { "-NoLogo", "-NoExit" };

    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string _title = "终端";

    [ObservableProperty]
    private string _fontFamily = "SimHei, SimSun, NSimSun, monospace";

    [ObservableProperty]
    private int _fontSize = 13;

    [ObservableProperty]
    private bool _isProcessRunning;

    [ObservableProperty]
    private string _currentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty]
    private string? _pendingCommand;

    [ObservableProperty]
    private TerminalCommandPhase _commandPhase = TerminalCommandPhase.Unknown;

    /// <summary>最近一次 send_terminal_command 写入前 XTerm buffer 行号（-1 表示尚无记录）。</summary>
    public int LastCommandStartLine { get; private set; } = -1;

    /// <summary>最近一次 send_terminal_command 的命令文本（用于 buffer 定位）。</summary>
    public string? LastCommandText { get; private set; }

    public ITerminalTranscriptService? TranscriptService { get; set; }

    public bool IsAwaitingCommand => CommandPhase == TerminalCommandPhase.Idle;

    public bool IsCommandExecuting => CommandPhase == TerminalCommandPhase.Executing;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string ShellPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return ResolveWindowsCmdPath();
            return "/bin/bash";
        }
    }

    /// <summary>
    /// Git Bash 需要 login + interactive；cmd 传 /K 可抑制启动版权横幅；PowerShell 用 -NoLogo。
    /// </summary>
    public IList<string> ShellArguments
    {
        get
        {
            if (IsBash)
                return BashInteractiveArgs;
            if (IsPowerShell)
                return PowerShellNoLogoArgs;
            if (IsCmd)
                return CmdInteractiveArgs;
            return Array.Empty<string>();
        }
    }

    public bool IsBash => ShellPath.EndsWith("bash.exe", StringComparison.OrdinalIgnoreCase)
        || ShellPath.EndsWith("bash", StringComparison.OrdinalIgnoreCase);

    public bool IsCmd => OperatingSystem.IsWindows()
        && ShellPath.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase);

    public bool IsPowerShell => ShellPath.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase)
        || ShellPath.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase);

    public TerminalViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        ApplyTerminalSettings(_settingsService.Load().Terminal);
    }

    private static string ResolveWindowsCmdPath()
    {
        var comspec = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec))
            return comspec;

        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windir))
            windir = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";

        var isWow64 = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") != null;
        if (isWow64)
        {
            var sysnative = Path.Combine(windir, "Sysnative", "cmd.exe");
            if (File.Exists(sysnative))
                return sysnative;
        }

        var system32 = Path.Combine(windir, "System32", "cmd.exe");
        if (File.Exists(system32))
            return system32;

        return "cmd.exe";
    }

    private void ApplyTerminalSettings(TerminalSettings config)
    {
        if (!string.IsNullOrWhiteSpace(config.FontFamily))
            FontFamily = config.FontFamily.Trim();
        if (config.FontSize > 0)
            FontSize = config.FontSize;
    }

    [RelayCommand]
    private void RestartTerminal()
    {
        PendingCommand = IsCmd ? "cls" : "clear";
    }

    public void OnProcessExited(int exitCode)
    {
        IsProcessRunning = false;
        CommandPhase = TerminalCommandPhase.Unknown;
    }

    public void OnProcessStarted()
    {
        IsProcessRunning = true;
    }

    public void SetCommandPhase(TerminalCommandPhase phase)
    {
        if (CommandPhase == phase)
            return;

        CommandPhase = phase;
    }

    public void SetLastCommandStartLine(int line) => LastCommandStartLine = line;

    public void SetLastCommandText(string? command) => LastCommandText = command;

    partial void OnCommandPhaseChanged(TerminalCommandPhase value)
    {
        OnPropertyChanged(nameof(IsAwaitingCommand));
        OnPropertyChanged(nameof(IsCommandExecuting));
    }
}
