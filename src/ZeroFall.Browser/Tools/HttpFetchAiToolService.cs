using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ZeroFall.Base.AiTools;
using ZeroFall.Base.Diagnostics;
using ZeroFall.Browser.Services;
using ZeroFall.Platform.Services;
using ZeroFall.Traffic.Ingest;

namespace ZeroFall.Browser.Tools;

/// <summary>AI 出站 HTTP 工具（不依赖 CDP）。</summary>
public sealed class HttpFetchAiToolService
{
    private readonly IOutboundHttpClientFactory _httpClientFactory;
    private readonly ITrafficCaptureSink _captureSink;
    private readonly IAiChatRunContext _runContext;

    public HttpFetchAiToolService(
        IOutboundHttpClientFactory httpClientFactory,
        ITrafficCaptureSink captureSink,
        IAiChatRunContext runContext)
    {
        _httpClientFactory = httpClientFactory;
        _captureSink = captureSink;
        _runContext = runContext;
    }

    [AiTool("fetch",
        """
        出站 HTTP 单条请求（默认 GET）。默认 isAll=true 返回扁平 JSON：status、headers、body、latencyMs、entryId 等；失败时含 ok:false。
        isAll=false 时只返回响应正文；HTML 响应按 isMd 转 Markdown 或返回 raw HTML，适合搜索/阅读场景。
        示例 fetch(url="https://www.baidu.com")：HTML 响应且 isMd=true（默认）时 body 为 Markdown。
        支持 method、headers（JSON 对象）、cookies（Cookie 头简写）、body、isAll、isMd、withImages。
        POST/PUT/PATCH 且 body 为 key=value（& 连接）时，未指定 Content-Type 会自动使用 application/x-www-form-urlencoded，并写入 Content-Length（避免 chunked 导致部分 PHP/nginx 收不到 $_POST）；返回含 requestHeaders（实际发出的请求头）。
        不执行页面 JS；强 JS/SPA 页请 browser_tab action=open 触发浏览，再用 sql→http_traffic_entries 读 API 响应。
        多条有策略的请求（爆破、遍历、条件分支）请在工作区写脚本后用终端执行，不要堆几十次 fetch。
        连接地址仅来自 url；显式 Host 头原样发送（虚拟主机/反代），不得用 Host 决定 TCP 连接目标。
        """)]
    public async Task<string> FetchAsync(
        [ToolParam("完整 URL")] string url,
        [ToolParam("HTTP 方法，默认 GET", Required = false)] string method = "GET",
        [ToolParam("请求头 JSON 对象", Required = false)] string? headers = null,
        [ToolParam("Cookie 头（分号分隔 name=value），优先于 headers.Cookie", Required = false)] string? cookies = null,
        [ToolParam("请求体，默认空", Required = false)] string? body = "",
        [ToolParam("合并 Cookie 时使用的浏览器标签 Id（可选，当前版本忽略）", Required = false)] string? tabId = null,
        [ToolParam("true=返回完整 JSON；false=只返回响应正文，默认 true", Required = false)] bool isAll = true,
        [ToolParam("document MIME 时 true=Markdown、false=raw HTML，默认 true", Required = false)] bool isMd = true,
        [ToolParam("Markdown 模式是否保留图片，默认 false", Required = false)] bool withImages = false)
    {
        _ = tabId;
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
            _ => Task.FromResult<string?>(null),
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
}
