namespace ZeroFall.Traffic.Ingest;

/// <summary>捕获端统一提交入口（Browser / Fluxzy 实现）。</summary>
public interface ITrafficCaptureSink
{
    void Submit(Traffic.Capture.TrafficCaptureRecord capture);
    void SubmitBodyUpdate(TrafficBodyCaptureUpdate update);
}

/// <summary>响应体异步补全。</summary>
public sealed record TrafficBodyCaptureUpdate(
    string EntryId,
    string RequestBody,
    string ResponseBody,
    byte[]? RequestBodyRaw,
    byte[]? ResponseBodyRaw);
