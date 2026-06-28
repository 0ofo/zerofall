using System;

namespace ZeroFall.Traffic.Capture;

/// <summary>WebView2 <c>CoreWebView2WebResourceContext</c> 映射；与 Platform 层同名枚举值对齐。</summary>
public enum TrafficResourceContext
{
    Unknown = 0,
    Document,
    Stylesheet,
    Image,
    Media,
    Font,
    Script,
    XmlHttpRequest,
    Fetch,
    TextTrack,
    EventSource,
    WebSocket,
    Manifest,
    SignedExchange,
    Ping,
    CSPViolationReport,
    Other
}

public static class TrafficResourceContextMapper
{
    public static TrafficResourceContext FromPlatform(int platformValue) =>
        Enum.IsDefined(typeof(TrafficResourceContext), platformValue)
            ? (TrafficResourceContext)platformValue
            : TrafficResourceContext.Unknown;
}
