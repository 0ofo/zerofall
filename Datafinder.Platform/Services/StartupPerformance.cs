using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Datafinder.Platform.Services;

/// <summary>
/// 启动调度：在加载遮罩仍可见时分帧构建 UI，遮罩关闭后再启动 WebView/终端/代理等重操作。
/// </summary>
public static class StartupPerformance
{
    private static int _layoutReady;

    /// <summary>Dock 布局与 Tab 内容已就绪（加载遮罩已可关闭）。</summary>
    public static bool IsLayoutReady => Volatile.Read(ref _layoutReady) != 0;

    public static void MarkLayoutReady() => Interlocked.Exchange(ref _layoutReady, 1);

    /// <summary>让出一帧 UI 时间，使 ProgressBar 等动画继续刷新。</summary>
    public static Task YieldUiFrameAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => tcs.TrySetResult(), DispatcherPriority.Render);
        return tcs.Task;
    }

    /// <summary>让启动遮罩可见若干帧（ProgressBar 需多帧才能动起来）。</summary>
    public static Task YieldUiFramesAsync(int frames)
    {
        if (frames <= 0)
            return Task.CompletedTask;

        return YieldUiFramesCoreAsync(frames);
    }

    private static async Task YieldUiFramesCoreAsync(int frames)
    {
        for (var i = 0; i < frames; i++)
            await YieldUiFrameAsync().ConfigureAwait(false);
    }

    /// <summary>在后台延迟后在 UI 线程执行（不阻塞调用方）。</summary>
    public static void RunAfterDelay(Action action, int delayMs)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0)
                    await Task.Delay(delayMs).ConfigureAwait(false);
                Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartupPerformance] RunAfterDelay failed: {ex}");
            }
        });
    }

    /// <summary>等到 Loaded 后再排到 ApplicationIdle。</summary>
    public static void RunOnUiIdle(Action action)
    {
        Dispatcher.UIThread.Post(
            () => Dispatcher.UIThread.Post(action, DispatcherPriority.ApplicationIdle),
            DispatcherPriority.Loaded);
    }

    /// <summary>在 UI 队列末尾执行（WebView 等重操作，避免与首帧布局争抢）。</summary>
    public static void RunLastOnUiThread(Action action)
    {
        RunOnUiIdle(() => RunOnUiIdle(() => RunOnUiIdle(action)));
    }
}
