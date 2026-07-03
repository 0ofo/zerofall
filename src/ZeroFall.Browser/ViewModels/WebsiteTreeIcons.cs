using System;
using System.IO;
using System.Linq;
using Avalonia.Media;
using ZeroFall.Platform.Services;

namespace ZeroFall.Browser.ViewModels;

/// <summary>
/// 网站树节点图标：目录、HTTP 状态码、MIME。
/// </summary>
internal static class WebsiteTreeIcons
{
    public static StreamGeometry? Folder => Get("SemiIconFolder");

    public static StreamGeometry? TechnologyFolder => FirstResolved("SemiIconCode", "SemiIconServer");

    public static StreamGeometry? Technology => FirstResolved("SemiIconServer", "SemiIconCode");

    public static StreamGeometry? TargetScope => FirstResolved("SemiIconGlobeStroked", "SemiIconGlobeStrokeStroked", "SemiIconGlobeStroke", "SemiIconGlobe");

    public static StreamGeometry? ScopeHost => FirstResolved("SemiIconGlobeStroked", "SemiIconGlobeStrokeStroked", "SemiIconGlobe", "SemiIconGlobeStroke");

    public static StreamGeometry? ResolveRequestIcon(string status, string responseHeaders, Uri requestUri)
    {
        if (TryParseHttpStatus(status, out var code))
        {
            if (code is >= 300 and < 400)
                return FirstResolved("SemiIconForward", "SemiIconExternalOpen", "SemiIconArrowRight");
            if (code is >= 400 and < 500)
                return FirstResolved("SemiIconLock", "SemiIconKey");
            if (code is >= 500 and < 600)
                return FirstResolved("SemiIconAlertTriangle", "SemiIconAlertCircle");
            if (code is >= 200 and < 300)
                return ResolveMimeIcon(responseHeaders, requestUri);
        }

        return ResolveMimeIcon(responseHeaders, requestUri);
    }

    public static StreamGeometry? ResolveMimeIcon(string responseHeaders, Uri requestUri)
    {
        var mime = ParseFirstContentType(responseHeaders);
        if (string.IsNullOrWhiteSpace(mime))
            mime = GuessMimeFromRequestUrl(requestUri);
        return FirstResolvedIcon(MediaTypeToSemiKeys(mime));
    }

    private static bool TryParseHttpStatus(string status, out int code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var digits = new string(status.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out code) && code is >= 100 and <= 599;
    }

    private static StreamGeometry? FirstResolved(params string[] keys)
    {
        foreach (var key in keys)
        {
            var g = Get(key);
            if (g != null)
                return g;
        }

        return Get("SemiIconFile");
    }

    private static StreamGeometry? Get(string key) => IconHelper.GetIcon(key);

    private static StreamGeometry? FirstResolvedIcon(string[] keys)
    {
        foreach (var key in keys)
        {
            var g = Get(key);
            if (g != null)
                return g;
        }

        return Get("SemiIconFile");
    }

    private static string[] MediaTypeToSemiKeys(string mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
            return ["SemiIconFile"];

        mime = mime.Trim().ToLowerInvariant();
        var boundary = mime.IndexOf(';');
        if (boundary >= 0)
            mime = mime[..boundary].Trim();

        var slash = mime.IndexOf('/');
        var main = slash > 0 ? mime[..slash] : mime;

        if (main == "multipart")
            return ["SemiIconGridView"];

        if (mime.StartsWith("text/html", StringComparison.Ordinal)
            || mime == "application/xhtml+xml")
            return ["SemiIconGlobeStroked", "SemiIconGlobeStrokeStroked", "SemiIconGlobe"];

        if (mime.StartsWith("text/css", StringComparison.Ordinal))
            return ["SemiIconFile"];

        if (mime.StartsWith("text/plain", StringComparison.Ordinal))
            return ["SemiIconFile"];

        if (mime is "application/json" || mime.StartsWith("application/problem+json", StringComparison.Ordinal))
            return ["SemiIconCode"];

        if (mime.StartsWith("application/javascript", StringComparison.Ordinal)
            || mime.StartsWith("text/javascript", StringComparison.Ordinal))
            return ["SemiIconCode"];

        if (mime is "application/wasm")
            return ["SemiIconCode"];

        if (mime.Contains("xml", StringComparison.Ordinal))
            return ["SemiIconCode"];

        if (mime.StartsWith("font/", StringComparison.Ordinal)
            || mime.StartsWith("application/font", StringComparison.Ordinal)
            || mime.Contains("woff", StringComparison.Ordinal))
            return ["SemiIconFile", "SemiIconServer"];

        if (main == "image")
            return ["SemiIconImage", "SemiIconSticker", "SemiIconFile"];

        if (main == "audio")
            return ["SemiIconMusic", "SemiIconFile"];

        if (main == "video")
            return ["SemiIconVideoCamera", "SemiIconFilm", "SemiIconFile"];

        if (mime is "application/pdf")
            return ["SemiIconFile"];

        if (mime is "application/zip"
            || mime.StartsWith("application/vnd.", StringComparison.Ordinal)
            || mime.StartsWith("application/x-", StringComparison.Ordinal))
            return ["SemiIconFile", "SemiIconFolder"];

        if (mime is "application/octet-stream")
            return ["SemiIconServer", "SemiIconFile"];

        return ["SemiIconFile"];
    }

    private static string ParseFirstContentType(string headers)
    {
        foreach (var line in headers.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = trimmed["Content-Type:".Length..].Trim();
            var semi = value.IndexOf(';');
            var core = semi >= 0 ? value[..semi].Trim() : value;
            return string.IsNullOrEmpty(core) ? string.Empty : core.ToLowerInvariant();
        }

        return string.Empty;
    }

    private static string GuessMimeFromRequestUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path.Length <= 2)
            return string.Empty;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" or ".mjs" or ".cjs" => "application/javascript",
            ".json" => "application/json",
            ".xml" or ".svg" => "application/xml",
            ".wasm" => "application/wasm",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".ico" or ".bmp" => "image/unknown",
            ".mp4" or ".webm" or ".ogg" or ".ogv" or ".mov" => "video/unknown",
            ".mp3" or ".wav" or ".flac" or ".aac" => "audio/unknown",
            ".woff" or ".woff2" or ".ttf" or ".otf" => "font/woff2",
            ".pdf" => "application/pdf",
            ".zip" or ".gz" => "application/zip",
            _ => string.Empty
        };
    }
}
