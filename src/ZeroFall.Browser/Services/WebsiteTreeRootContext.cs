using System;
using System.Collections.Generic;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;

namespace ZeroFall.Browser.Services;

/// <summary>
/// 网站树根站点解析：根 = 地址栏文档主站；必要时参考流量 MIME + host 推断会话主站。
/// </summary>
public sealed class WebsiteTreeRootContext
{
    private readonly IBrowserTabManager? _tabManager;
    private readonly Dictionary<(string TabId, int SessionId), string> _sessionRoots = new();
    // 同会话内 text/html 主文档的 host（top_level 不可靠时的参考）
    private readonly Dictionary<(string TabId, int SessionId), string> _sessionHtmlHosts = new();
    private bool _replayMode;

    public WebsiteTreeRootContext(IBrowserTabManager? tabManager = null)
    {
        _tabManager = tabManager;
    }

    public void Clear()
    {
        _sessionRoots.Clear();
        _sessionHtmlHosts.Clear();
        _replayMode = false;
    }

    public void BeginReplay() => _replayMode = true;

    public void EndReplay() => _replayMode = false;

    public void OnDocumentNavigated(string tabId, int pageSessionId, string topLevelUrl)
    {
        if (string.IsNullOrWhiteSpace(tabId) || string.IsNullOrWhiteSpace(topLevelUrl))
            return;
        if (IsRedirectStubUrl(topLevelUrl) || WebsiteTreeHostClassifier.IsResourceHost(topLevelUrl))
            return;

        SetSessionRoot(tabId, pageSessionId, topLevelUrl);
    }

