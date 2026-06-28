using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroFall.Platform.Services;

/// <summary>
/// WebView2 初始化顺序：AI 聊天适配器就绪后，Content 浏览器才能创建；且同一时刻只允许一个 InitializeAsync。
/// </summary>
public static class WebView2CreationCoordinator
{
    private static readonly SemaphoreSlim InitGate = new(1, 1);
    private static readonly SemaphoreSlim AiReadyGate = new(0, 1);
    private static int _aiAdapterReady;

    public static bool IsAiAdapterReady => Volatile.Read(ref _aiAdapterReady) != 0;

    public static async Task WaitUntilAiAdapterReadyAsync(CancellationToken cancellationToken = default)
    {
        if (IsAiAdapterReady)
            return;

        await AiReadyGate.WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    public static void MarkAiAdapterReady()
    {
        if (Interlocked.Exchange(ref _aiAdapterReady, 1) != 0)
            return;

        try
        {
            AiReadyGate.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public static Task WaitForInitAsync(CancellationToken cancellationToken = default) =>
        InitGate.WaitAsync(cancellationToken);

    public static void ReleaseInit()
    {
        try
        {
            InitGate.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public static void ArmInitRelease(int delayMs, Action? onTimeout = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                onTimeout?.Invoke();
                ReleaseInit();
            }
            catch
            {
                ReleaseInit();
            }
        });
    }
}
