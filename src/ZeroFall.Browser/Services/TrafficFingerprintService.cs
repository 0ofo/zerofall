using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Fingerprint;
using ZeroFall.Fingerprint.Core;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Services;

namespace ZeroFall.Browser.Services;

public sealed class TrafficFingerprintService : IDisposable
{
    private const int MaxCachedTrafficEvents = 8000;
    private const int MaxLiveParallelism = 4;

    /// <summary>浏览器被动指纹：wappalyzer + fingerprinthub + arl + favicon。</summary>
    private static readonly string[] BrowserEnabledEngines = ["wappalyzer", "fingerprinthub", "arl", "favicon"];

    private readonly SiteTechnologyStore _store;
    private readonly FingerprintAuditJournalService _auditJournal;
    private readonly IOutboundHttpClientFactory _httpClientFactory;
    private readonly TrafficMonitorTabViewModel _trafficMonitor;
    private readonly WebsiteTreeRootContext _rootContext;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _liveConcurrency = new(MaxLiveParallelism, MaxLiveParallelism);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, int> _processedBodySig = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WebTrafficRecordedEvent> _trafficByEntryId = new(StringComparer.Ordinal);
    private readonly IDisposable _trafficSub;
    private readonly IDisposable _bodySub;
    private readonly IDisposable _clearedSub;
    private FingerprintEngine? _engine;

    public TrafficFingerprintService(
        IEventBus eventBus,
        SiteTechnologyStore store,
        FingerprintAuditJournalService auditJournal,
        IOutboundHttpClientFactory httpClientFactory,
        TrafficMonitorTabViewModel trafficMonitor,
        WebsiteTreeRootContext rootContext)
    {
        _store = store;
        _auditJournal = auditJournal;
        _httpClientFactory = httpClientFactory;
        _trafficMonitor = trafficMonitor;
        _rootContext = rootContext;
        _trafficSub = eventBus.SubscribeDisposable<TrafficEntryIngestedEvent>(e =>
            OnTrafficRecorded(TrafficEntryMetadataComputer.ToEvent(e.Entry)));
        _bodySub = eventBus.SubscribeDisposable<WebTrafficBodyUpdatedEvent>(OnBodyUpdated);
        _clearedSub = eventBus.SubscribeDisposable<TrafficRecordsClearedEvent>(OnTrafficCleared);
        Warmup();
    }

    private void OnTrafficCleared(TrafficRecordsClearedEvent e)
    {
        _store.Clear();
        _processedBodySig.Clear();
        _trafficByEntryId.Clear();
        _ = _auditJournal.ClearAsync();
    }

    public SiteTechnologyStore Store => _store;

    /// <summary>后台预加载指纹引擎，避免首条流量等待数秒。</summary>
    public void Warmup() => _ = Task.Run(async () =>
    {
        try
        {
            await GetEngineAsync().ConfigureAwait(false);
        }
        catch
        {
            // 预热失败不影响后续懒加载
        }
    });

    public async Task RunActiveProbeAsync(string siteAuthority, string baseUrl, CancellationToken cancellationToken = default)
    {
        var engine = await GetEngineAsync(cancellationToken).ConfigureAwait(false);
        var sender = CreateActiveSender(baseUrl, cancellationToken);
        var sw = Stopwatch.StartNew();
        var breakdown = await engine.ActiveMatchBreakdownAsync(baseUrl, level: 2, sender, cancellationToken)
            .ConfigureAwait(false);
        sw.Stop();
        _store.Merge(siteAuthority, breakdown.Combined);
        _auditJournal.Enqueue(FingerprintAuditBuilder.BuildActiveRecord(
            siteAuthority, baseUrl, breakdown, (int)sw.ElapsedMilliseconds));
    }

    private void OnTrafficRecorded(WebTrafficRecordedEvent e)
    {
        CacheTrafficEvent(e);
        ScheduleLiveFingerprint(e);
    }

    private void OnBodyUpdated(WebTrafficBodyUpdatedEvent e)
    {
        if (!TryResolveTrafficEvent(e.EntryId, out var baseEvent))
            return;

        _processedBodySig.TryRemove(e.EntryId, out _);

        ScheduleLiveFingerprint(new WebTrafficRecordedEvent(
            baseEvent.EntryId,
            baseEvent.Time,
            baseEvent.Tab,
            baseEvent.BrowserTabId,
            baseEvent.PageSessionId,
            baseEvent.TopLevelUrl,
            baseEvent.Method,
            baseEvent.Url,
            baseEvent.Status,
            baseEvent.RequestHeaders,
            e.RequestBody,
            baseEvent.ResponseHeaders,
            e.ResponseBody,
            baseEvent.LatencyMs,
            e.RequestBodyRaw,
            e.ResponseBodyRaw,
            baseEvent.ResourceContext,
            baseEvent.MimeFilterCategory,
            baseEvent.MimePrimaryClass,
            baseEvent.MimeType,
            baseEvent.SessionDocumentHost,
            baseEvent.HasQuery,
            FingerprintEligible: false,
            ResponseBodyLength: e.ResponseBodyRaw?.Length ?? e.ResponseBody.Length,
            baseEvent.StatusCode));
    }

