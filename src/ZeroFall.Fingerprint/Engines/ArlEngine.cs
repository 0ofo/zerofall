using System.Text.RegularExpressions;
using ZeroFall.Fingerprint.Core;
using ZeroFall.Fingerprint.Hashing;

namespace ZeroFall.Fingerprint.Engines;

public sealed class ArlEngine : IWebMatchEngine
{
    private readonly List<ArlCompiledRule> _rules;

    private ArlEngine(List<ArlCompiledRule> rules) => _rules = rules;

    public string Name => "arl";
    public bool SupportWeb => true;

    public static ArlEngine Load(string path)
    {
        var rules = new List<ArlCompiledRule>();
        string? currentName = null;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("- name:", StringComparison.Ordinal))
            {
                currentName = line["- name:".Length..].Trim();
                continue;
            }

            if (!line.StartsWith("rule:", StringComparison.Ordinal) || string.IsNullOrEmpty(currentName))
                continue;

            var ruleText = line["rule:".Length..].Trim();
            if (ruleText.Length >= 2
                && ((ruleText[0] == '\'' && ruleText[^1] == '\'') || (ruleText[0] == '"' && ruleText[^1] == '"')))
                ruleText = ruleText[1..^1];

            rules.Add(new ArlCompiledRule(currentName, ruleText));
        }

        return new ArlEngine(rules);
    }

    public FrameworkSet WebMatch(byte[] rawContent, WebMatchContext context)
    {
        var result = new FrameworkSet();
        foreach (var rule in _rules)
        {
            if (!rule.IsMatch(context.BodyText, context.HeaderText, context.Title, context))
                continue;
            var name = ExtractCleanName(rule.Name);
            result.Add(new Framework(name, FrameworkSource.Arl));
        }

        return result;
    }

    private static string ExtractCleanName(string name)
    {
        foreach (var suffix in new[] { "_body", "_header", "_title", "_icon_hash" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];
        }

        return name;
    }

    private sealed class ArlCompiledRule
    {
        private static readonly Regex BodyRe = new(@"body=""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        private static readonly Regex HeaderRe = new(@"header=""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        private static readonly Regex TitleRe = new(@"title=""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        private static readonly Regex IconHashRe = new(@"icon_hash=""([^""]+)""", RegexOptions.Compiled);

        private readonly List<ArlCondition> _conditions;

        public string Name { get; }

        public ArlCompiledRule(string name, string rule)
        {
            Name = name;
            _conditions = ParseConditions(rule);
        }

        public bool IsMatch(string body, string header, string title, WebMatchContext context)
        {
            if (_conditions.Count == 0)
                return false;

            foreach (var cond in _conditions)
            {
                if (!cond.IsMatch(body, header, title, context))
                    return false;
            }

            return true;
        }

        private static List<ArlCondition> ParseConditions(string rule)
        {
            var list = new List<ArlCondition>();
            foreach (Match m in BodyRe.Matches(rule))
            {
                if (m.Groups[1].Value.Length > 0)
                    list.Add(new ArlCondition("body", Unescape(m.Groups[1].Value)));
            }

            foreach (Match m in HeaderRe.Matches(rule))
            {
                if (m.Groups[1].Value.Length > 0)
                    list.Add(new ArlCondition("header", Unescape(m.Groups[1].Value)));
            }

            foreach (Match m in TitleRe.Matches(rule))
            {
                if (m.Groups[1].Value.Length > 0)
                    list.Add(new ArlCondition("title", Unescape(m.Groups[1].Value)));
            }

            foreach (Match m in IconHashRe.Matches(rule))
                list.Add(new ArlCondition("icon_hash", m.Groups[1].Value));

            return list;
        }

        private static string Unescape(string s) =>
            s.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

        private sealed class ArlCondition(string type, string keyword)
        {
            public bool IsMatch(string body, string header, string title, WebMatchContext context) => type switch
            {
                "body" => body.Contains(keyword, StringComparison.OrdinalIgnoreCase),
                "header" => header.Contains(keyword, StringComparison.OrdinalIgnoreCase),
                "title" => title.Contains(keyword, StringComparison.OrdinalIgnoreCase),
                "icon_hash" => MatchesIconHash(context, keyword),
                _ => false
            };

            private static bool MatchesIconHash(WebMatchContext context, string keyword)
            {
                var split = RawHttpSplitter.Split(context.RawContent);
                var bodyBytes = split.Body.IsEmpty ? context.RawContent : split.Body.ToArray();
                if (bodyBytes.Length == 0)
                    return false;
                var shodan = FaviconMmh3Hasher.Compute(bodyBytes);
                return string.Equals(shodan, keyword, StringComparison.Ordinal);
            }
        }
    }
}
