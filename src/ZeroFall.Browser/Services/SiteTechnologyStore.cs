using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Fingerprint.Core;

namespace ZeroFall.Browser.Services;

public sealed class SiteTechnologyStore
{
    private readonly ConcurrentDictionary<string, FrameworkSet> _bySite = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _activeProbeSites = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private long _notifyGeneration;

    public event Action? TechnologiesChanged;

    public IReadOnlyList<Framework> GetFrameworks(string siteAuthority)
    {
        if (string.IsNullOrWhiteSpace(siteAuthority))
            return [];

        lock (_sync)
        {
            return _bySite.TryGetValue(NormalizeAuthority(siteAuthority), out var set)
                ? SnapshotFrameworks(set)
                : [];
        }
    }

    public string GetCmsSummary(string siteAuthority)
    {
        lock (_sync)
        {
            if (!_bySite.TryGetValue(NormalizeAuthority(siteAuthority), out var set))
                return string.Empty;

            return string.Join(", ", SnapshotFrameworks(set).Select(f => f.DisplayText));
        }
    }

    public void Merge(string siteAuthority, FrameworkSet matches)
    {
        if (matches.Count == 0 || string.IsNullOrWhiteSpace(siteAuthority))
            return;

        if (MergeInto(siteAuthority, matches))
            ScheduleNotify();
    }

    /// <summary>资源站全量入库；跨 host 时仅将可汇总项合并到主站。</summary>
    public void MergeTraffic(
        string requestAuthority,
        FrameworkSet matches,
        string? rootAuthority,
        string url,
        string responseHeaders)
    {
        if (matches.Count == 0)
            return;

        var changed = MergeInto(requestAuthority, matches);

        if (!string.IsNullOrWhiteSpace(rootAuthority)
            && !string.Equals(
                NormalizeAuthority(requestAuthority),
                NormalizeAuthority(rootAuthority),
                StringComparison.OrdinalIgnoreCase))
        {
            var rollup = TechnologyRollupFilter.SelectForRootRollup(url, responseHeaders, matches);
            if (rollup.Count > 0)
                changed |= MergeInto(rootAuthority, rollup);
        }

        if (changed)
            ScheduleNotify();
    }

    private bool MergeInto(string siteAuthority, FrameworkSet matches)
    {
        lock (_sync)
        {
            var key = NormalizeAuthority(siteAuthority);
            var set = _bySite.GetOrAdd(key, _ => new FrameworkSet());
            var before = Snapshot(set);
            set.Merge(matches);
            return !string.Equals(before, Snapshot(set), StringComparison.Ordinal);
        }
    }

    private static IReadOnlyList<Framework> SnapshotFrameworks(FrameworkSet set) =>
        set.Values
            .Where(f => f is not null && !string.IsNullOrEmpty(f.Name))
            .OrderBy(f => f.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Snapshot(FrameworkSet set) =>
        string.Join('|', set.Values
            .Where(f => f is not null && !string.IsNullOrEmpty(f.Name))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.DisplayText));

    private void ScheduleNotify()
    {
        var generation = Interlocked.Increment(ref _notifyGeneration);
        _ = Task.Run(async () =>
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _notifyGeneration))
                return;
            TechnologiesChanged?.Invoke();
        });
    }

    public void Clear()
    {
        lock (_sync)
        {
            _bySite.Clear();
            _activeProbeSites.Clear();
        }
    }

    public bool IsActiveProbeEnabled(string siteAuthority) =>
        _activeProbeSites.ContainsKey(NormalizeAuthority(siteAuthority));

    public void SetActiveProbeEnabled(string siteAuthority, bool enabled)
    {
        var key = NormalizeAuthority(siteAuthority);
        if (enabled)
            _activeProbeSites[key] = 0;
        else
            _activeProbeSites.TryRemove(key, out _);
    }

    public static string NormalizeAuthority(string authority)
    {
        var value = authority.Trim().ToLowerInvariant();
        if (value.EndsWith(":80", StringComparison.Ordinal))
            return value[..^3];
        if (value.EndsWith(":443", StringComparison.Ordinal))
            return value[..^4];
        return value;
    }

    public static string ResolveRequestAuthority(string requestUrl)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var reqUri) || string.IsNullOrWhiteSpace(reqUri.Host))
            return string.Empty;
        return NormalizeAuthority(reqUri.Authority);
    }

    /// <summary>地址栏 TopLevelUrl 对应的根站点 authority。</summary>
    public static string ResolveRootAuthority(string? topLevelUrl, string requestUrl)
    {
        if (WebsiteTreeRootContext.TryResolveAuthority(topLevelUrl, out var authority))
            return authority;

        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var reqUri) || string.IsNullOrWhiteSpace(reqUri.Host))
            return string.Empty;
        return NormalizeAuthority(reqUri.Authority);
    }
}
