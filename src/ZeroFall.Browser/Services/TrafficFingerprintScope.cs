using System;
using System.IO;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;

namespace ZeroFall.Browser.Services;

/// <summary>被动指纹识别范围：地址栏文档 + favicon，排除 XHR/Fetch/静态子资源。</summary>
internal static class TrafficFingerprintScope
{
    public static bool ShouldFingerprint(WebTrafficRecordedEvent traffic)
    {
        if (TrafficHttpRawBuilder.IsFaviconRequest(traffic.Url))
            return true;

        if (traffic.MimeFilterCategory >= 0)
            return traffic.FingerprintEligible;

        if (traffic.ResourceContext == WebTrafficResourceContext.Document)
            return TrafficHttpRawBuilder.ShouldFingerprint(traffic.Url, traffic.ResponseHeaders);

        if (traffic.ResourceContext is WebTrafficResourceContext.XmlHttpRequest
            or WebTrafficResourceContext.Fetch
            or WebTrafficResourceContext.WebSocket
            or WebTrafficResourceContext.EventSource)
            return false;

        if (traffic.ResourceContext != WebTrafficResourceContext.Unknown)
            return false;

        // ResourceContext 未配对（响应早于 WebResourceRequested）或代理流量：宽松回退
        return IsDocumentLikeGetRequest(traffic);
    }

    /// <summary>Unknown 时：GET + 可指纹 MIME；TopLevelUrl 未就绪时同站 HTML 主文档仍识别。</summary>
    private static bool IsDocumentLikeGetRequest(WebTrafficRecordedEvent traffic)
    {
        if (!string.Equals(traffic.Method, "GET", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TrafficHttpRawBuilder.ShouldFingerprint(traffic.Url, traffic.ResponseHeaders))
            return false;

        if (!IsHtmlOrDocumentResponse(traffic))
            return false;

        if (IsTopLevelDocumentRequest(traffic))
            return true;

        if (!Uri.TryCreate(traffic.Url, UriKind.Absolute, out var request))
            return false;

        if (string.IsNullOrWhiteSpace(traffic.TopLevelUrl))
            return IsRootDocumentPath(request);

        if (!Uri.TryCreate(traffic.TopLevelUrl, UriKind.Absolute, out var top))
            return IsRootDocumentPath(request);

        return string.Equals(request.Scheme, top.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(request.Authority, top.Authority, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHtmlOrDocumentResponse(WebTrafficRecordedEvent traffic)
    {
        var mime = !string.IsNullOrWhiteSpace(traffic.MimeType)
            ? traffic.MimeType
            : TrafficHttpRawBuilder.ParseContentType(traffic.ResponseHeaders);
        if (!string.IsNullOrEmpty(mime))
            return mime.Contains("html", StringComparison.Ordinal);

        if (!Uri.TryCreate(traffic.Url, UriKind.Absolute, out var uri))
            return false;

        return IsRootDocumentPath(uri);
    }

    private static bool IsRootDocumentPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path == "/")
            return true;

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
            return true;

        return ext.Equals(".html", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".php", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".asp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".aspx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTopLevelDocumentRequest(WebTrafficRecordedEvent traffic)
    {
        if (!string.Equals(traffic.Method, "GET", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(traffic.Url, UriKind.Absolute, out var request))
            return false;

        if (!Uri.TryCreate(traffic.TopLevelUrl, UriKind.Absolute, out var top))
            return false;

        if (!string.Equals(request.Scheme, top.Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(request.Authority, top.Authority, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(
            NormalizeDocumentPath(request),
            NormalizeDocumentPath(top),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDocumentPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path))
            return "/";
        return path.EndsWith('/') ? path : path;
    }
}
