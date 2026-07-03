using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroFall.Base.AiTools;

/// <summary>AI 工具全局串行队列：顺序执行。少数工具由调用方显式豁免，不占此锁。</summary>
public static class AiToolExecutionQueue
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ToolCallResult> RunAsync(
        Func<CancellationToken, Task<ToolCallResult>> work,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
