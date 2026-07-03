using ZeroFall.Browser.ViewModels;
using ZeroFall.Traffic.Ingest;

namespace ZeroFall.Browser.Services;

/// <summary>流量入库后发布（dedup 已在 <see cref="TrafficIngestGateway"/> 完成一次）。</summary>
public sealed record TrafficEntryIngestedEvent(
    TrafficLogEntryViewModel Entry,
    TrafficCaptureDedup.Decision Decision,
    string? SupersededEntryId = null);

public sealed record TrafficBodyIngestedEvent(
    string EntryId,
    string RequestBody,
    string ResponseBody,
    byte[]? RequestBodyRaw,
    byte[]? ResponseBodyRaw,
    bool FingerprintEligible,
    int ResponseBodyLength);
