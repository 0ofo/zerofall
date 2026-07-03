using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Browser.Serialization;

namespace ZeroFall.Browser.Services;

/// <summary>
/// 通过 CDP 引擎级 DOM 探测等待文档可用（不注入页面 JS，避免反调试站拦截 readyState）。
/// AOT WebView2 封装暂未订阅 <c>Page.loadEventFired</c> 事件，故以 <c>Page.enable</c> + <c>DOM.getDocument</c> 轮询代替。
/// </summary>
internal static class CdpDocumentLoadWaiter
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static async Task<bool> WaitForDocumentReadyAsync(
        Func<string, string, Task<string>> callCdpAsync,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            return false;

        _ = await callCdpAsync("Page.enable", "{}").ConfigureAwait(false);
        _ = await callCdpAsync("DOM.enable", "{}").ConfigureAwait(false);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsDocumentAvailableAsync(callCdpAsync).ConfigureAwait(false))
                return true;

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> IsDocumentAvailableAsync(Func<string, string, Task<string>> callCdpAsync)
    {
        var raw = await callCdpAsync(
            "DOM.getDocument",
            BrowserJson.Serialize(new DomGetDocumentParams { Depth = 0, Pierce = false })).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out _))
                return false;

            return doc.RootElement.TryGetProperty("root", out var root)
                   && root.TryGetProperty("nodeId", out var nodeIdEl)
                   && nodeIdEl.TryGetInt32(out var nodeId)
                   && nodeId > 0;
        }
        catch
        {
            return false;
        }
    }
}
