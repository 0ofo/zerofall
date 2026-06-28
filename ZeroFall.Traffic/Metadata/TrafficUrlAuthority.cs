using System;

namespace ZeroFall.Traffic.Metadata;

public static class TrafficUrlAuthority
{
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

    public static string NormalizeAuthority(Uri uri)
    {
        var port = uri.Port;
        if (port is 80 or 443 or -1)
            return uri.Host.ToLowerInvariant();
        return $"{uri.Host.ToLowerInvariant()}:{port}";
    }
}
