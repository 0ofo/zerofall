namespace ZeroFall.Traffic.Capture;

/// <summary>???????? DTO????? + ?????????</summary>
public sealed class TrafficCaptureRecord
{
    public required string EntryId { get; init; }
    public required TrafficCaptureSource Source { get; init; }
    public required string TimeText { get; init; }
    public required string TabLabel { get; init; }
    public required string BrowserTabId { get; init; }
    public required int PageSessionId { get; init; }
    public required string TopLevelUrl { get; init; }
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required string Status { get; init; }
    public long? LatencyMs { get; init; }
    public TrafficHttpHeaders RequestHeaders { get; init; } = TrafficHttpHeaders.Empty;
    public TrafficHttpHeaders ResponseHeaders { get; init; } = TrafficHttpHeaders.Empty;
    public string RequestBody { get; init; } = string.Empty;
    public string ResponseBody { get; init; } = string.Empty;
    public byte[]? RequestBodyRaw { get; init; }
    public byte[]? ResponseBodyRaw { get; init; }
    public TrafficResourceContext ResourceContext { get; init; } = TrafficResourceContext.Unknown;
    public TrafficCaptureFields Fields { get; init; }

    public static TrafficCaptureRecord FromBrowser(
        string entryId,
        string timeText,
        string tabLabel,
        string browserTabId,
        int pageSessionId,
        string topLevelUrl,
        string method,
        string url,
        int statusCode,
        long? latencyMs,
        TrafficHttpHeaders requestHeaders,
        TrafficHttpHeaders responseHeaders,
        string requestBody,
        byte[]? requestBodyRaw,
        TrafficResourceContext resourceContext)
    {
        var fields = TrafficCaptureFields.Compute(url, topLevelUrl, statusCode, requestHeaders, responseHeaders);
        return new TrafficCaptureRecord
        {
            EntryId = entryId,
            Source = TrafficCaptureSource.Browser,
            TimeText = timeText,
            TabLabel = tabLabel,
            BrowserTabId = browserTabId,
            PageSessionId = pageSessionId,
            TopLevelUrl = topLevelUrl,
            Method = method,
            Url = url,
            Status = statusCode.ToString(),
            LatencyMs = latencyMs,
            RequestHeaders = requestHeaders,
            ResponseHeaders = responseHeaders,
            RequestBody = requestBody,
            RequestBodyRaw = requestBodyRaw,
            ResourceContext = resourceContext,
            Fields = fields
        };
    }

    public static TrafficCaptureRecord FromProxy(
        string entryId,
        string timeText,
        string method,
        string url,
        int? statusCode,
        long? latencyMs,
        TrafficHttpHeaders requestHeaders,
        TrafficHttpHeaders responseHeaders,
        string requestBody,
        string responseBody,
        byte[]? requestBodyRaw,
        byte[]? responseBodyRaw)
    {
        var fields = TrafficCaptureFields.Compute(url, url, statusCode, requestHeaders, responseHeaders);
        var statusText = statusCode is int code && code > 0 ? code.ToString() : "-";
        return new TrafficCaptureRecord
        {
            EntryId = entryId,
            Source = TrafficCaptureSource.Proxy,
            TimeText = timeText,
            TabLabel = ProxyTrafficSource.TabLabel,
            BrowserTabId = ProxyTrafficSource.BrowserTabId,
            PageSessionId = 0,
            TopLevelUrl = url,
            Method = method,
            Url = url,
            Status = statusText,
            LatencyMs = latencyMs,
            RequestHeaders = requestHeaders,
            ResponseHeaders = responseHeaders,
            RequestBody = requestBody,
            ResponseBody = responseBody,
            RequestBodyRaw = requestBodyRaw,
            ResponseBodyRaw = responseBodyRaw,
            Fields = fields
        };
    }
}

public static class ProxyTrafficSource
{
    public const string BrowserTabId = "proxy";
    public const string TabLabel = "Proxy";
}
