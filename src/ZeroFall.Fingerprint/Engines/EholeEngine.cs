using System.Text.Json;
using System.Text.RegularExpressions;
using ZeroFall.Fingerprint.Core;
using ZeroFall.Fingerprint.Json;
using ZeroFall.Fingerprint.Text;

namespace ZeroFall.Fingerprint.Engines;

public sealed class EholeEngine : IWebMatchEngine, IFaviconContributor
{
    private readonly List<EholeCompiledRule> _rules;

    private EholeEngine(List<EholeCompiledRule> rules, Dictionary<string, string> faviconMap)
    {
        _rules = rules;
        foreach (var pair in faviconMap)
            FaviconMap[pair.Key] = pair.Value;
    }

    public string Name => "ehole";
    public bool SupportWeb => true;
    public Dictionary<string, string> FaviconMap { get; } = new(StringComparer.Ordinal);

    public static EholeEngine Load(string path)
    {
        var json = File.ReadAllText(path);
        var root = JsonSerializer.Deserialize(json, FingerprintJsonContext.Default.EholeRootDto);
        var rules = new List<EholeCompiledRule>();
        var faviconMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in root?.Fingerprint ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Cms) || item.Keyword is not { Length: > 0 })
                continue;

            var method = item.Method?.ToLowerInvariant() ?? "keyword";
            if (method == "faviconhash")
            {
                foreach (var hash in item.Keyword)
                    faviconMap[hash] = item.Cms;
                continue;
            }

            rules.Add(new EholeCompiledRule(
                item.Cms,
                method,
                item.Location?.ToLowerInvariant() ?? "body",
                item.Keyword));
        }

        return new EholeEngine(rules, faviconMap);
    }

    /// <summary>仅解析 ehole.json 中的 faviconhash 条目，供浏览器只开 favicon 引擎时使用。</summary>
    public static void ContributeFaviconHashesFromFile(string path, FaviconEngine faviconEngine)
    {
        if (!File.Exists(path))
            return;

        var json = File.ReadAllText(path);
        var root = JsonSerializer.Deserialize(json, FingerprintJsonContext.Default.EholeRootDto);
        foreach (var item in root?.Fingerprint ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Cms) || item.Keyword is not { Length: > 0 })
                continue;
            if (!string.Equals(item.Method, "faviconhash", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var hash in item.Keyword)
                faviconEngine.ShodanMmh3Fingers[hash] = item.Cms;
        }
    }

    public FrameworkSet WebMatch(byte[] rawContent, WebMatchContext context)
    {
        var result = new FrameworkSet();
        foreach (var rule in _rules)
        {
            var frame = rule.Match(context.HeaderText, context.BodyText);
            if (frame is not null)
                result.Add(frame);
        }

        return result;
    }

    public void ContributeFaviconHashes(FaviconEngine faviconEngine)
    {
        foreach (var (hash, name) in FaviconMap)
            faviconEngine.ShodanMmh3Fingers[hash] = name;
    }

    private sealed class EholeCompiledRule
    {
        private readonly Regex[]? _regexes;
        private readonly string[] _lowerKeywords;

        public string Cms { get; }
        public string Method { get; }
        public string Location { get; }

        public EholeCompiledRule(string cms, string method, string location, string[] keywords)
        {
            Cms = cms;
            Method = method;
            Location = location;
            _lowerKeywords = keywords.Select(k => k.ToLowerInvariant()).ToArray();
            if (method == "regular")
            {
                _regexes = keywords
                    .Select(k => RegexFactory.TryCreate(k.ToLowerInvariant()))
                    .Where(r => r is not null)
                    .Cast<Regex>()
                    .ToArray();
            }
        }

        public Framework? Match(string header, string body)
        {
            var target = Location switch
            {
                "header" => header,
                "title" => ResponseTextDecoder.ExtractTitle(body),
                _ => body
            };

            if (string.IsNullOrEmpty(target))
                return null;

            var lowerTarget = target.ToLowerInvariant();
            return MatchMethod(lowerTarget) ? new Framework(Cms, FrameworkSource.Ehole) : null;
        }

        private bool MatchMethod(string lowerContent) => Method switch
        {
            "regular" => _regexes is { Length: > 0 } && _regexes.All(r => r.IsMatch(lowerContent)),
            _ => _lowerKeywords.All(k => lowerContent.Contains(k, StringComparison.Ordinal))
        };
    }
}
