namespace ZeroFall.Fingerprint.Core;

/// <summary>单次 Web 指纹识别的分引擎结果，供审计日志与引擎质量比对。</summary>
public sealed class FingerprintWebMatchBreakdown
{
    public FrameworkSet Combined { get; }
    public IReadOnlyDictionary<string, FrameworkSet> ByEngine { get; }
    public IReadOnlyList<string> EnabledEngines { get; }

    internal FingerprintWebMatchBreakdown(
        FrameworkSet combined,
        IReadOnlyDictionary<string, FrameworkSet> byEngine,
        IReadOnlyList<string> enabledEngines)
    {
        Combined = combined;
        ByEngine = byEngine;
        EnabledEngines = enabledEngines;
    }

    public IReadOnlyDictionary<string, int> HitCountsByEngine()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (engine, set) in ByEngine)
            counts[engine] = set.Count;
        return counts;
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ToNameMap(
        IReadOnlyDictionary<string, FrameworkSet> byEngine)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (engine, set) in byEngine)
        {
            map[engine] = set.Values
                .Select(f => f.DisplayText)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return map;
    }
}
