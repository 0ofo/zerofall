using System.Text.RegularExpressions;

namespace ZeroFall.Fingerprint.Http;

internal static partial class FaviconUrlExtractor
{
    private static readonly string[] Patterns =
    [
        """<link[^>]*rel=["'](?:shortcut )?icon["'][^>]*href=["']([^"']+)["']""",
        """<link[^>]*href=["']([^"']+)["'][^>]*rel=["'](?:shortcut )?icon["']""",
        """href=["']([^"']*favicon[^"']*)["']"""
    ];

    public static string Extract(string body, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return string.Empty;

        foreach (var pattern in Patterns)
        {
            var match = Regex.Match(body, pattern, RegexOptions.IgnoreCase);
            if (!match.Success || match.Groups.Count < 2)
                continue;

            var path = match.Groups[1].Value.Trim();
            if (!Uri.TryCreate(path, UriKind.RelativeOrAbsolute, out var refUri))
                continue;
            return new Uri(baseUri, refUri).ToString();
        }

        return new Uri(baseUri, "/favicon.ico").ToString();
    }
}

internal static partial class ContentRedirectParser
{
    [GeneratedRegex(@"location\.(?:href|replace)\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex JsLocationRegex();

    [GeneratedRegex(@"<meta[^>]+http-equiv=[""']refresh[""'][^>]+content=[""'][^;""]*;\s*url=([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefreshRegex();

    public static IReadOnlyList<string> Parse(string body, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return [];

        var urls = new List<string>();
        foreach (Match match in JsLocationRegex().Matches(body))
        {
            if (TryResolve(match.Groups[1].Value, baseUri, out var resolved))
                urls.Add(resolved);
        }

        foreach (Match match in MetaRefreshRegex().Matches(body))
        {
            if (TryResolve(match.Groups[1].Value, baseUri, out var resolved))
                urls.Add(resolved);
        }

        return urls;
    }

    private static bool TryResolve(string value, Uri baseUri, out string resolved)
    {
        resolved = string.Empty;
        value = value.Trim();
        if (string.IsNullOrEmpty(value))
            return false;
        if (!Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var refUri))
            return false;
        resolved = new Uri(baseUri, refUri).ToString();
        return true;
    }
}
