using System.Text.Json;
using System.Text.RegularExpressions;
using ZeroFall.Fingerprint.Core;
using ZeroFall.Fingerprint.Json;

namespace ZeroFall.Fingerprint.Engines;

public sealed partial class WappalyzerEngine : IWebMatchEngine
{
    private readonly List<WappalyzerCompiledApp> _apps;

    private WappalyzerEngine(List<WappalyzerCompiledApp> apps) => _apps = apps;

    public string Name => "wappalyzer";
    public bool SupportWeb => true;

    public static WappalyzerEngine Load(string path)
    {
        var json = File.ReadAllText(path);
        var root = JsonSerializer.Deserialize(json, FingerprintJsonContext.Default.WappalyzerRootDto);
        var apps = new List<WappalyzerCompiledApp>();
        foreach (var pair in root?.Apps ?? [])
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;
            apps.Add(WappalyzerCompiledApp.Create(pair.Key, pair.Value));
        }

        return new WappalyzerEngine(apps);
    }

    public FrameworkSet WebMatch(byte[] rawContent, WebMatchContext context)
    {
        var result = new FrameworkSet();
        var normalizedHeaders = NormalizeHeaders(context.HeadersMap);
        var normalizedBody = context.BodyText.ToLowerInvariant();

        foreach (var app in _apps)
        {
            if (!app.IsMatch(normalizedHeaders, normalizedBody))
                continue;
            result.Add(new Framework(app.Name, FrameworkSource.Wappalyzer));
        }

        return result;
    }

    private static Dictionary<string, string> NormalizeHeaders(IReadOnlyDictionary<string, string[]> headers)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in headers)
            normalized[key.ToLowerInvariant()] = string.Join(", ", values).ToLowerInvariant();
        return normalized;
    }

    private sealed class WappalyzerCompiledApp
    {
        private readonly (string Key, Regex Pattern)[] _headers;
        private readonly Regex[] _html;
        private readonly Regex[] _scriptSrc;
        private readonly (string Key, Regex Pattern)[] _cookies;

        public string Name { get; }

        private WappalyzerCompiledApp(
            string name,
            (string Key, Regex Pattern)[] headers,
            Regex[] html,
            Regex[] scriptSrc,
            (string Key, Regex Pattern)[] cookies)
        {
            Name = name;
            _headers = headers;
            _html = html;
            _scriptSrc = scriptSrc;
            _cookies = cookies;
        }

        public static WappalyzerCompiledApp Create(string name, WappalyzerAppDto dto)
        {
            var headers = (dto.Headers ?? [])
                .Select(p => (Key: p.Key.ToLowerInvariant(), Pattern: RegexFactory.TryCreate(p.Value.ToLowerInvariant()) ?? RegexFactory.TryCreate(Regex.Escape(p.Value.ToLowerInvariant()))))
                .Where(p => p.Pattern is not null)
                .Select(p => (p.Key, p.Pattern!))
                .ToArray();
            var html = (dto.Html ?? []).Select(p => RegexFactory.TryCreate(p.ToLowerInvariant())).Where(r => r is not null).Cast<Regex>().ToArray();
            var scriptSrc = (dto.ScriptSrc ?? []).Select(p => RegexFactory.TryCreate(p.ToLowerInvariant())).Where(r => r is not null).Cast<Regex>().ToArray();
            var cookies = (dto.Cookies ?? [])
                .Select(p =>
                {
                    var pattern = string.IsNullOrEmpty(p.Value)
                        ? RegexFactory.TryCreate(Regex.Escape(p.Key.ToLowerInvariant()))
                        : RegexFactory.TryCreate(p.Value.ToLowerInvariant());
                    return (Key: p.Key.ToLowerInvariant(), Pattern: pattern);
                })
                .Where(p => p.Pattern is not null)
                .Select(p => (p.Key, p.Pattern!))
                .ToArray();
            return new WappalyzerCompiledApp(name, headers, html, scriptSrc, cookies);
        }

        public bool IsMatch(Dictionary<string, string> headers, string body)
        {
            var hasAny = _headers.Length > 0 || _html.Length > 0 || _scriptSrc.Length > 0 || _cookies.Length > 0;
            if (!hasAny)
                return false;

            if (_headers.Length > 0 && !_headers.All(h => headers.TryGetValue(h.Key, out var value) && h.Pattern.IsMatch(value)))
                return false;

            if (_cookies.Length > 0)
            {
                var normalized = NormalizeCookies(FindSetCookie(headers));
                if (!_cookies.All(c => normalized.TryGetValue(c.Key, out var value) && c.Pattern.IsMatch(value)))
                    return false;
            }

            if (_html.Length > 0 && !_html.Any(r => r.IsMatch(body)))
                return false;

            if (_scriptSrc.Length > 0)
            {
                var scriptMatched = _scriptSrc.Any(r => r.IsMatch(body));
                if (!scriptMatched)
                {
                    foreach (Match match in WappalyzerEngine.ScriptSrcRegex().Matches(body))
                    {
                        if (_scriptSrc.Any(r => r.IsMatch(match.Groups[1].Value)))
                        {
                            scriptMatched = true;
                            break;
                        }
                    }
                }

                if (!scriptMatched)
                    return false;
            }

            return _headers.Length > 0 || _html.Length > 0 || _scriptSrc.Length > 0 || _cookies.Length > 0;
        }

        private static List<string> FindSetCookie(Dictionary<string, string> headers)
        {
            if (!headers.TryGetValue("set-cookie", out var value))
                return [];

            var values = new List<string>();
            foreach (var part in value.Split(' '))
            {
                if (string.IsNullOrEmpty(part))
                    continue;
                if (part.Contains(','))
                    values.AddRange(part.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                else if (part.Contains(';'))
                    values.AddRange(part.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                else
                    values.Add(part);
            }

            return values;
        }

        private static Dictionary<string, string> NormalizeCookies(IEnumerable<string> cookies)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in cookies)
            {
                var pieces = part.Trim().Split('=', 2);
                if (pieces.Length < 2)
                    continue;
                normalized[pieces[0].Trim().ToLowerInvariant()] = pieces[1].Trim().ToLowerInvariant();
            }

            return normalized;
        }
    }

    [GeneratedRegex("""<script[^>]+src=["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    internal static partial Regex ScriptSrcRegex();
}
