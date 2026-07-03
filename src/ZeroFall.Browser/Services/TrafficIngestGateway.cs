using System;
using ZeroFall.Base.Events;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Traffic;
using ZeroFall.Traffic.Capture;
using ZeroFall.Traffic.Ingest;

namespace ZeroFall.Browser.Services;

/// <summary>唯一流量入库网关：结构化捕获 → 去重 → 元数据 → 发布。Proxy 源入库。</summary>
public sealed class TrafficIngestGateway : ITrafficCaptureSink
{
    private readonly IEventBus _eventBus;
    private readonly TrafficCaptureDedup _dedup = new();

    public TrafficIngestGateway(IEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    public void Reset() => _dedup.Reset();

    public void Submit(TrafficCaptureRecord capture)
    {
        var (decision, supersededEntryId) = _dedup.Evaluate(
            capture.EntryId,
            capture.Source,
            capture.Method,
            capture.Url);

        if (decision == TrafficCaptureDedup.Decision.DropProxyDuplicate)
            return;

        var entry = BuildEntry(capture);
        TrafficEntryMetadataComputer.Apply(entry, capture);

        _eventBus.Publish(new TrafficEntryIngestedEvent(entry, decision, supersededEntryId));
    }

    public void SubmitBodyUpdate(TrafficBodyCaptureUpdate update)
    {
        _eventBus.Publish(new WebTrafficBodyUpdatedEvent(
            update.EntryId,
            update.RequestBody,
            update.ResponseBody,
            update.RequestBodyRaw,
            update.ResponseBodyRaw));
    }

    private static TrafficLogEntryViewModel BuildEntry(TrafficCaptureRecord capture) =>
        new()
        {
            EntryId = capture.EntryId,
            Time = capture.TimeText,
            Tab = capture.TabLabel,
            BrowserTabId = capture.BrowserTabId,
            CaptureSource = capture.Source,
            PageSessionId = capture.PageSessionId,
            TopLevelUrl = capture.TopLevelUrl,
            ResourceContext = MapResourceContext(capture.ResourceContext),
            Method = capture.Method,
            Url = capture.Url,
            Status = capture.Status,
            LatencyMs = capture.LatencyMs,
            RequestHeaders = capture.RequestHeaders.ToWireText(),
            ResponseHeaders = capture.ResponseHeaders.ToWireText(),
            RequestBody = capture.RequestBody,
            ResponseBody = capture.ResponseBody,
            RequestBodyRaw = capture.RequestBodyRaw,
            ResponseBodyRaw = capture.ResponseBodyRaw
        };

    private static WebTrafficResourceContext MapResourceContext(TrafficResourceContext context) =>
        Enum.IsDefined(typeof(WebTrafficResourceContext), (int)context)
            ? (WebTrafficResourceContext)(int)context
            : WebTrafficResourceContext.Unknown;
}
