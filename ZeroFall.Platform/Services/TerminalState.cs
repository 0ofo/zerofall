using System;

namespace ZeroFall.Platform.Services;

public static class TerminalState
{
    public static string ShellPath { get; set; } = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";

    public static string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
