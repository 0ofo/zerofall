using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ZeroFall.Base.AiTools;
using ZeroFall.Base.Diagnostics;
using ZeroFall.Browser.Serialization;
using ZeroFall.Browser.Services;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Services;
using ZeroFall.Traffic.Ingest;
namespace ZeroFall.Browser.Tools;

public class BrowserAiToolService : IDisposable
{
    private const int DefaultBrowserReadyTimeoutSeconds = 3;
    private const int MaxBrowserReadyTimeoutSeconds = 30;

    private readonly ICdpBridge _cdpBridge;
    private readonly IBrowserTabManager _tabManager;
    private readonly WebsiteTreeViewModel _websiteTree;
    private readonly IOutboundHttpClientFactory _httpClientFactory;
    private readonly CdpHtmlInjectionService _htmlInjection;
    private readonly ITrafficCaptureSink _captureSink;
    private readonly IAiChatRunContext _runContext;

    public BrowserAiToolService(
        ICdpBridge cdpBridge,
        IBrowserTabManager tabManager,
        WebsiteTreeViewModel websiteTree,
        IOutboundHttpClientFactory httpClientFactory,
        CdpHtmlInjectionService htmlInjection,
        ITrafficCaptureSink captureSink,
        IAiChatRunContext runContext)
    {
        _cdpBridge = cdpBridge;
        _tabManager = tabManager;
        _websiteTree = websiteTree;
        _httpClientFactory = httpClientFactory;
        _htmlInjection = htmlInjection;
        _captureSink = captureSink;
        _runContext = runContext;
    }

    public void Dispose()
    {
    }

    [AiTool("browser_tab",
        """
        管理 Content 区浏览器标签。action=open|navigate|reload|list|switch|close。
        open 新建标签并默认最多等 3 秒；navigate 在已有/当前标签跳转；reload 刷新；list/switch/close 管理标签。
        打开后读页面内容用 page_content（传 tabId），page_content 也会短等页面就绪，二者可形成轮询。
        SPA/反 CDP 站：正文可能在 XHR 里，优先 sql→http_traffic_entries 或 browser_website_tree，勿死磕 page_content。
        """)]
    public async Task<string> BrowserTabAsync(
        [ToolParam("open|navigate|reload|list|switch|close")] string action,
        [ToolParam("URL，open/navigate 必填", Required = false)] string? url = null,
        [ToolParam("目标标签 Id，navigate/reload/switch/close 可选或必填", Required = false)] string? tabId = null,
        [ToolParam("标签标题，open 可选", Required = false)] string? title = null,
        [ToolParam("open 后是否立即切换到该标签，默认 true", Required = false)] bool activate = true,
        [ToolParam("reload 是否忽略缓存，默认 false", Required = false)] bool ignoreCache = false,
        [ToolParam("等待页面就绪秒数，open 默认 3，最大 30", Required = false)] int readyTimeoutSeconds = DefaultBrowserReadyTimeoutSeconds)
    {
        var act = (action ?? string.Empty).Trim().ToLowerInvariant();
        AppDiagnostics.Mark($"Tool browser_tab begin action={act} tab={tabId ?? ""} urlLength={url?.Length ?? 0}");
        string result = act switch
        {
            "open" => string.IsNullOrWhiteSpace(url)
                ? ToolResultJson.Error("open 需要 url")
                : await OpenTab(url, title, activate, readyTimeoutSeconds).ConfigureAwait(false),
            "navigate" => string.IsNullOrWhiteSpace(url)
                ? ToolResultJson.Error("navigate 需要 url")
                : await NavigateAsync(url, false, tabId).ConfigureAwait(false),
            "reload" => await ReloadAsync(ignoreCache, tabId).ConfigureAwait(false),
            "list" => await TabsAsync("list").ConfigureAwait(false),
            "switch" => string.IsNullOrWhiteSpace(tabId)
                ? ToolResultJson.Error("switch 需要 tabId")
                : await TabsAsync("switch", tabId).ConfigureAwait(false),
            "close" => string.IsNullOrWhiteSpace(tabId)
                ? ToolResultJson.Error("close 需要 tabId")
                : await TabsAsync("close", tabId).ConfigureAwait(false),
            _ => ToolResultJson.Error("action 必须是 open、navigate、reload、list、switch、close")
        };
        AppDiagnostics.Mark($"Tool browser_tab end action={act} resultLength={result?.Length ?? 0}");
        return result ?? string.Empty;
    }

