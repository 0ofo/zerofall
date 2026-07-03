using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Base.Events;
using ZeroFall.Browser.Serialization;
using ZeroFall.Platform.Events;

namespace ZeroFall.Browser.Services;

/// <summary>在临时浏览器标签注入 HTML 后按 <c>page_content</c> 逻辑提取内容。</summary>
public sealed class CdpHtmlInjectionService
{
    private const int TabReadyTimeoutSeconds = 45;

    private readonly ICdpBridge _cdpBridge;
    private readonly IEventBus _eventBus;

    public CdpHtmlInjectionService(ICdpBridge cdpBridge, IEventBus eventBus)
    {
        _cdpBridge = cdpBridge;
        _eventBus = eventBus;
    }

    public async Task<string> ExtractAsync(
        string html,
        string pageUrl,
        bool isMd,
        bool withImages,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var tabId = $"fetch-{Guid.NewGuid():N}";
        try
        {
            _eventBus.Publish(new OpenBrowserTabRequestedEvent("about:blank", "fetch", tabId));

            if (!await WaitForSessionAsync(tabId, TimeSpan.FromSeconds(TabReadyTimeoutSeconds), cancellationToken)
                    .ConfigureAwait(false))
                return "浏览器标签未能就绪（请先确保浏览器模块已加载）";

            cancellationToken.ThrowIfCancellationRequested();

            await _cdpBridge.CallMethodOnTabAsync(tabId, "Page.enable", "{}").ConfigureAwait(false);
            await _cdpBridge.CallMethodOnTabAsync(tabId, "Runtime.enable", "{}").ConfigureAwait(false);

            var navJson = await _cdpBridge.CallMethodOnTabAsync(
                tabId,
                "Page.navigate",
                BrowserJson.Serialize(new PageNavigateParams { Url = "about:blank" })).ConfigureAwait(false);
            if (CdpEvaluateParser.TryGetProtocolError(navJson, out var navErr))
                return $"导航失败: {navErr}";

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);

            var frameId = await GetMainFrameIdAsync(tabId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(frameId))
                return "无法获取主 frame";

            var preparedHtml = PrepareHtmlWithBase(html, pageUrl);
            var setContentJson = await _cdpBridge.CallMethodOnTabAsync(
                tabId,
                "Page.setDocumentContent",
                BrowserJson.Serialize(new PageSetDocumentContentParams
                {
                    FrameId = frameId,
                    Html = preparedHtml
                })).ConfigureAwait(false);
            if (CdpEvaluateParser.TryGetProtocolError(setContentJson, out var setErr))
                return $"注入 HTML 失败: {setErr}";

            return await CdpPageContentExtractor.ExtractAsync(
                (method, parameters) => _cdpBridge.CallMethodOnTabAsync(tabId, method, parameters),
                isMd,
                withImages).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"HTML 转换失败: {ex.Message}";
        }
        finally
        {
            if (_cdpBridge.HasSession(tabId))
                _eventBus.Publish(new CloseContentTabRequestedEvent(tabId));
        }
    }

    private static string PrepareHtmlWithBase(string html, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)
            || html.Contains("<base", StringComparison.OrdinalIgnoreCase))
            return html;

        var baseTag = $"<base href=\"{System.Net.WebUtility.HtmlEncode(pageUrl)}\">";
        if (html.Contains("<head", StringComparison.OrdinalIgnoreCase))
        {
            return System.Text.RegularExpressions.Regex.Replace(
                html,
                "(<head[^>]*>)",
                $"$1{baseTag}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return baseTag + html;
    }

    private async Task<string?> GetMainFrameIdAsync(string tabId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var json = await _cdpBridge.CallMethodOnTabAsync(tabId, "Page.getFrameTree", "{}").ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("frameTree", out var tree)
                && tree.TryGetProperty("frame", out var frame)
                && frame.TryGetProperty("id", out var idEl))
                return idEl.GetString();
        }
        catch
        {
            // fall through
        }

        return null;
    }

    private async Task<bool> WaitForSessionAsync(string tabId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_cdpBridge.HasSession(tabId))
                return true;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        return false;
    }
}
