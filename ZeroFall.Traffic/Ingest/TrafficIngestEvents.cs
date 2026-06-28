using ZeroFall.Traffic.Capture;

namespace ZeroFall.Traffic.Ingest;

/// <summary>?????????????? Browser ? <c>TrafficEntryIngestedEvent</c> ????</summary>
public sealed record TrafficCaptureDecision(
    TrafficCaptureRecord Capture,
    TrafficCaptureDedup.Decision Decision,
    string? SupersededEntryId = null);