    public async Task<string> OpenTab(
        [ToolParam("完整 URL")] string url,
        [ToolParam("标签标题（可选）", Required = false)] string? title = null,
        [ToolParam("是否立即切换到该标签", Required = false)] bool activate = true,
        [ToolParam("等待页面就绪秒数，默认 3，最大 30", Required = false)] int readyTimeoutSeconds = DefaultBrowserReadyTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ToolResultJson.Error("url 不能为空");

        var tabId = await _tabManager.OpenTabAsync(url.Trim(), title, activate).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tabId))
            return ToolResultJson.Error("打开标签失败");

        var deadline = DateTime.UtcNow + GetReadyTimeout(readyTimeoutSeconds);
        if (!await _cdpBridge.WaitForSessionAsync(tabId, GetRemaining(deadline)).ConfigureAwait(false))
        {
            return ToolResultJson.Data(o =>
            {
                o["tabId"] = tabId;
                o["url"] = url.Trim();
                o["cdpReady"] = false;
                o["documentReady"] = false;
                o["readyTimeoutSeconds"] = GetReadyTimeoutSeconds(readyTimeoutSeconds);
                o["warning"] = "WebView 尚未就绪，可稍后轮询 page_content";
            });
        }

        var documentReady = await WaitForDocumentReadyAsync(tabId, GetRemaining(deadline)).ConfigureAwait(false);

        return ToolResultJson.Data(o =>
        {
            o["tabId"] = tabId;
            o["url"] = url.Trim();
            o["cdpReady"] = true;
            o["documentReady"] = documentReady;
            o["readyTimeoutSeconds"] = GetReadyTimeoutSeconds(readyTimeoutSeconds);
        });
    }

    public async Task<string> TabsAsync(
        [ToolParam("list、switch 或 close")] string action,
        [ToolParam("标签 Id（switch/close 必填）", Required = false)] string? tabId = null)
    {
        var act = (action ?? "").Trim().ToLowerInvariant();
        return act switch
        {
            "list" => _tabManager.ListTabsJson(),
            "switch" => string.IsNullOrWhiteSpace(tabId)
                ? ToolResultJson.Error("switch 需要 tabId")
                : await _tabManager.SwitchTabAsync(tabId.Trim()).ConfigureAwait(false),
            "close" => string.IsNullOrWhiteSpace(tabId)
                ? ToolResultJson.Error("close 需要 tabId")
                : await _tabManager.CloseTabAsync(tabId.Trim()).ConfigureAwait(false),
            _ => ToolResultJson.Error("action 必须是 list、switch 或 close")
        };
    }

    [AiTool("browser_cdp",
        """
        调用 Chrome DevTools Protocol 方法。执行页面 JavaScript 时使用 method=Runtime.evaluate，
        parameters 传 {"expression":"...","returnByValue":true,"awaitPromise":true}。
        可传 tabId 指定标签；未传则使用当前活动标签。
        """)]
    public async Task<string> CallCdpAsync(
        [ToolParam("CDP 方法名，如 Runtime.evaluate、DOM.getDocument、Network.getCookies")] string method,
        [ToolParam("CDP 方法参数 JSON", Required = false)] string? parameters = null,
        [ToolParam("目标标签 Id（可选）", Required = false)] string? tabId = null)
    {
        if (string.IsNullOrWhiteSpace(method))
            return "方法名不能为空";

        var id = _tabManager.ResolveTabId(tabId);
        if (id == null)
            return "没有可用的浏览器标签，请先 browser_tab action=open 或 browser_tab action=list";

        if (!_cdpBridge.HasSession(id)
            && !await _cdpBridge.WaitForSessionAsync(id, TimeSpan.FromSeconds(20)).ConfigureAwait(false))
            return $"浏览器标签 {id} CDP 未就绪";

        return await _cdpBridge.CallMethodOnTabAsync(id, method, parameters);
    }

    public async Task<string> NavigateAsync(
        [ToolParam("完整 URL")] string url,
        [ToolParam("true=新建标签页打开", Required = false)] bool newTab = false,
        [ToolParam("已有标签 Id（与 newTab 二选一）", Required = false)] string? tabId = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "url 不能为空";

        if (newTab)
        {
            var newId = await _tabManager.OpenTabAsync(url.Trim(), null, true).ConfigureAwait(false);
            if (string.IsNullOrEmpty(newId))
                return "打开新标签失败";

            if (!await _cdpBridge.WaitForSessionAsync(newId, TimeSpan.FromSeconds(45)).ConfigureAwait(false))
                return $"已创建标签 tabId: {newId}，CDP 尚未就绪";

            var navParam = BrowserJson.Serialize(new PageNavigateParams { Url = url.Trim() });
            var navResult = await _cdpBridge.CallMethodOnTabAsync(newId, "Page.navigate", navParam);
            return $"已在新标签打开\n tabId: {newId}\n url: {url.Trim()}\n{navResult}";
        }

        var id = _tabManager.ResolveTabId(tabId);
        if (id == null)
            return "没有可用的浏览器标签，请 browser_tab action=open 或 browser_tab action=list";

        if (!_cdpBridge.HasSession(id)
            && !await _cdpBridge.WaitForSessionAsync(id, TimeSpan.FromSeconds(20)).ConfigureAwait(false))
            return $"浏览器标签 {id} CDP 未就绪";

        await _tabManager.SwitchTabAsync(id).ConfigureAwait(false);
        var param = BrowserJson.Serialize(new PageNavigateParams { Url = url.Trim() });
        var result = await _cdpBridge.CallMethodOnTabAsync(id, "Page.navigate", param);
        return $"标签 {id} 导航中\n{result}";
    }

    public async Task<string> ReloadAsync(
        [ToolParam("是否忽略缓存", Required = false)] bool ignoreCache = false,
        [ToolParam("目标标签 Id（可选）", Required = false)] string? tabId = null)
    {
        var param = BrowserJson.Serialize(new PageReloadParams { IgnoreCache = ignoreCache });
        return await CallCdpOnTabAsync("Page.reload", param, tabId);
    }

    [AiTool("fetch",
        """
        出站 HTTP 单条请求（默认 GET）。默认 isAll=true 返回扁平 JSON：status、headers、body、latencyMs、entryId 等；失败时含 ok:false。
        isAll=false 时只返回响应正文；HTML 响应按 isMd 转 Markdown 或返回 raw HTML，适合搜索/阅读场景。
        示例 fetch(url="https://www.baidu.com")：HTML 响应且 isMd=true（默认）时 body 为 Markdown。
        支持 method、headers（JSON 对象）、cookies（Cookie 头简写）、body、isAll、isMd、withImages、tabId（合并浏览器 Cookie）。
        POST/PUT/PATCH 且 body 为 key=value（& 连接）时，未指定 Content-Type 会自动使用 application/x-www-form-urlencoded，并写入 Content-Length（避免 chunked 导致部分 PHP/nginx 收不到 $_POST）；返回含 requestHeaders（实际发出的请求头）。
        不执行页面 JS；强 JS/SPA/反 CDP 页请 browser_tab action=open 触发浏览，再用 sql→http_traffic_entries 读 API 响应；page_content 仅作 DOM 快照补充。出站会写入 http_traffic_entries，可用 sql 复盘。
        多条有策略的请求（爆破、遍历、条件分支）请在工作区写脚本后用终端执行，不要堆几十次 fetch。
        连接地址仅来自 url；显式 Host 头原样发送（虚拟主机/反代），不得用 Host 决定 TCP 连接目标。
        """)]
    public async Task<string> FetchAsync(
        [ToolParam("完整 URL")] string url,
        [ToolParam("HTTP 方法，默认 GET", Required = false)] string method = "GET",
        [ToolParam("请求头 JSON 对象", Required = false)] string? headers = null,
        [ToolParam("Cookie 头（分号分隔 name=value），优先于 headers.Cookie", Required = false)] string? cookies = null,
        [ToolParam("请求体，默认空", Required = false)] string? body = "",
        [ToolParam("合并 Cookie 时使用的浏览器标签 Id（可选）", Required = false)] string? tabId = null,
        [ToolParam("true=返回完整 JSON；false=只返回响应正文，默认 true", Required = false)] bool isAll = true,
        [ToolParam("document MIME 时 true=Markdown、false=raw HTML，默认 true", Required = false)] bool isMd = true,
        [ToolParam("Markdown 模式是否保留图片，默认 false", Required = false)] bool withImages = false)
    {
        AppDiagnostics.Mark($"Tool fetch begin method={method} urlLength={url?.Length ?? 0} isMd={isMd} isAll={isAll}");
        if (string.IsNullOrWhiteSpace(url))
            return ToolResultJson.Error("url 不能为空");

        var targetUrl = url.Trim();
        var m = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        var headersJson = string.IsNullOrWhiteSpace(headers) ? null : headers.Trim();
        var cookiesValue = string.IsNullOrWhiteSpace(cookies) ? null : cookies.Trim();

        var result = await HttpAiOutboundClient.SendAsync(
            _httpClientFactory,
            targetUrl,
            m,
            body ?? string.Empty,
            headersJson,
            cookiesValue,
            async target =>
            {
                var param = BrowserJson.Serialize(new NetworkGetCookiesParams { Urls = [target] });
                var cdp = await CallCdpOnTabAsync("Network.getCookies", param, tabId).ConfigureAwait(false);
                return HttpAiOutboundClient.ParseCookieHeaderFromCdpJson(cdp);
            },
            _runContext.CancellationToken).ConfigureAwait(false);

        if (result.IsToolError)
            return result.ErrorJson!;

        try
        {
            _captureSink.Submit(result.ToCaptureRecord());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[fetch] traffic ingest failed: {ex.Message}");
            AppDiagnostics.Exception("Tool fetch traffic ingest failed", ex);
        }

        var raw = result.ToToolJson();
        var formatted = await ApplyDocumentBodyFormatAsync(raw, targetUrl, isAll, isMd, withImages).ConfigureAwait(false);
        AppDiagnostics.Mark($"Tool fetch end length={formatted?.Length ?? 0}");
        return formatted ?? string.Empty;
    }

    private static Task<string> ApplyDocumentBodyFormatAsync(
        string outboundJson,
        string pageUrl,
        bool isAll,
        bool isMd,
        bool withImages)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(outboundJson);
        }
        catch
        {
            return Task.FromResult(outboundJson);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("body", out var bodyEl))
                return Task.FromResult(outboundJson);

            var contentType = TryGetResponseContentType(root);
            var rawBody = bodyEl.GetString() ?? string.Empty;
            var isHtml = DocumentMimeHelper.IsHtmlDocument(contentType);
            var bodyOut = isHtml && isMd
                ? FetchHtmlMarkdownFallback.ToMarkdown(rawBody, withImages)
                : rawBody;

            if (!isAll)
                return Task.FromResult(bodyOut);

            if (!isHtml)
                return Task.FromResult(outboundJson);

            return Task.FromResult(ToolResultJson.Data(o =>
            {
                if (root.TryGetProperty("entryId", out var entryIdEl) && entryIdEl.ValueKind == JsonValueKind.String)
                    o["entryId"] = entryIdEl.GetString() ?? string.Empty;
                if (root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.Number)
                    o["status"] = statusEl.GetInt32();
                if (root.TryGetProperty("statusText", out var statusTextEl) && statusTextEl.ValueKind == JsonValueKind.String)
                    o["statusText"] = statusTextEl.GetString() ?? string.Empty;
                if (root.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                    o["url"] = urlEl.GetString() ?? pageUrl;
                if (root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String)
                    o["method"] = methodEl.GetString() ?? "GET";
                if (root.TryGetProperty("latencyMs", out var latencyEl) && latencyEl.ValueKind == JsonValueKind.Number)
                    o["latencyMs"] = latencyEl.GetInt32();
                if (root.TryGetProperty("transport", out var transportEl) && transportEl.ValueKind == JsonValueKind.String)
                    o["transport"] = transportEl.GetString() ?? "outbound";
                if (root.TryGetProperty("requestHeaders", out var requestHeadersEl))
                    o["requestHeaders"] = JsonNode.Parse(requestHeadersEl.GetRawText());
                if (root.TryGetProperty("headers", out var headersEl))
                    o["headers"] = JsonNode.Parse(headersEl.GetRawText());
                if (root.TryGetProperty("bodyChars", out var bodyCharsEl) && bodyCharsEl.ValueKind == JsonValueKind.Number)
                    o["bodyChars"] = bodyCharsEl.GetInt32();
                if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
                    o["error"] = errorEl.GetString() ?? string.Empty;
                o["contentFormat"] = isMd ? "md" : "raw";
                o["body"] = bodyOut;
            }));
        }
    }

    private static string? TryGetResponseContentType(JsonElement root)
    {
        if (!root.TryGetProperty("headers", out var headersEl))
            return null;

        if (headersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in headersEl.EnumerateObject())
            {
                if (prop.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    return prop.Value.GetString();
            }
            return null;
        }

        if (headersEl.ValueKind != JsonValueKind.String)
            return null;

        try
        {
            using var nested = JsonDocument.Parse(headersEl.GetString() ?? "{}");
            if (nested.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var prop in nested.RootElement.EnumerateObject())
            {
                if (prop.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    return prop.Value.GetString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    [AiTool("browser_cookies",
        """
        读取浏览器 Cookie。提供 url 或 domain 时用 Network.getCookies；均省略时返回全部 Cookie（可用 domain 过滤）。
        """)]
    public async Task<string> CookiesAsync(
        [ToolParam("页面 URL（与 domain 二选一）", Required = false)] string? url = null,
        [ToolParam("域名，如 example.com（与 url 二选一；仅 getAll 时作过滤）", Required = false)] string? domain = null,
        [ToolParam("目标标签 Id（可选）", Required = false)] string? tabId = null)
    {
        if (!string.IsNullOrWhiteSpace(url) || !string.IsNullOrWhiteSpace(domain))
        {
            string[] urls;
            if (!string.IsNullOrWhiteSpace(url))
                urls = [url.Trim()];
            else
                urls = BuildUrlsForDomain(domain!.Trim());

            var param = BrowserJson.Serialize(new NetworkGetCookiesParams { Urls = urls });
            return await CallCdpOnTabAsync("Network.getCookies", param, tabId);
        }

        var raw = await CallCdpOnTabAsync("Network.getAllCookies", "{}", tabId);
        return string.IsNullOrWhiteSpace(domain)
            ? raw
            : FilterCookiesResponseByDomain(raw, domain.Trim());
    }

    [AiTool("page_content",
        """
        读取**当前**浏览器标签页 DOM 快照（CDP DOM 路径，不注入页面 JS）。默认最多等待 3 秒；isMd=true（默认）Markdown，false=raw HTML。
        需 JS 渲染时先 browser_tab action=open，再轮询本工具。SPA/反 CDP 站 API 数据优先 sql→http_traffic_entries；摸清路径用 browser_website_tree。
        """)]
    public async Task<string> PageContentAsync(
        [ToolParam("true=Markdown（默认），false=raw HTML", Required = false)] bool isMd = true,
        [ToolParam("Markdown 模式是否保留图片，默认 false", Required = false)] bool withImages = false,
        [ToolParam("目标标签 Id（可选）", Required = false)] string? tabId = null,
        [ToolParam("等待页面就绪秒数，默认 3，最大 30", Required = false)] int readyTimeoutSeconds = DefaultBrowserReadyTimeoutSeconds)
    {
        AppDiagnostics.Mark($"Tool page_content begin tab={tabId ?? ""} isMd={isMd}");
        var id = _tabManager.ResolveTabId(tabId);
        if (id == null)
            return "没有可用的浏览器标签";

        var readyTimeout = GetReadyTimeout(readyTimeoutSeconds);
        if (!_cdpBridge.HasSession(id)
            && !await _cdpBridge.WaitForSessionAsync(id, readyTimeout).ConfigureAwait(false))
        {
            return ToolResultJson.Error($"浏览器标签 {id} WebView 未就绪，请稍后重试 page_content");
        }

        _ = await WaitForDocumentReadyAsync(id, readyTimeout).ConfigureAwait(false);

        var content = await CdpPageContentExtractor.ExtractAsync(
            (method, parameters) => _cdpBridge.CallMethodOnTabAsync(id, method, parameters),
            isMd,
            withImages).ConfigureAwait(false);
        AppDiagnostics.Mark($"Tool page_content end tab={id} length={content?.Length ?? 0}");
        return content ?? string.Empty;
    }

    [AiTool("browser_website_tree",
        """
        获取**当前浏览器会话**内已捕获请求的网站树 JSON（非 sql 全历史，但比单页 page_content 更广）。
        按站点聚合路径层级；嵌套 site 节点为 CDN/跨域子站；同 path 多次请求在 JSON 中合并为单个叶子名。
        适用：摸清攻击面、列静态资源目录、找隐藏 API 路径。先 browser_tab action=open 浏览目标站再调用。
        site 可填根站点 host 或关联子站（如 cdn.example.com）；省略则取当前活动标签页站点。未找到时返回 availableSites。
        """)]
    public string WebsiteTree(
        [ToolParam("站点 host 或 host:port（可选，匹配根站点或关联子站点）", Required = false)] string? site = null)
    {
        string? rootAuthority = site;
        if (string.IsNullOrWhiteSpace(rootAuthority))
        {
            var url = _tabManager.GetActiveTabUrl();
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                return "{\"error\":\"当前无活动标签或无法解析站点\"}";
            rootAuthority = NormalizeTabSiteAuthority(uri);
        }
        else
        {
            rootAuthority = site!.Trim();
        }

        return _websiteTree.BuildSiteTreeJson(rootAuthority);
    }

    private static string NormalizeTabSiteAuthority(Uri uri)
    {
        var authority = uri.Authority.ToLowerInvariant();
        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && authority.EndsWith(":80", StringComparison.Ordinal))
            return authority[..^3];
        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && authority.EndsWith(":443", StringComparison.Ordinal))
            return authority[..^4];
        return authority;
    }

    private async Task<string> CallCdpOnTabAsync(string method, string parameters, string? tabId)
    {
        var id = _tabManager.ResolveTabId(tabId);
        if (id == null)
            return "没有可用的浏览器标签";

        if (!_cdpBridge.HasSession(id)
            && !await _cdpBridge.WaitForSessionAsync(id, TimeSpan.FromSeconds(20)).ConfigureAwait(false))
            return $"浏览器标签 {id} CDP 未就绪";

        return await _cdpBridge.CallMethodOnTabAsync(id, method, parameters);
    }

    private async Task<bool> WaitForDocumentReadyAsync(string tabId, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return false;

        AppDiagnostics.Mark($"WaitDocumentReady begin tab={tabId} timeoutMs={timeout.TotalMilliseconds:0}");
        var ready = await CdpDocumentLoadWaiter.WaitForDocumentReadyAsync(
            (method, parameters) => _cdpBridge.CallMethodOnTabAsync(tabId, method, parameters),
            timeout).ConfigureAwait(false);

        AppDiagnostics.Mark(ready
            ? $"WaitDocumentReady end tab={tabId} via=DOM"
            : $"WaitDocumentReady timeout tab={tabId} via=DOM");
        return ready;
    }

    private static int GetReadyTimeoutSeconds(int seconds) =>
        Math.Clamp(seconds <= 0 ? DefaultBrowserReadyTimeoutSeconds : seconds, 1, MaxBrowserReadyTimeoutSeconds);

    private static TimeSpan GetReadyTimeout(int seconds) => TimeSpan.FromSeconds(GetReadyTimeoutSeconds(seconds));

    private static TimeSpan GetRemaining(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static string JsonEncode(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    private static string[] BuildUrlsForDomain(string domain)
    {
        domain = domain.Trim().TrimStart('.');
        if (string.IsNullOrEmpty(domain))
            return [];

        return [$"https://{domain}/", $"http://{domain}/"];
    }

    private static string FilterCookiesResponseByDomain(string raw, string domain)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _))
                return raw;

            if (!root.TryGetProperty("cookies", out var cookiesEl) || cookiesEl.ValueKind != JsonValueKind.Array)
                return raw;

            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("cookies");
                writer.WriteStartArray();
                foreach (var cookie in cookiesEl.EnumerateArray())
                {
                    if (!cookie.TryGetProperty("domain", out var domainEl))
                        continue;

                    var cookieDomain = domainEl.GetString();
                    if (string.IsNullOrEmpty(cookieDomain) || !CookieMatchesDomainFilter(cookieDomain, domain))
                        continue;

                    cookie.WriteTo(writer);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return raw;
        }
    }

    private static bool CookieMatchesDomainFilter(string cookieDomain, string filterDomain)
    {
        filterDomain = filterDomain.Trim().TrimStart('.').ToLowerInvariant();
        var cd = cookieDomain.Trim().ToLowerInvariant();
        if (cd.StartsWith('.'))
            cd = cd[1..];

        if (string.IsNullOrEmpty(filterDomain) || string.IsNullOrEmpty(cd))
            return false;

        if (cd == filterDomain)
            return true;

        if (filterDomain.EndsWith("." + cd, StringComparison.Ordinal))
            return true;

        return cd.EndsWith("." + filterDomain, StringComparison.Ordinal);
    }

    private static string NormalizeHttpToolJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ToolResultJson.Error("空响应");

        var trimmed = raw.Trim();
        if (trimmed == "{}")
            return ToolResultJson.Error("空 JSON 对象");

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("ok", out _)
                    || doc.RootElement.TryGetProperty("status", out _)
                    || doc.RootElement.TryGetProperty("error", out _))
                    return trimmed;

                if (doc.RootElement.GetRawText() == "{}")
                    return ToolResultJson.Error("空 JSON 对象");
            }
        }
        catch
        {
            // fall through
        }

        return ToolResultJson.Error(trimmed);
    }
}
