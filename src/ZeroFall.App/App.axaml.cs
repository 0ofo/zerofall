using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZeroFall.App.Services;
using ZeroFall.App.ViewModels;
using ZeroFall.App.Views;
using ZeroFall.Base.Diagnostics;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace ZeroFall.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private DispatcherTimer? _diagnosticHeartbeatTimer;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppDiagnostics.Mark("FrameworkInitializationCompleted entered");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            mainWindow.Activate();
            AppDiagnostics.Mark("MainWindow shown");

            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine($"[App] UI Unhandled: {e.Exception}");
                System.Diagnostics.Debug.WriteLine($"[App] UI Unhandled: {e.Exception}");
                AppDiagnostics.Exception("Dispatcher UI unhandled", e.Exception);

                if (e.Exception is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x8007139F) })
                    e.Handled = true;
            };

            StartDiagnosticHeartbeat();
            desktop.Exit += (_, _) =>
            {
                AppDiagnostics.Mark("Desktop lifetime exit");
                _diagnosticHeartbeatTimer?.Stop();
            };

            _ = InitializeAsync(mainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAsync(MainWindow mainWindow)
    {
        try
        {
            AppDiagnostics.Mark("InitializeAsync begin");
            // 空窗 + 遮罩动画（前 3 帧）
            await StartupPerformance.YieldUiFramesAsync(3);

            AppDiagnostics.Mark("Build services begin");
            Services = await Task.Run(AppModuleBootstrap.Build);
            AppDiagnostics.Mark("Build services completed");
            var mainWindowViewModel = Services.GetRequiredService<MainWindowViewModel>();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AppDiagnostics.Mark("Main content injection begin");
                mainWindow.DataContext = mainWindowViewModel;
                mainWindow.TrySubscribeEvents();
                mainWindow.InjectMainContent(new MainContentView { DataContext = mainWindowViewModel });
                AppDiagnostics.Mark("Main content injection completed");
            });

            // 遮罩保持：Dock Tab 注册 + 物化完成后再关闭
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                AppDiagnostics.Mark("Dock layout initialization begin");
                await mainWindowViewModel.InitializeDockLayoutAsync();
                mainWindowViewModel.CompleteStartupLayout();
                mainWindowViewModel.DockLayout.SyncShellPanelVisibility();
                mainWindow.SyncShellPanelLayout(mainWindowViewModel.DockLayout);
                AppDiagnostics.Mark("Dock layout initialization completed");
            });

            await StartupPerformance.YieldUiFramesAsync(2);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainWindow.HideLoadingOverlay();
                StartupPerformance.MarkLayoutReady();
                AppDiagnostics.Mark("Startup layout ready");

                WebViewStartupCoordinator.Schedule(
                    mainWindowViewModel.DockLayout,
                    Services.GetRequiredService<IEventBus>());
            });

            StartupPerformance.RunAfterDelay(() =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    AppDiagnostics.Mark("TryRestoreLastProject begin");
                    mainWindowViewModel.TryRestoreLastProject();
                    if (mainWindow.DataContext is MainWindowViewModel vm)
                    {
                        mainWindow.SyncShellPanelLayout(vm.DockLayout);
                        if (vm.HasProject)
                            vm.DockLayout.CompleteStartupLayout();
                    }
                    AppDiagnostics.Mark("TryRestoreLastProject completed");
                }, DispatcherPriority.Background);
            }, delayMs: 300);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] InitializeAsync failed: {ex}");
            Console.Error.WriteLine($"[App] InitializeAsync failed: {ex}");
            AppDiagnostics.Exception("InitializeAsync failed", ex);
            await Dispatcher.UIThread.InvokeAsync(mainWindow.HideLoadingOverlay);
        }
    }

    private void StartDiagnosticHeartbeat()
    {
        _diagnosticHeartbeatTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _diagnosticHeartbeatTimer.Tick += (_, _) => AppDiagnostics.Heartbeat("UI thread alive");
        _diagnosticHeartbeatTimer.Start();
    }
}
