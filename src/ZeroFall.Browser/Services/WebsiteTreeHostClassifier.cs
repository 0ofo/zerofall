using System;

namespace ZeroFall.Browser.Services;

/// <summary>区分地址栏主站与 CDN/静态资源域名，避免资源站成为网站树根。</summary>
internal static class WebsiteTreeHostClassifier
{
    public static bool IsResourceHost(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost))
            return false;

        if (Uri.TryCreate(urlOrHost, UriKind.Absolute, out var uri))
            return IsResourceHostName(uri.Host);

        return IsResourceHostName(urlOrHost);
    }

    public static bool IsResourceHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        host = host.Trim().ToLowerInvariant();

        if (host is "csdnimg.cn" or "bdstatic.com" or "bcebos.com")
            return true;

        if (host.EndsWith(".csdnimg.cn", StringComparison.Ordinal))
            return true;

        if (host.EndsWith(".bdstatic.com", StringComparison.Ordinal)
            || host.EndsWith(".bcebos.com", StringComparison.Ordinal))
            return true;

        if (host.StartsWith("g.", StringComparison.Ordinal)
            || host.StartsWith("img-", StringComparison.Ordinal)
            || host.StartsWith("i-", StringComparison.Ordinal)
            || host.StartsWith("profile-", StringComparison.Ordinal)
            || host.StartsWith("kunpeng-", StringComparison.Ordinal)
            || host.StartsWith("kunpeng.", StringComparison.Ordinal))
            return true;

        if (host.StartsWith("cdn.", StringComparison.Ordinal)
            || host.StartsWith("cdn-", StringComparison.Ordinal)
            || host.Contains(".cdn.", StringComparison.Ordinal)
            || host.StartsWith("static.", StringComparison.Ordinal)
            || host.StartsWith("img.", StringComparison.Ordinal)
            || host.StartsWith("assets.", StringComparison.Ordinal)
            || host.StartsWith("media.", StringComparison.Ordinal)
            || host.StartsWith("pss.", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>是否适合作为会话文档根（地址栏等价 URL 的 host）。</summary>
    public static bool IsDocumentRootHost(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost))
            return false;
        if (IsResourceHost(urlOrHost))
            return false;

        if (Uri.TryCreate(urlOrHost, UriKind.Absolute, out var uri))
            return !string.IsNullOrWhiteSpace(uri.Host);

        return !string.IsNullOrWhiteSpace(urlOrHost) && !urlOrHost.Contains('/');
    }
}
