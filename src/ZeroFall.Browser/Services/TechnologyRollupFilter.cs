using System;
using System.Collections.Generic;
using ZeroFall.Fingerprint.Core;

namespace ZeroFall.Browser.Services;

/// <summary>
/// 跨 host 汇总到主站时：前端资源库（JS/CSS 等）可汇总；CDN/服务器/边缘设施仅保留在资源站。
/// </summary>
internal static class TechnologyRollupFilter
{
    private static readonly HashSet<string> InfrastructureExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "nginx", "openresty", "tengine", "apache", "httpd", "iis", "microsoft-iis",
        "litespeed", "caddy", "tomcat", "jetty", "gunicorn", "uwsgi", "php",
        "cloudflare", "akamai", "fastly", "varnish", "squid", "haproxy", "envoy",
        "kong", "traefik", "lighttpd", "cowboy", "kestrel", "weblogic", "websphere",
        "cdn", "cdnjs", "jsdelivr", "unpkg", "amazon cloudfront", "cloudfront",
        "alibaba cloud cdn", "tencent cdn", "baidu cdn", "qiniu", "upyun",
        "openssl", "brotli", "gzip", "waf", "imperva", "incapsula", "sucuri",
        "ddos-guard", "awselb", "amazon alb", "google cloud cdn", "azure cdn"
    };

    public static FrameworkSet SelectForRootRollup(string url, string responseHeaders, FrameworkSet matches)
    {
        if (matches.Count == 0)
            return matches;

        var assetKind = ClassifyAsset(url, responseHeaders);
        var rollup = new FrameworkSet();
        foreach (var framework in matches.Values)
        {
            if (ShouldRollupToRoot(framework, assetKind))
                rollup.Add(framework);
        }

        return rollup;
    }

    private static bool ShouldRollupToRoot(Framework framework, TrafficAssetKind assetKind)
    {
        // Goby 规则偏设备/中间件，跨 host 汇总易把 CDN/边缘 Server 误归到主站。
        if (framework.Source == FrameworkSource.Goby)
            return false;

        if (IsInfrastructure(framework.Name))
            return false;

        if (framework.Source == FrameworkSource.Ico)
            return false;

        return assetKind switch
        {
            TrafficAssetKind.JavaScript or TrafficAssetKind.Css => true,
            TrafficAssetKind.Html => !IsServerOrCdnFingerprint(framework),
            TrafficAssetKind.Font or TrafficAssetKind.Image => false,
            _ => IsApplicationLike(framework)
        };
    }

    private static bool IsServerOrCdnFingerprint(Framework framework)
    {
        if (IsInfrastructure(framework.Name))
            return true;

        return IsServerLikeName(framework.Name);
    }

    private static bool IsApplicationLike(Framework framework) =>
        !IsInfrastructure(framework.Name);

    private static bool IsInfrastructure(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = name.Trim().ToLowerInvariant();
        if (InfrastructureExact.Contains(normalized))
            return true;

        if (normalized.Contains("cloudflare", StringComparison.Ordinal)
            || normalized.Contains("akamai", StringComparison.Ordinal)
            || normalized.Contains("fastly", StringComparison.Ordinal))
            return true;

        if (normalized.EndsWith(" cdn", StringComparison.Ordinal)
            || normalized.EndsWith("-cdn", StringComparison.Ordinal)
            || normalized.StartsWith("cdn ", StringComparison.Ordinal))
            return true;

        return IsServerLikeName(normalized);
    }

    private static bool IsServerLikeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = name.Trim().ToLowerInvariant();
        if (normalized is "server" or "web server" or "web-server")
            return true;

        return normalized.Contains("nginx", StringComparison.Ordinal)
               || normalized.Contains("apache", StringComparison.Ordinal)
               || normalized.Contains("cloudflare", StringComparison.Ordinal)
               || normalized.Contains("-server", StringComparison.Ordinal)
               || normalized.Contains("_server", StringComparison.Ordinal)
               || normalized.Contains("web-server", StringComparison.Ordinal)
               || normalized.Contains("webserver", StringComparison.Ordinal)
               || normalized.Contains("http-server", StringComparison.Ordinal);
    }

    public static TrafficAssetKind ClassifyAsset(string url, string responseHeaders)
    {
        if (TrafficHttpRawBuilder.IsFaviconRequest(url))
            return TrafficAssetKind.Image;

        var mime = TrafficHttpRawBuilder.ParseContentType(responseHeaders);
        if (!string.IsNullOrEmpty(mime))
        {
            if (mime.Contains("javascript", StringComparison.Ordinal)
                || mime.Contains("ecmascript", StringComparison.Ordinal))
                return TrafficAssetKind.JavaScript;

            if (mime.StartsWith("text/css", StringComparison.Ordinal))
                return TrafficAssetKind.Css;

            if (mime is "text/html" or "application/xhtml+xml")
                return TrafficAssetKind.Html;

            if (mime.StartsWith("font/", StringComparison.Ordinal)
                || mime.Contains("woff", StringComparison.Ordinal))
                return TrafficAssetKind.Font;

            if (mime.StartsWith("image/", StringComparison.Ordinal))
                return TrafficAssetKind.Image;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return TrafficAssetKind.Other;

        var ext = System.IO.Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return ext switch
        {
            ".js" or ".mjs" or ".cjs" => TrafficAssetKind.JavaScript,
            ".css" => TrafficAssetKind.Css,
            ".html" or ".htm" => TrafficAssetKind.Html,
            ".woff" or ".woff2" or ".ttf" or ".otf" or ".eot" => TrafficAssetKind.Font,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".ico" or ".svg" => TrafficAssetKind.Image,
            _ => TrafficAssetKind.Other
        };
    }
}

public enum TrafficAssetKind
{
    Other,
    Html,
    JavaScript,
    Css,
    Font,
    Image
}
