using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ZeroFall.Browser.Serialization;

public sealed class PageNavigateParams
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";
}

public sealed class PageReloadParams
{
    [JsonPropertyName("ignoreCache")]
    public bool IgnoreCache { get; init; }
}

public sealed class NetworkGetCookiesParams
{
    [JsonPropertyName("urls")]
    public string[] Urls { get; init; } = [];
}

public sealed class EmulationSetDeviceMetricsOverrideParams
{
    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("deviceScaleFactor")]
    public double DeviceScaleFactor { get; init; } = 1;

    [JsonPropertyName("mobile")]
    public bool Mobile { get; init; }

    [JsonPropertyName("scale")]
    public int Scale { get; init; } = 1;
}

public sealed class EmulatedMediaFeature
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("value")]
    public string Value { get; init; } = "";
}

public sealed class EmulationSetEmulatedMediaParams
{
    [JsonPropertyName("features")]
    public EmulatedMediaFeature[] Features { get; init; } = [];
}

public sealed class PageNavigateToHistoryEntryParams
{
    [JsonPropertyName("entryId")]
    public int EntryId { get; init; }
}

public sealed class RuntimeEvaluateParams
{
    [JsonPropertyName("expression")]
    public string Expression { get; init; } = "";

    [JsonPropertyName("returnByValue")]
    public bool ReturnByValue { get; init; } = true;

    [JsonPropertyName("awaitPromise")]
    public bool AwaitPromise { get; init; }
}

public sealed class DomGetDocumentParams
{
    [JsonPropertyName("depth")]
    public int Depth { get; init; } = -1;

    [JsonPropertyName("pierce")]
    public bool Pierce { get; init; } = true;
}

public sealed class DomGetOuterHtmlParams
{
    [JsonPropertyName("nodeId")]
    public int NodeId { get; init; }
}

public sealed class PageSetDocumentContentParams
{
    [JsonPropertyName("frameId")]
    public string FrameId { get; init; } = "";

    [JsonPropertyName("html")]
    public string Html { get; init; } = "";
}

public sealed class HttpFetchConfig
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("method")]
    public string Method { get; init; } = "GET";

    [JsonPropertyName("body")]
    public string Body { get; init; } = "";

    [JsonPropertyName("headersJson")]
    public string? HeadersJson { get; init; }

}

public sealed class BrowserTabListItemDto
{
    [JsonPropertyName("tabId")]
    public string TabId { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("hasCdpSession")]
    public bool HasCdpSession { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; }

    [JsonPropertyName("isEphemeral")]
    public bool IsEphemeral { get; init; }
}

public sealed class BrowserTabListResponseDto
{
    [JsonPropertyName("activeTabId")]
    public string? ActiveTabId { get; init; }

    [JsonPropertyName("tabs")]
    public List<BrowserTabListItemDto> Tabs { get; init; } = [];
}
