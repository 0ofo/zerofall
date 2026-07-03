using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ZeroFall.Base.Diagnostics;

public static class AppDiagnostics
{
    private static readonly object Gate = new();
    private static readonly int ProcessId = Environment.ProcessId;
    private static bool _sessionStarted;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZeroFall",
        "logs");

    public static string LatestLogPath => Path.Combine(LogDirectory, "latest.log");

    public static string CrashLogPath => Path.Combine(LogDirectory, "crash.log");

    public static void StartSession(string source)
    {
        lock (Gate)
        {
            if (_sessionStarted)
                return;

            _sessionStarted = true;
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.WriteAllText(
                    LatestLogPath,
                    $"{LinePrefix()} SESSION START {source}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never affect application behavior.
            }
        }
    }

    public static void Mark(string message) => Append(LatestLogPath, $"MARK {message}");

    public static void Heartbeat(string message) => Append(LatestLogPath, $"HEARTBEAT {message}");

    public static void Exception(string message, Exception exception)
    {
        var text = $"{message}{Environment.NewLine}{exception}";
        Append(LatestLogPath, $"EXCEPTION {text}");
        Append(CrashLogPath, $"EXCEPTION {text}");
    }

    public static void Text(string message) => Append(LatestLogPath, $"TEXT {message}");

    private static void Append(string path, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(path, $"{LinePrefix()} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppDiagnostics] write failed: {ex.Message}");
        }
    }

    private static string LinePrefix()
    {
        return $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} pid={ProcessId} tid={Environment.CurrentManagedThreadId}";
    }
}
