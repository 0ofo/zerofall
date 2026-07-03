using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ZeroFall.Terminal;

internal static class UiThreadHelper
{
    public static Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Dispatcher.UIThread.CheckAccess())
            return Task.FromResult(action());

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    tcs.TrySetCanceled(cancellationToken);
                else
                    tcs.TrySetResult(action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    public static Task RunAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Dispatcher.UIThread.CheckAccess())
            return action();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    tcs.TrySetCanceled(cancellationToken);
                else
                {
                    await action().ConfigureAwait(true);
                    tcs.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }
}
