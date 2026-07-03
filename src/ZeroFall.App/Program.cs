using Avalonia;
using System;
using System.Threading.Tasks;
using ZeroFall.Base.Diagnostics;

namespace ZeroFall;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDiagnostics.StartSession("Program.Main");
        AppDiagnostics.Mark("Main entered");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var text = e.ExceptionObject is Exception exception ? exception.ToString() : e.ExceptionObject?.ToString() ?? "unknown";
            Console.Error.WriteLine($"[App] Unhandled: {text}");
            System.Diagnostics.Debug.WriteLine($"[App] Unhandled: {text}");
            if (e.ExceptionObject is Exception unhandledException)
                AppDiagnostics.Exception("AppDomain unhandled", unhandledException);
            else
                AppDiagnostics.Text($"AppDomain unhandled: {text}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[App] Unobserved task: {e.Exception}");
            System.Diagnostics.Debug.WriteLine($"[App] Unobserved task: {e.Exception}");
            AppDiagnostics.Exception("TaskScheduler unobserved", e.Exception);
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppDiagnostics.Mark("ProcessExit");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppDiagnostics.Mark("Main leaving");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<ZeroFall.App.App>()
            .UsePlatformDetect()
            .LogToTrace();
}
