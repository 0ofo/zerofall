using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Base.AiTools;

namespace ZeroFall.AiPanel.Services;

/// <summary>AI 工具统一经 <see cref="AiToolExecutionQueue"/> 串行调度；仅 <c>fetch</c> 与 <c>spawn_agent</c> 绕过队列。</summary>
internal static class AiToolUiDispatcher
{
    public static Task<ToolCallResult> ExecuteAsync(
        AiToolRegistry registry,
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        if (BypassesExecutionQueue(toolName))
            return registry.ExecuteAsync(toolName, argumentsJson, cancellationToken);

        return AiToolExecutionQueue.RunAsync(
            ct => registry.ExecuteAsync(toolName, argumentsJson, ct),
            cancellationToken);
    }

    private static bool BypassesExecutionQueue(string toolName) =>
        string.Equals(toolName, "fetch", StringComparison.Ordinal)
        || string.Equals(toolName, "spawn_agent", StringComparison.Ordinal);
}
