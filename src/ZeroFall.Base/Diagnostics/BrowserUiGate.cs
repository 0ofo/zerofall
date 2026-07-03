using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroFall.Base.Diagnostics;

/// <summary>
/// Content 浏览器与 AI 聊天 WebView 共用 UI 线程；所有 ExecuteScript/CDP 必须经此互斥，避免双 WebView 交叉调用导致挂死。
/// </summary>
public static class BrowserUiGate
{
    private static readonly SemaphoreSlim ScriptGate = new(1, 1);
    private static int _browserToolRoundDepth;

    public static bool IsBusy => ScriptGate.CurrentCount == 0;

    public static bool IsBrowserToolRoundActive => Volatile.Read(ref _browserToolRoundDepth) > 0;

    public static bool IsBrowserStackTool(string toolName) =>
        toolName.StartsWith("browser_", StringComparison.Ordinal)
        || string.Equals(toolName, "page_content", StringComparison.Ordinal);

    public static void EnterBrowserToolRound() =>
        Interlocked.Increment(ref _browserToolRoundDepth);

    public static void ExitBrowserToolRound() =>
        Interlocked.Decrement(ref _browserToolRoundDepth);

    public static void Enter()
    {
        ScriptGate.Wait();
    }

    public static void Exit()
    {
        ScriptGate.Release();
    }

    public static Task EnterAsync(CancellationToken cancellationToken = default) =>
        ScriptGate.WaitAsync(cancellationToken);
}
