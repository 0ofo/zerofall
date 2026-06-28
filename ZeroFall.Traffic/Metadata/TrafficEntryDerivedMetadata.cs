using ZeroFall.Traffic.Capture;

namespace ZeroFall.Traffic.Metadata;

/// <summary>派生元数据：捕获时写入，筛选/归档/网站树直接读列。</summary>
public readonly record struct TrafficEntryDerivedMetadata
{
    public TrafficMimeSnapshot Mime { get; init; }
    public string SessionDocumentHost { get; init; }
    public bool HasQuery { get; init; }
    public bool FingerprintEligible { get; init; }
    public int ResponseBodyLength { get; init; }
    public int? StatusCode { get; init; }
    public string Host { get; init; }
    public string Path { get; init; }
    public string Extension { get; init; }
    public string RequestContentType { get; init; }
    public string ResponseContentType { get; init; }

    public TrafficEntryDerivedMetadata()
    {
        SessionDocumentHost = string.Empty;
        Host = string.Empty;
        Path = string.Empty;
        Extension = string.Empty;
        RequestContentType = string.Empty;
        ResponseContentType = string.Empty;
    }

    public static TrafficEntryDerivedMetadata FromCaptureFields(
        TrafficCaptureFields fields,
        int responseBodyLength = 0,
        bool fingerprintEligible = false) =>
        new()
        {
            Mime = fields.Mime,
            SessionDocumentHost = fields.SessionDocumentHost,
            HasQuery = fields.Uri.HasQuery,
            StatusCode = fields.StatusCode,
            Host = fields.Uri.Host,
            Path = fields.Uri.Path,
            Extension = fields.Uri.Extension,
            RequestContentType = fields.RequestContentType,
            ResponseContentType = fields.ResponseContentType,
            ResponseBodyLength = responseBodyLength,
            FingerprintEligible = fingerprintEligible
        };
}
