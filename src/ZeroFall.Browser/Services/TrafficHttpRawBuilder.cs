using System;
using System.IO;
using System.Text;

namespace ZeroFall.Browser.Services;

internal static class TrafficHttpRawBuilder
{
    public static byte[] BuildResponseRaw(string status, string responseHeaders, string responseBody, byte[]? responseBodyRaw)
    {
        var text = BuildResponseText(status, responseHeaders, responseBody, responseBodyRaw);
        return Encoding.UTF8.GetBytes(text);
    }

    public static string BuildResponseText(
        string status,
        string responseHeaders,
        string responseBody,
        byte[]? responseBodyRaw)
    {
        var sb = new StringBuilder();
        if (!HttpRequestComposer.HeadersStartWithResponseLine(responseHeaders))
            sb.AppendLine(string.IsNullOrWhiteSpace(status) ? "HTTP/1.1 200 OK" : $"HTTP/1.1 {status}");

        if (!string.IsNullOrEmpty(responseHeaders))
            sb.Append(responseHeaders);

        sb.AppendLine();

        var contentType = ParseContentType(responseHeaders);
        if (!string.IsNullOrEmpty(responseBody) && TrafficBodyCodec.IsTextLikeContentType(contentType))
            sb.Append(responseBody);
        else if (responseBodyRaw is { Length: > 0 })
            sb.Append(TrafficBodyCodec.FormatBodyForRawView(responseBodyRaw, contentType));
        else if (!string.IsNullOrEmpty(responseBody))
            sb.Append(responseBody);

        return sb.ToString();
    }

    public static bool ShouldFingerprint(string url, string responseHeaders)
    {
        if (IsFaviconRequest(url))
            return true;

        if (HasFingerprintableHeaders(responseHeaders))
            return true;

        var mime = ParseContentType(responseHeaders);
        if (string.IsNullOrEmpty(mime))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                return ext is ".html" or ".htm" or ".js" or ".mjs" or ".json" or ".xml" or ".css" or "" or "/";
            }

            return false;
        }

        return mime.StartsWith("text/", StringComparison.Ordinal)
               || mime.Contains("json", StringComparison.Ordinal)
               || mime.Contains("xml", StringComparison.Ordinal)
               || mime.Contains("javascript", StringComparison.Ordinal)
               || mime.StartsWith("image/", StringComparison.Ordinal);
    }

    public static bool HasAnalyzableContent(string responseHeaders, string responseBody, byte[]? responseBodyRaw)
    {
        if (ComputeBodySignature(responseBody, responseBodyRaw) > 0)
            return true;

        return HasFingerprintableHeaders(responseHeaders);
    }

    public static int ComputeBodySignature(string? responseBody, byte[]? responseBodyRaw)
    {
        if (responseBodyRaw is { Length: > 0 })
            return responseBodyRaw.Length;

        return string.IsNullOrEmpty(responseBody) ? 0 : responseBody.Length;
    }

    public static bool IsFaviconRequest(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var path = uri.AbsolutePath.ToLowerInvariant();
        return path.EndsWith("favicon.ico", StringComparison.Ordinal)
               || path.Contains("favicon", StringComparison.Ordinal);
    }

    private static bool HasFingerprintableHeaders(string headers)
    {
        if (string.IsNullOrWhiteSpace(headers))
            return false;

        return headers.Contains("Content-Type:", StringComparison.OrdinalIgnoreCase)
               || headers.Contains("Server:", StringComparison.OrdinalIgnoreCase)
               || headers.Contains("Set-Cookie:", StringComparison.OrdinalIgnoreCase)
               || headers.Contains("X-Powered-By:", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ParseContentType(string headers)
    {
        foreach (var line in headers.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = trimmed["Content-Type:".Length..].Trim();
            var semi = value.IndexOf(';');
            return (semi >= 0 ? value[..semi] : value).Trim().ToLowerInvariant();
        }

        return string.Empty;
    }
}
