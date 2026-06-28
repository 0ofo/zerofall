using System;
using System.IO;
using ZeroFall.Traffic;

namespace ZeroFall.Traffic.Metadata;

/// <summary>
/// ???? MIME ?????? <see cref="TrafficHttpHeaders"/> ???????? wire ???????
/// </summary>
public readonly record struct TrafficMimeSnapshot
{
    public TrafficMimeCategory FilterCategory { get; init; }
    public string PrimaryClass { get; init; }
    public string MediaType { get; init; }

    public TrafficMimeSnapshot()
    {
        PrimaryClass = string.Empty;
        MediaType = string.Empty;
        FilterCategory = TrafficMimeCategory.OtherBinary;
    }

    public bool IsHtml => FilterCategory == TrafficMimeCategory.Html;

    public static TrafficMimeSnapshot FromStructured(
        TrafficHttpHeaders responseHeaders,
        string url,
        string? fileExtension = null)
    {
        var mediaType = responseHeaders.GetContentTypeMediaType();
        if (string.IsNullOrEmpty(mediaType))
            mediaType = GuessMediaTypeFromExtension(fileExtension ?? TryGetExtension(url));

        var primary = ExtractPrimaryClass(mediaType);
        var filterCategory = TrafficMimeClassifier.Classify(mediaType, url, fileExtension);
        return new TrafficMimeSnapshot
        {
            FilterCategory = filterCategory,
            PrimaryClass = primary,
            MediaType = mediaType
        };
    }

    public static string NormalizeMediaType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var value = raw.Trim().ToLowerInvariant();
        var semi = value.IndexOf(';');
        if (semi >= 0)
            value = value[..semi].Trim();
        return value;
    }

    public static string ExtractPrimaryClass(string mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return string.Empty;

        var slash = mediaType.IndexOf('/');
        return slash < 0 ? mediaType : mediaType[..slash];
    }

    private static string GuessMediaTypeFromExtension(string extensionOrUrl)
    {
        var ext = extensionOrUrl.Contains('/', StringComparison.Ordinal)
            ? TryGetExtension(extensionOrUrl)
            : extensionOrUrl.TrimStart('.').ToLowerInvariant();

        return ext switch
        {
            "html" or "htm" => "text/html",
            "js" or "mjs" or "cjs" => "application/javascript",
            "css" => "text/css",
            "json" => "application/json",
            "xml" or "svg" or "rss" or "atom" => "application/xml",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "ico" => "image/x-icon",
            "woff" => "font/woff",
            "woff2" => "font/woff2",
            "swf" or "flv" => "application/x-shockwave-flash",
            _ => string.Empty
        };
    }

    private static string TryGetExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.Empty;

        return Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
    }
}
