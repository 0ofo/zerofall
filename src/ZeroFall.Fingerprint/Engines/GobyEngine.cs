using System.Text.Json;
using ZeroFall.Fingerprint.Core;
using ZeroFall.Fingerprint.Json;

namespace ZeroFall.Fingerprint.Engines;

public sealed class GobyEngine : IWebMatchEngine
{
    private readonly List<GobyCompiledEntry> _entries;

    private GobyEngine(List<GobyCompiledEntry> entries) => _entries = entries;

    public string Name => "goby";
    public bool SupportWeb => true;

    public static GobyEngine Load(string path)
    {
        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize(json, FingerprintJsonContext.Default.GobyEntryDtoArray) ?? [];
        return new GobyEngine(entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.Rule is { Count: > 0 })
            .Select(GobyCompiledEntry.Create)
            .ToList());
    }

    public FrameworkSet WebMatch(byte[] rawContent, WebMatchContext context)
    {
        var raw = context.LowerText;
        var result = new FrameworkSet();
        foreach (var entry in _entries)
        {
            var frame = entry.Match(raw);
            if (frame is not null)
                result.Add(frame);
        }

        return result;
    }

    private sealed class GobyCompiledEntry
    {
        private readonly string _logic;
        private readonly Dictionary<string, GobyCompiledRule> _rules;

        public string Name { get; }

        private GobyCompiledEntry(string name, string logic, Dictionary<string, GobyCompiledRule> rules)
        {
            Name = name;
            _logic = logic;
            _rules = rules;
        }

        public static GobyCompiledEntry Create(GobyEntryDto dto)
        {
            var rules = new Dictionary<string, GobyCompiledRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in dto.Rule ?? [])
            {
                if (string.IsNullOrWhiteSpace(rule.Label) || string.IsNullOrWhiteSpace(rule.Feature))
                    continue;
                rules[rule.Label] = new GobyCompiledRule(rule.Feature.ToLowerInvariant(), rule.IsEqual);
            }

            return new GobyCompiledEntry(dto.Name!, dto.Logic ?? "a", rules);
        }

        public Framework? Match(string raw)
        {
            var vars = _rules.ToDictionary(p => p.Key, p => p.Value.IsMatch(raw), StringComparer.OrdinalIgnoreCase);
            return GobyLogicEvaluator.Evaluate(_logic, vars) ? new Framework(Name, FrameworkSource.Goby) : null;
        }
    }

    private sealed class GobyCompiledRule(string feature, bool isEqual)
    {
        public bool IsMatch(string raw)
        {
            var found = raw.Contains(feature, StringComparison.Ordinal);
            return isEqual ? found : !found;
        }
    }
}

internal static class GobyLogicEvaluator
{
    public static bool Evaluate(string logic, IReadOnlyDictionary<string, bool> vars)
    {
        logic = logic.Replace(" ", string.Empty, StringComparison.Ordinal);
        return EvaluateOr(logic, vars);
    }

    private static bool EvaluateOr(string expr, IReadOnlyDictionary<string, bool> vars)
    {
        foreach (var orPart in SplitTopLevel(expr, "||"))
        {
            if (EvaluateAnd(orPart, vars))
                return true;
        }

        return false;
    }

    private static bool EvaluateAnd(string expr, IReadOnlyDictionary<string, bool> vars)
    {
        expr = expr.Trim();
        if (expr.StartsWith('(') && expr.EndsWith(')') && IsBalanced(expr[1..^1]))
            return EvaluateOr(expr[1..^1], vars);

        foreach (var andPart in SplitTopLevel(expr, "&&"))
        {
            if (!EvaluateTerm(andPart.Trim(), vars))
                return false;
        }

        return true;
    }

    private static bool EvaluateTerm(string term, IReadOnlyDictionary<string, bool> vars)
    {
        if (term.StartsWith('(') && term.EndsWith(')') && IsBalanced(term[1..^1]))
            return EvaluateOr(term[1..^1], vars);

        if (term.Contains("||", StringComparison.Ordinal) || term.Contains("&&", StringComparison.Ordinal))
            return EvaluateOr(term, vars);

        return vars.TryGetValue(term, out var value) && value;
    }

    private static IEnumerable<string> SplitTopLevel(string expr, string separator)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < expr.Length; i++)
        {
            if (expr[i] == '(') depth++;
            else if (expr[i] == ')') depth--;
            else if (depth == 0 && expr.AsSpan(i).StartsWith(separator, StringComparison.Ordinal))
            {
                yield return expr[start..i];
                i += separator.Length - 1;
                start = i + 1;
            }
        }

        yield return expr[start..];
    }

    private static bool IsBalanced(string expr)
    {
        var depth = 0;
        foreach (var ch in expr)
        {
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            if (depth < 0) return false;
        }

        return depth == 0;
    }
}