    /// <summary>归档/批处理第一遍：建立会话文档根（地址栏 + HTML 流量 MIME/host）。</summary>
    public void OnReplayEvent(WebTrafficRecordedEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.BrowserTabId))
            return;

        if (!string.IsNullOrWhiteSpace(e.SessionDocumentHost)
            && !WebsiteTreeHostClassifier.IsResourceHostName(e.SessionDocumentHost))
            RememberHtmlDocumentHost(e.BrowserTabId, e.PageSessionId, e.SessionDocumentHost);

        if (WebsiteTreeTrafficHints.TryInferSessionDocumentHost(e, out var htmlAuthority, out var htmlUrl))
        {
            RememberHtmlDocumentHost(e.BrowserTabId, e.PageSessionId, htmlAuthority);
            SetSessionRoot(e.BrowserTabId, e.PageSessionId, htmlUrl);
        }

        if (IsMainFrameDocumentRoot(e))
            SetSessionRoot(e.BrowserTabId, e.PageSessionId, e.Url);
        else if (!IsRedirectStubUrl(e.TopLevelUrl) && WebsiteTreeHostClassifier.IsDocumentRootHost(e.TopLevelUrl))
            SetSessionRoot(e.BrowserTabId, e.PageSessionId, e.TopLevelUrl);
    }

    private void RememberHtmlDocumentHost(string tabId, int pageSessionId, string authority)
    {
        if (string.IsNullOrWhiteSpace(authority) || WebsiteTreeHostClassifier.IsResourceHostName(authority))
            return;

        var key = (tabId, pageSessionId);
        if (_sessionHtmlHosts.TryGetValue(key, out var existing)
            && WebsiteTreeHostClassifier.IsResourceHostName(existing)
            && !WebsiteTreeHostClassifier.IsResourceHostName(authority))
        {
            _sessionHtmlHosts[key] = authority;
            return;
        }

        if (!_sessionHtmlHosts.ContainsKey(key))
            _sessionHtmlHosts[key] = authority;
    }

    private void SetSessionRoot(string tabId, int pageSessionId, string topLevelUrl)
    {
        if (string.IsNullOrWhiteSpace(topLevelUrl)
            || IsRedirectStubUrl(topLevelUrl)
            || WebsiteTreeHostClassifier.IsResourceHost(topLevelUrl))
            return;

        var key = (tabId, pageSessionId);
        if (_sessionRoots.TryGetValue(key, out var existing)
            && !ShouldReplaceSessionRoot(existing, topLevelUrl))
            return;

        _sessionRoots[key] = topLevelUrl;
        if (TryResolveAuthority(topLevelUrl, out var authority))
            RememberHtmlDocumentHost(tabId, pageSessionId, authority);
    }

    private static bool ShouldReplaceSessionRoot(string existing, string candidate)
    {
        if (WebsiteTreeHostClassifier.IsResourceHost(existing)
            && !WebsiteTreeHostClassifier.IsResourceHost(candidate))
            return true;

        if (!WebsiteTreeHostClassifier.IsResourceHost(existing)
            && WebsiteTreeHostClassifier.IsResourceHost(candidate))
            return false;

        return false;
    }

    public string? ResolveRootTopLevelUrl(WebTrafficRecordedEvent e)
    {
        if (TryGetSessionRootUrl(e, out var sessionRoot))
            return sessionRoot;

        if (TryGetSessionHtmlHostAuthority(e, out var htmlAuthority)
            && TryBuildRootUrlFromAuthority(e, htmlAuthority, out var htmlRootUrl))
            return htmlRootUrl;

        if (IsRedirectStubUrl(e.TopLevelUrl)
            && !string.IsNullOrWhiteSpace(e.SessionDocumentHost)
            && !WebsiteTreeHostClassifier.IsResourceHostName(e.SessionDocumentHost)
            && TryBuildRootUrlFromAuthority(e, e.SessionDocumentHost, out var storedRootUrl))
            return storedRootUrl;

        if (!_replayMode
            && _tabManager is not null
            && !string.IsNullOrWhiteSpace(e.BrowserTabId)
            && _tabManager.TryGetTabNavigationState(e.BrowserTabId, out var live, out var liveSession)
            && liveSession == e.PageSessionId
            && !string.IsNullOrWhiteSpace(live)
            && !IsRedirectStubUrl(live)
            && WebsiteTreeHostClassifier.IsDocumentRootHost(live))
            return live;

        if (!IsRedirectStubUrl(e.TopLevelUrl) && WebsiteTreeHostClassifier.IsDocumentRootHost(e.TopLevelUrl))
            return e.TopLevelUrl;

        if (IsMainFrameDocumentRoot(e))
            return e.Url;

        return null;
    }

    public string? TryGetDocumentRootAuthority(WebTrafficRecordedEvent e)
    {
        var topLevel = ResolveRootTopLevelUrl(e);
        if (TryResolveAuthority(topLevel, out var authority) && !WebsiteTreeHostClassifier.IsResourceHostName(authority))
            return authority;

        if (TryGetSessionRootUrl(e, out var sessionUrl)
            && TryResolveAuthority(sessionUrl, out authority)
            && !WebsiteTreeHostClassifier.IsResourceHostName(authority))
            return authority;

        if (TryGetSessionHtmlHostAuthority(e, out authority))
            return authority;

        return null;
    }

    private bool TryGetSessionHtmlHostAuthority(WebTrafficRecordedEvent e, out string authority)
    {
        authority = string.Empty;
        if (string.IsNullOrWhiteSpace(e.BrowserTabId))
            return false;

        if (!_sessionHtmlHosts.TryGetValue((e.BrowserTabId, e.PageSessionId), out var stored) || string.IsNullOrWhiteSpace(stored))
            return false;

        authority = stored;
        return !WebsiteTreeHostClassifier.IsResourceHostName(authority);
    }

    private static bool TryBuildRootUrlFromAuthority(WebTrafficRecordedEvent e, string authority, out string url)
    {
        url = string.Empty;
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var requestUri))
            return false;

        var scheme = requestUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        url = $"{scheme}://{authority}/";
        return true;
    }

    private bool TryGetSessionRootUrl(WebTrafficRecordedEvent e, out string sessionRoot)
    {
        sessionRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(e.BrowserTabId))
            return false;

        if (!_sessionRoots.TryGetValue((e.BrowserTabId, e.PageSessionId), out var root)
            || string.IsNullOrWhiteSpace(root))
            return false;

        sessionRoot = root;
        return true;
    }

    /// <summary>解析网站树根 authority；子资源参考同会话 HTML 文档 host。</summary>
    public static string? ResolveRootAuthority(WebTrafficRecordedEvent e, WebsiteTreeRootContext context)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out _))
            return null;

        if (WebsiteTreeTrafficHints.IsSubresource(e))
            return context.TryGetDocumentRootAuthority(e);

        var authority = context.TryGetDocumentRootAuthority(e);
        if (!string.IsNullOrEmpty(authority))
            return authority;

        if (IsMainFrameDocumentRoot(e) && Uri.TryCreate(e.Url, UriKind.Absolute, out var requestUri))
            return NormalizeAuthority(requestUri);

        return null;
    }

    public static string ResolveRootAuthority(string? topLevelUrl, Uri requestUri)
    {
        if (TryResolveAuthority(topLevelUrl, out var authority) && !WebsiteTreeHostClassifier.IsResourceHostName(authority))
            return authority;
        return NormalizeAuthority(requestUri);
    }

    /// <summary>主框架文档导航（可成为会话根）；CDN/静态资源返回 false。</summary>
    public static bool IsMainFrameDocumentRoot(WebTrafficRecordedEvent e)
    {
        if (WebsiteTreeHostClassifier.IsResourceHost(e.Url))
            return false;

        if (e.ResourceContext == WebTrafficResourceContext.Document)
            return WebsiteTreeTrafficHints.IsHtmlDocument(e);

        if (e.ResourceContext is WebTrafficResourceContext.Script
            or WebTrafficResourceContext.Stylesheet
            or WebTrafficResourceContext.Image
            or WebTrafficResourceContext.Font
            or WebTrafficResourceContext.Media
            or WebTrafficResourceContext.XmlHttpRequest
            or WebTrafficResourceContext.Fetch
            or WebTrafficResourceContext.WebSocket
            or WebTrafficResourceContext.EventSource
            or WebTrafficResourceContext.TextTrack
            or WebTrafficResourceContext.Manifest)
            return false;

        if (!string.Equals(e.Method, "GET", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var requestUri) || string.IsNullOrWhiteSpace(requestUri.Host))
            return false;

        if (!WebsiteTreeTrafficHints.IsHtmlDocument(e))
            return false;

        if (HasStaticAssetExtension(requestUri))
            return false;

        if (IsRedirectStubUrl(e.TopLevelUrl))
            return !SameAuthority(e.TopLevelUrl, requestUri);

        if (!string.IsNullOrWhiteSpace(e.TopLevelUrl)
            && Uri.TryCreate(e.TopLevelUrl, UriKind.Absolute, out var topUri)
            && !string.IsNullOrWhiteSpace(topUri.Host))
        {
            if (string.Equals(topUri.Authority, requestUri.Authority, StringComparison.OrdinalIgnoreCase))
                return IsRootDocumentPath(requestUri);

            return true;
        }

        return IsRootDocumentPath(requestUri);
    }

    /// <summary>搜索/广告等站内外链中转页，不能作为网站树根。</summary>
    public static bool IsRedirectStubUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        if (host.Contains("baidu.com", StringComparison.Ordinal) && path.StartsWith("/link", StringComparison.Ordinal))
            return true;
        if (host.Contains("bing.com", StringComparison.Ordinal) && path.StartsWith("/ck/", StringComparison.Ordinal))
            return true;
        if (host.Contains("sogou.com", StringComparison.Ordinal) && path.StartsWith("/link", StringComparison.Ordinal))
            return true;
        if (host.Contains("so.com", StringComparison.Ordinal) && path.StartsWith("/link", StringComparison.Ordinal))
            return true;
        if (host.Contains("google.", StringComparison.Ordinal) && path.Equals("/url", StringComparison.Ordinal))
            return true;
        if (host.Contains("duckduckgo.com", StringComparison.Ordinal) && path.Equals("/l/", StringComparison.Ordinal))
            return true;

        return false;
    }

    public static bool TryResolveAuthority(string? url, out string authority)
    {
        authority = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || IsRedirectStubUrl(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        authority = NormalizeAuthority(uri);
        return !string.IsNullOrEmpty(authority);
    }

    private static bool SameAuthority(string? url, Uri requestUri)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var topUri))
            return false;
        return string.Equals(topUri.Authority, requestUri.Authority, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStaticAssetExtension(Uri uri)
    {
        var ext = System.IO.Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrEmpty(ext))
            return false;

        return ext.Equals(".js", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".css", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".svg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".woff", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".woff2", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".map", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootDocumentPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path == "/")
            return true;

        var ext = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
            return true;

        return ext.Equals(".html", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".php", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".asp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".aspx", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeAuthority(Uri uri)
    {
        var authority = uri.Authority ?? uri.Host ?? string.Empty;
        authority = authority.ToLowerInvariant();
        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && authority.EndsWith(":80", StringComparison.OrdinalIgnoreCase))
            authority = authority[..^3];
        else if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && authority.EndsWith(":443", StringComparison.OrdinalIgnoreCase))
            authority = authority[..^4];
        return authority;
    }
}
