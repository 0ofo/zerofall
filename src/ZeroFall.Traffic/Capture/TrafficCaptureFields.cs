using System;
using System.IO;
using ZeroFall.Traffic.Metadata;

namespace ZeroFall.Traffic.Capture;

/// <summary>URL 解析一次：host / path / extension / has_query，入库直接复用。</summary>
public readonly record struct TrafficUriFacts
{
    public string Host { get; init; }
    public string Path { get; init; }
    public string Extension { get; init; }
    public bool HasQuery { get; init; }

    public static TrafficUriFacts FromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new TrafficUriFacts
            {
                HasQuery = url.Contains('?', StringComparison.Ordinal)
            };
        }

        return new TrafficUriFacts
        {
            Host = uri.Host,
            Path = uri.PathAndQuery,
            Extension = System.IO.Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant(),
            HasQuery = uri.Query.Length > 1
        };
    }
}

/// <summary>捕获边界一次算清的字段，禁止入库前再解析 URL/Content-Type。</summary>
public readonly record struct TrafficCaptureFields
{
    public int? StatusCode { get; init; }
    public TrafficUriFacts Uri { get; init; }
    public string RequestContentType { get; init; }
    public string ResponseContentType { get; init; }
    public TrafficMimeSnapshot Mime { get; init; }
    public string SessionDocumentHost { get; init; }

    public TrafficCaptureFields()
    {
        RequestContentType = string.Empty;
        ResponseContentType = string.Empty;
        SessionDocumentHost = string.Empty;
    }

    public static TrafficCaptureFields Compute(
        string url,
        string? topLevelUrl,
        int? statusCode,
        TrafficHttpHeaders requestHeaders,
        TrafficHttpHeaders responseHeaders)
    {
        var uri = TrafficUriFacts.FromUrl(url);
        var reqCt = requestHeaders.GetContentTypeMediaType();
        var respCt = responseHeaders.GetContentTypeMediaType();
        var mime = TrafficMimeSnapshot.FromStructured(responseHeaders, url, uri.Extension);
        return new TrafficCaptureFields
        {
            StatusCode = statusCode,
            Uri = uri,
            RequestContentType = reqCt,
            ResponseContentType = respCt,
            Mime = mime,
            SessionDocumentHost = TrafficSessionDocumentHost.Resolve(topLevelUrl, url)
        };
    }
}

public static class TrafficSessionDocumentHost
{
    public static string Resolve(string? topLevelUrl, string? requestUrl)
    {
        if (TryDocumentHostFromUrl(topLevelUrl, out var host))
            return host;
        return TryDocumentHostFromUrl(requestUrl, out host) ? host : string.Empty;
    }

    private static bool TryDocumentHostFromUrl(string? url, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(url)
            || TrafficUrlAuthority.IsRedirectStubUrl(url)
            || !TrafficUrlAuthority.TryResolveAuthority(url, out var authority)
            || TrafficHostClassifier.IsResourceHostName(authority))
            return false;

        host = authority;
        return true;
    }
}