    private void ScheduleLiveFingerprint(WebTrafficRecordedEvent e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _liveConcurrency.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    await ProcessTrafficAsync(e, runActiveIfEnabled: true).ConfigureAwait(false);
                }
                finally
                {
                    _liveConcurrency.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
        }, _cts.Token);
    }

    private void CacheTrafficEvent(WebTrafficRecordedEvent e)
    {
        _trafficByEntryId[e.EntryId] = WithoutBodies(e);
        if (_trafficByEntryId.Count <= MaxCachedTrafficEvents)
            return;

        foreach (var key in _trafficByEntryId.Keys)
        {
            if (_trafficByEntryId.Count <= MaxCachedTrafficEvents)
                break;
            _trafficByEntryId.TryRemove(key, out _);
        }
    }

    private static WebTrafficRecordedEvent WithoutBodies(WebTrafficRecordedEvent e) =>
        e with
        {
            RequestBody = string.Empty,
            ResponseBody = string.Empty,
            RequestBodyRaw = null,
            ResponseBodyRaw = null
        };

    private bool TryResolveTrafficEvent(string entryId, out WebTrafficRecordedEvent trafficEvent)
    {
        if (_trafficByEntryId.TryGetValue(entryId, out var cached))
        {
            var live = _trafficMonitor.FindEntryById(entryId);
            trafficEvent = live is not null ? WithBodiesFromEntry(cached, live) : cached;
            return true;
        }

        var entry = _trafficMonitor.FindEntryById(entryId);
        if (entry is null)
        {
            trafficEvent = null!;
            return false;
        }

        trafficEvent = TrafficEntryMetadataComputer.ToEvent(entry);
        CacheTrafficEvent(trafficEvent);
        return true;
    }

    private static WebTrafficRecordedEvent WithBodiesFromEntry(
        WebTrafficRecordedEvent metadata,
        TrafficLogEntryViewModel entry) =>
        metadata with
        {
            RequestBody = entry.RequestBody,
            ResponseBody = entry.ResponseBody,
            RequestBodyRaw = entry.RequestBodyRaw,
            ResponseBodyRaw = entry.ResponseBodyRaw,
            ResponseBodyLength = entry.ResponseBodyLength
        };

    private async Task ProcessTrafficAsync(WebTrafficRecordedEvent e, bool runActiveIfEnabled)
    {
        var requestAuthority = SiteTechnologyStore.ResolveRequestAuthority(e.Url);
        if (string.IsNullOrEmpty(requestAuthority))
            return;

        if (!TrafficFingerprintScope.ShouldFingerprint(e))
            return;

        var bodySig = ComputeContentSignature(e);
        if (!TrafficHttpRawBuilder.HasAnalyzableContent(e.ResponseHeaders, e.ResponseBody, e.ResponseBodyRaw))
            return;

        if (_processedBodySig.TryGetValue(e.EntryId, out var prevSig) && prevSig == bodySig)
            return;

        try
        {
            var engine = await GetEngineAsync().ConfigureAwait(false);
            var raw = TrafficHttpRawBuilder.BuildResponseRaw(
                e.Status, e.ResponseHeaders, e.ResponseBody, e.ResponseBodyRaw);
            var assetKind = TechnologyRollupFilter.ClassifyAsset(e.Url, e.ResponseHeaders);
            var sw = Stopwatch.StartNew();
            var breakdown = engine.WebMatchBreakdown(raw);
            sw.Stop();

            var appliedByEngine = FingerprintAuditBuilder.ApplyBrowserEngineFilters(breakdown.ByEngine, assetKind);
            var matches = FingerprintAuditBuilder.CombineSets(appliedByEngine.Values);
            if (matches.Count == 0 && !TrafficHttpRawBuilder.IsFaviconRequest(e.Url))
            {
                if (assetKind == TrafficAssetKind.Html || breakdown.Combined.Count > 0)
                {
                    var rootForLog = WebsiteTreeRootContext.ResolveRootAuthority(e, _rootContext) ?? string.Empty;
                    _auditJournal.Enqueue(FingerprintAuditBuilder.BuildPassiveRecord(
                        e,
                        requestAuthority,
                        rootForLog,
                        assetKind,
                        breakdown,
                        appliedByEngine,
                        matches,
                        new FrameworkSet(),
                        (int)sw.ElapsedMilliseconds));
                }

                if (TrafficHttpRawBuilder.ComputeBodySignature(e.ResponseBody, e.ResponseBodyRaw) == 0)
                    return;
                _processedBodySig[e.EntryId] = bodySig;
                return;
            }

            var rootAuthority = WebsiteTreeRootContext.ResolveRootAuthority(e, _rootContext);
            if (string.IsNullOrEmpty(rootAuthority))
                return;
            var rollup = TechnologyRollupFilter.SelectForRootRollup(e.Url, e.ResponseHeaders, matches);
            _store.MergeTraffic(requestAuthority, matches, rootAuthority, e.Url, e.ResponseHeaders);
            _auditJournal.Enqueue(FingerprintAuditBuilder.BuildPassiveRecord(
                e,
                requestAuthority,
                rootAuthority,
                assetKind,
                breakdown,
                appliedByEngine,
                matches,
                rollup,
                (int)sw.ElapsedMilliseconds));

            if (TrafficHttpRawBuilder.IsFaviconRequest(e.Url) && e.ResponseBodyRaw is { Length: > 0 })
            {
                var fav = engine.MatchFavicon(e.ResponseBodyRaw);
                _store.Merge(requestAuthority, fav);
            }

            _processedBodySig[e.EntryId] = bodySig;

            if (runActiveIfEnabled
                && !string.IsNullOrEmpty(rootAuthority)
                && _store.IsActiveProbeEnabled(rootAuthority))
            {
                var baseUrl = ResolveBaseUrl(_rootContext.ResolveRootTopLevelUrl(e), e.Url);
                if (!string.IsNullOrEmpty(baseUrl))
                    await RunActiveProbeAsync(rootAuthority, baseUrl).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrafficFingerprint] ProcessTraffic failed: {ex.Message}");
        }
    }

    private static int ComputeContentSignature(WebTrafficRecordedEvent e)
    {
        var bodySig = TrafficHttpRawBuilder.ComputeBodySignature(e.ResponseBody, e.ResponseBodyRaw);
        if (bodySig > 0)
            return bodySig;

        return string.IsNullOrEmpty(e.ResponseHeaders)
            ? 0
            : string.GetHashCode(e.ResponseHeaders, StringComparison.Ordinal);
    }

    private Func<string, Task<byte[]?>> CreateActiveSender(string baseUrl, CancellationToken cancellationToken)
    {
        return async path =>
        {
            try
            {
                var normalizedPath = string.IsNullOrWhiteSpace(path) ? "/" : path;
                if (!normalizedPath.StartsWith('/'))
                    normalizedPath = "/" + normalizedPath;
                var url = baseUrl.TrimEnd('/') + normalizedPath;
                using var client = _httpClientFactory.CreateClient("fingerprint-active", TimeSpan.FromSeconds(10));
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;
                return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        };
    }

    private static string ResolveBaseUrl(string? topLevelUrl, string requestUrl)
    {
        if (!string.IsNullOrWhiteSpace(topLevelUrl) && Uri.TryCreate(topLevelUrl, UriKind.Absolute, out var top))
            return $"{top.Scheme}://{top.Authority}/";
        if (Uri.TryCreate(requestUrl, UriKind.Absolute, out var req))
            return $"{req.Scheme}://{req.Authority}/";
        return string.Empty;
    }

    private async Task<FingerprintEngine> GetEngineAsync(CancellationToken cancellationToken = default)
    {
        if (_engine is not null)
            return _engine;

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _engine ??= await Task.Run(() =>
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "fingerprints");
                var engine = FingerprintEngineFactory.Create(new FingerprintEngineOptions
                {
                    FingerprintsDirectory = dir,
                    AliasesPath = Path.Combine(AppContext.BaseDirectory, "aliases.yaml"),
                    EnabledEngines = BrowserEnabledEngines
                });
                var names = string.Join(", ", engine.GetEnabledWebEngineNames());
                System.Diagnostics.Debug.WriteLine(
                    $"[TrafficFingerprint] engines loaded: {(string.IsNullOrEmpty(names) ? "(none)" : names)}; favicon={(BrowserEnabledEngines.Contains("favicon") ? "on" : "off")}");
                return engine;
            }, cancellationToken).ConfigureAwait(false);
            return _engine;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _trafficSub.Dispose();
        _bodySub.Dispose();
        _clearedSub.Dispose();
        _liveConcurrency.Dispose();
        _loadGate.Dispose();
        _cts.Dispose();
    }
}
