namespace ZeroFall.Browser.Serialization;

/// <summary>HTTP 流量行选中时发给 AI 面板的 JSON 载荷（Native AOT 需源生成序列化）。</summary>
public sealed class TrafficSelectionPayloadDto
{
    public string EntryId { get; init; } = string.Empty;
    public string Time { get; init; } = string.Empty;
    public string Tab { get; init; } = string.Empty;
    public string BrowserTabId { get; init; } = string.Empty;
    public string PageSessionId { get; init; } = string.Empty;
    public string TopLevelUrl { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Remark { get; init; } = string.Empty;
    public string Color { get; init; } = string.Empty;
    public string RequestHeaders { get; init; } = string.Empty;
    public string RequestBody { get; init; } = string.Empty;
    public byte[]? RequestBodyRaw { get; init; }
    public string ResponseHeaders { get; init; } = string.Empty;
    public string ResponseBody { get; init; } = string.Empty;
    public byte[]? ResponseBodyRaw { get; init; }
}
