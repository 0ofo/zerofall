using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ZeroFall.Platform.Services;

/// <summary>
/// UI 线程边界：只允许界面更新；await 后在后台线程继续。
/// </summary>
public static class UiThreadBridge
{
    public static void Post(Action action) =>
        Dispatcher.UIThread.Post(action, DispatcherPriority.Normal);

    public static void PostBackground(Action action) =>
        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

    public static Task InvokeAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    public static Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                tcs.TrySetResult(await func().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }
}
