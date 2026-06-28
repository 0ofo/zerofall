namespace Datafinder.Platform.Models;

/// <summary>对齐 WebView2 COREWEBVIEW2_WEB_RESOURCE_CONTEXT。</summary>
public enum WebTrafficResourceContext
{
    Unknown = 0,
    Document = 1,
    Stylesheet = 2,
    Image = 3,
    Media = 4,
    Font = 5,
    Script = 6,
    XmlHttpRequest = 7,
    Fetch = 8,
    TextTrack = 9,
    EventSource = 10,
    WebSocket = 11,
    Manifest = 12,
    SignedExchange = 13,
    Ping = 14,
    CspViolationReport = 15,
    Other = 16
}

public static class WebTrafficResourceContextExtensions
{
    public static WebTrafficResourceContext FromWebView2(int value) =>
        value is >= (int)WebTrafficResourceContext.Document and <= (int)WebTrafficResourceContext.Other
            ? (WebTrafficResourceContext)value
            : WebTrafficResourceContext.Unknown;

    public static bool IsApiLike(this WebTrafficResourceContext context) =>
        context is WebTrafficResourceContext.XmlHttpRequest or WebTrafficResourceContext.Fetch;

    public static string ToStorageKey(this WebTrafficResourceContext context) =>
        context == WebTrafficResourceContext.Unknown ? string.Empty : context.ToString();
}
