using System;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Traffic.Metadata;

namespace ZeroFall.Browser.Services;

/// <summary>从流量记录的 MIME、ResourceContext 与 host 推断主文档 / 子资源。</summary>
internal static class WebsiteTreeTrafficHints
{
    public static string? ParseMime(WebTrafficRecordedEvent e) =>
        !string.IsNullOrWhiteSpace(e.MimeType)
            ? e.MimeType
            : TrafficMimeSnapshot.NormalizeMediaType(TrafficHttpRawBuilder.ParseContentType(e.ResponseHeaders));

    public static bool IsHtmlDocument(WebTrafficRecordedEvent e)
    {
        if (e.ResourceContext == WebTrafficResourceContext.Document)
            return true;

        if (e.MimeFilterCategory == (int)TrafficMimeCategory.Html)
            return true;

        var mime = ParseMime(e);
        if (!string.IsNullOrEmpty(mime))
            return mime.Contains("html", StringComparison.Ordinal);

        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri))
            return false;

        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path == "/")
            return string.Equals(e.Method, "GET", StringComparison.OrdinalIgnoreCase);

        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".html", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".php", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".asp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".aspx", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSubresource(WebTrafficRecordedEvent e)
    {
        if (e.ResourceContext is WebTrafficResourceContext.Script
            or WebTrafficResourceContext.Stylesheet
            or WebTrafficResourceContext.Image
            or WebTrafficResourceContext.Font
            or WebTrafficResourceContext.Media
            or WebTrafficResourceContext.TextTrack
            or WebTrafficResourceContext.Manifest)
            return true;

        if (e.ResourceContext == WebTrafficResourceContext.Document)
            return false;

        var mime = ParseMime(e);
        if (!string.IsNullOrEmpty(mime))
        {
            if (mime.Contains("html", StringComparison.Ordinal))
                return false;

            if (mime.StartsWith("image/", StringComparison.Ordinal)
                || mime.StartsWith("font/", StringComparison.Ordinal)
                || mime.StartsWith("video/", StringComparison.Ordinal)
                || mime.StartsWith("audio/", StringComparison.Ordinal))
                return true;

            if (mime.Contains("javascript", StringComparison.Ordinal)
                || mime.Contains("ecmascript", StringComparison.Ordinal)
                || mime.StartsWith("text/css", StringComparison.Ordinal)
                || mime.StartsWith("text/javascript", StringComparison.Ordinal))
                return true;

            if (mime.Contains("json", StringComparison.Ordinal)
                || mime.Contains("xml", StringComparison.Ordinal))
                return !mime.Contains("html", StringComparison.Ordinal);
        }

        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri))
            return false;

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

    /// <summary>从 HTML 主文档流量推断会话主站 authority（MIME + host）。</summary>
    public static bool TryInferSessionDocumentHost(WebTrafficRecordedEvent e, out string authority, out string documentUrl)
    {
        authority = string.Empty;
        documentUrl = string.Empty;

        if (!string.Equals(e.Method, "GET", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsHtmlDocument(e))
            return false;

        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        if (WebsiteTreeHostClassifier.IsResourceHostName(uri.Host))
            return false;

        if (WebsiteTreeRootContext.IsRedirectStubUrl(e.Url))
            return false;

        authority = WebsiteTreeRootContext.NormalizeAuthority(uri);
        documentUrl = e.Url;
        return !string.IsNullOrEmpty(authority);
    }

    public static bool TryGetRequestAuthority(WebTrafficRecordedEvent e, out string authority)
    {
        authority = string.Empty;
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        authority = WebsiteTreeRootContext.NormalizeAuthority(uri);
        return !string.IsNullOrEmpty(authority);
    }
}
