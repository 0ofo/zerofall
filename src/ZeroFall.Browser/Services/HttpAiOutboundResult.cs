using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using ZeroFall.Base.AiTools;
using ZeroFall.Traffic;
using ZeroFall.Traffic.Capture;

namespace ZeroFall.Browser.Services;

/// <summary>AI <c>fetch</c> 出站请求结果（工具 JSON + 流量归档）。</summary>
internal sealed class HttpAiOutboundResult
{
    public required string EntryId { get; init; }
    public string? ErrorJson { get; init; }

    public int Status { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    public int LatencyMs { get; init; }
    public Dictionary<string, string> ResponseHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string ResponseBodyFull { get; init; } = string.Empty;
    public int ResponseBodyChars { get; init; }
    public string RequestHeadersWire { get; init; } = string.Empty;
    public string RequestBody { get; init; } = string.Empty;

    public bool IsToolError => ErrorJson != null;

    public static HttpAiOutboundResult Fail(string message) =>
        new() { EntryId = Guid.NewGuid().ToString("N"), ErrorJson = ToolResultJson.Error(message) };

    public static HttpAiOutboundResult FromException(string entryId, Exception ex, int latencyMs) =>
        new()
        {
            EntryId = entryId,
            ErrorJson = ToolResultJson.Data(o =>
            {
                o["ok"] = false;
                o["error"] = ex.Message;
                o["latencyMs"] = latencyMs;
                o["transport"] = "outbound";
            })
        };

    public string ToToolJson()
    {
        var bodyChars = ResponseBodyChars > 0 ? ResponseBodyChars : ResponseBodyFull.Length;

        var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in TrafficHttpHeaders.FromWireText(RequestHeadersWire).Entries)
            requestHeaders[name] = value;

        return ToolResultJson.Data(o =>
        {
            o["entryId"] = EntryId;
            o["status"] = Status;
            o["statusText"] = StatusText;
            o["url"] = Url;
            o["method"] = Method;
            o["latencyMs"] = LatencyMs;
            o["transport"] = "outbound";
            o["requestHeaders"] = ToJsonObject(requestHeaders);
            o["headers"] = ToJsonObject(ResponseHeaders);
            o["body"] = ResponseBodyFull;
            o["bodyChars"] = bodyChars;
        });
    }

    public TrafficCaptureRecord ToCaptureRecord()
    {
        var reqHeaders = TrafficHttpHeaders.FromWireText(RequestHeadersWire);
        var respHeaders = TrafficHttpHeaders.FromWireText(ToResponseHeadersWire());
        var responseBytes = Encoding.UTF8.GetBytes(ResponseBodyFull);

        return TrafficCaptureRecord.FromAiFetch(
            EntryId,
            DateTime.Now.ToString("HH:mm:ss.fff"),
            Method,
            Url,
            Status,
            LatencyMs,
            reqHeaders,
            respHeaders,
            RequestBody,
            ResponseBodyFull,
            responseBytes.Length == 0 ? null : responseBytes);
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> headers)
    {
        var o = new JsonObject();
        foreach (var (name, value) in headers)
            o[name] = value;
        return o;
    }

    private string ToResponseHeadersWire()
    {
        if (ResponseHeaders.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var (name, value) in ResponseHeaders)
            sb.Append(name).Append(": ").AppendLine(value);
        return sb.ToString();
    }
}
