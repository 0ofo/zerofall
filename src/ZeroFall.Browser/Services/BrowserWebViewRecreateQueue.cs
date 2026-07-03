using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace ZeroFall.Browser.Services;

/// <summary>
/// 串行化各浏览器标签的 WebView2 重建，避免多标签同时创建环境导致 UI 卡死或进程崩溃。
/// </summary>
internal static class BrowserWebViewRecreateQueue
{
    private static readonly object Gate = new();
    private static readonly Queue<Action> Queue = new();
    private static bool _draining;

    public static void Enqueue(Action recreateAction)
    {
        lock (Gate)
        {
            Queue.Enqueue(recreateAction);
            if (_draining)
                return;
            _draining = true;
        }

        Dispatcher.UIThread.Post(DrainNext, DispatcherPriority.Background);
    }

    private static void DrainNext()
    {
        Action? action;
        lock (Gate)
        {
            if (Queue.Count == 0)
            {
                _draining = false;
                return;
            }

            action = Queue.Dequeue();
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BrowserWebViewRecreateQueue] {ex}");
        }

        Dispatcher.UIThread.Post(DrainNext, DispatcherPriority.Background);
    }
}
