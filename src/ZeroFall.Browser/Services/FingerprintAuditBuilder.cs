using System;
using System.Collections.Generic;
using System.Linq;
using ZeroFall.Fingerprint.Core;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;

namespace ZeroFall.Browser.Services;

internal static class FingerprintAuditBuilder
{
    public static Dictionary<string, FrameworkSet> ApplyBrowserEngineFilters(
        IReadOnlyDictionary<string, FrameworkSet> byEngine,
        TrafficAssetKind assetKind)
    {
        var applied = new Dictionary<string, FrameworkSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var (engine, set) in byEngine)
        {
            if (assetKind != TrafficAssetKind.Html
                && string.Equals(engine, "goby", StringComparison.OrdinalIgnoreCase))
            {
                applied[engine] = new FrameworkSet();
                continue;
            }

            applied[engine] = CloneSet(set);
        }

        return applied;
    }

    public static FrameworkSet CombineSets(IEnumerable<FrameworkSet> sets)
    {
        var combined = new FrameworkSet();
        foreach (var set in sets)
            combined.Merge(set);
        return combined;
    }

    public static FingerprintAuditRecord BuildPassiveRecord(
        WebTrafficRecordedEvent traffic,
        string requestAuthority,
        string rootAuthority,
        TrafficAssetKind assetKind,
        FingerprintWebMatchBreakdown breakdown,
        IReadOnlyDictionary<string, FrameworkSet> appliedByEngine,
        FrameworkSet appliedCombined,
        FrameworkSet rollup,
        int durationMs)
    {
        var isCrossHost = !string.Equals(
            SiteTechnologyStore.NormalizeAuthority(requestAuthority),
            SiteTechnologyStore.NormalizeAuthority(rootAuthority),
            StringComparison.OrdinalIgnoreCase);

        var mergedDisplays = appliedCombined.Values
            .Select(f => f.DisplayText)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rollupDisplays = rollup.Values
            .Select(f => f.DisplayText)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var requestOnly = isCrossHost
            ? mergedDisplays
                .Except(rollupDisplays, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        var context = WebMatchContext.FromRaw(
            TrafficHttpRawBuilder.BuildResponseRaw(
                traffic.Status,
                traffic.ResponseHeaders,
                traffic.ResponseBody,
                traffic.ResponseBodyRaw));

        return new FingerprintAuditRecord
        {
            Trigger = "passive_traffic",
            EntryId = traffic.EntryId,
            BrowserTabId = traffic.BrowserTabId,
            Url = traffic.Url,
            TopLevelUrl = traffic.TopLevelUrl ?? string.Empty,
            RequestAuthority = requestAuthority,
            RootAuthority = rootAuthority,
            IsCrossHost = isCrossHost,
            AssetKind = assetKind,
            ResourceContext = traffic.ResourceContext,
            Status = traffic.Status,
            ContentType = TrafficHttpRawBuilder.ParseContentType(traffic.ResponseHeaders),
            BodyBytes = TrafficHttpRawBuilder.ComputeBodySignature(traffic.ResponseBody, traffic.ResponseBodyRaw),
            HeaderBytes = string.IsNullOrEmpty(traffic.ResponseHeaders)
                ? 0
                : traffic.ResponseHeaders.Length,
            DurationMs = durationMs,
            EnabledEngines = breakdown.EnabledEngines,
            RawHitsByEngine = FingerprintWebMatchBreakdown.ToNameMap(breakdown.ByEngine),
            AppliedHitsByEngine = FingerprintWebMatchBreakdown.ToNameMap(appliedByEngine),
            MergedToRequestHost = mergedDisplays,
            RolledUpToRoot = rollupDisplays,
            RequestHostOnly = requestOnly,
            PageTitle = context.Title,
            ServerHeader = TryGetServerHeader(traffic.ResponseHeaders)
        };
    }

    public static FingerprintAuditRecord BuildActiveRecord(
        string siteAuthority,
        string baseUrl,
        FingerprintWebMatchBreakdown breakdown,
        int durationMs)
    {
        var displays = breakdown.Combined.Values
            .Select(f => f.DisplayText)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FingerprintAuditRecord
        {
            Trigger = "active_probe",
            Url = baseUrl,
            TopLevelUrl = baseUrl,
            RequestAuthority = siteAuthority,
            RootAuthority = siteAuthority,
            AssetKind = TrafficAssetKind.Html,
            DurationMs = durationMs,
            EnabledEngines = breakdown.EnabledEngines,
            RawHitsByEngine = FingerprintWebMatchBreakdown.ToNameMap(breakdown.ByEngine),
            AppliedHitsByEngine = FingerprintWebMatchBreakdown.ToNameMap(breakdown.ByEngine),
            MergedToRequestHost = displays,
            RolledUpToRoot = [],
            RequestHostOnly = []
        };
    }

    private static FrameworkSet CloneSet(FrameworkSet source)
    {
        var clone = new FrameworkSet();
        clone.Merge(source);
        return clone;
    }

    private static string? TryGetServerHeader(string headers)
    {
        if (string.IsNullOrWhiteSpace(headers))
            return null;

        foreach (var line in headers.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Server:", StringComparison.OrdinalIgnoreCase))
                return trimmed["Server:".Length..].Trim();
        }

        return null;
    }
}
