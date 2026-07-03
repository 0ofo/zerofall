using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using ZeroFall.Browser.Services;
using ZeroFall.Traffic;
using ZeroFall.Traffic.Capture;
using ZeroFall.Traffic.Metadata;
using TrafficMetadata = ZeroFall.Traffic.Metadata.TrafficEntryDerivedMetadata;

namespace ZeroFall.Browser.ViewModels;

public sealed class TrafficLogEntryViewModel : INotifyPropertyChanged
{
    public required string EntryId { get; init; }
    public required string Time { get; init; }
    public required string Tab { get; init; }
    public required string BrowserTabId { get; init; }
    public TrafficCaptureSource CaptureSource { get; init; } = TrafficCaptureSource.Browser;
    public required int PageSessionId { get; init; }
    public required string TopLevelUrl { get; init; }
    public WebTrafficResourceContext ResourceContext { get; init; } = WebTrafficResourceContext.Unknown;
    public string ResourceContextText =>
        ResourceContext == WebTrafficResourceContext.Unknown ? string.Empty : ResourceContext.ToString();
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required string Status { get; init; }
    public long? LatencyMs { get; init; }
    public string ResponseLatencyText => LatencyMs is long ms ? $"{ms} ms" : "—";
    public required string RequestHeaders { get; init; }
    private string _requestBody = string.Empty;
    private byte[]? _requestBodyRaw;
    public required string RequestBody
    {
        get => _requestBody;
        set
        {
            if (string.Equals(_requestBody, value, StringComparison.Ordinal))
                return;

            _requestBody = value;
            _httpRequestText = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HttpRequestText));
        }
    }

    public byte[]? RequestBodyRaw
    {
        get => _requestBodyRaw;
        set
        {
            if (ReferenceEquals(_requestBodyRaw, value))
                return;

            _requestBodyRaw = value is { Length: 0 } ? null : value;
            _httpRequestText = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HttpRequestText));
        }
    }
    public required string ResponseHeaders { get; init; }
    private string _responseBody = string.Empty;
    private byte[]? _responseBodyRaw;
    public required string ResponseBody
    {
        get => _responseBody;
        set
        {
            if (string.Equals(_responseBody, value, StringComparison.Ordinal))
                return;

            _responseBody = value;
            _httpResponseText = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HttpResponseText));
        }
    }

    public byte[]? ResponseBodyRaw
    {
        get => _responseBodyRaw;
        set
        {
            if (ReferenceEquals(_responseBodyRaw, value))
                return;

            _responseBodyRaw = value is { Length: 0 } ? null : value;
            _httpResponseText = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HttpResponseText));
        }
    }

    private TrafficHighlightColor _color;
    public TrafficHighlightColor Color
    {
        get => _color;
        set
        {
            if (_color == value)
                return;

            _color = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHighlight));
        }
    }

    private string _remark = string.Empty;
    public string Remark
    {
        get => _remark;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(_remark, normalized, StringComparison.Ordinal))
                return;

            _remark = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRemark));
        }
    }

    public bool HasHighlight => Color != TrafficHighlightColor.None;
    public bool HasRemark => !string.IsNullOrWhiteSpace(Remark);

    private TrafficMetadata _derivedMetadata;
    private bool _hasDerivedMetadata;

    public bool HasDerivedMetadata => _hasDerivedMetadata;
    public TrafficMetadata DerivedMetadata => _derivedMetadata;
    public TrafficMimeSnapshot Mime => _derivedMetadata.Mime;
    public string SessionDocumentHost => _derivedMetadata.SessionDocumentHost;
    public bool HasUrlQuery => _derivedMetadata.HasQuery;
    public bool FingerprintEligible => _derivedMetadata.FingerprintEligible;
    public int ResponseBodyLength => _derivedMetadata.ResponseBodyLength;
    public int? StoredStatusCode => _derivedMetadata.StatusCode;

    public void ApplyDerivedMetadata(TrafficMetadata metadata)
    {
        _derivedMetadata = metadata;
        _hasDerivedMetadata = true;
        _responseContentType = metadata.ResponseContentType;
        _requestContentType = metadata.RequestContentType;
    }

    private string? _httpRequestText;
    private string? _httpResponseText;
    private string? _requestContentType;
    private string? _responseContentType;

    public string HttpRequestText => _httpRequestText ??= BuildHttpRequestText();

    public string HttpResponseText => _httpResponseText ??= BuildHttpResponseText();

    public string RequestContentType =>
        _hasDerivedMetadata && !string.IsNullOrEmpty(_derivedMetadata.RequestContentType)
            ? _derivedMetadata.RequestContentType
            : _requestContentType ??= ExtractContentType(RequestHeaders);

    public string ResponseContentType =>
        _hasDerivedMetadata && !string.IsNullOrEmpty(_derivedMetadata.ResponseContentType)
            ? _derivedMetadata.ResponseContentType
            : _responseContentType ??= ExtractContentType(ResponseHeaders);

    public static TrafficHighlightColor ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TrafficHighlightColor.None;

        return Enum.TryParse<TrafficHighlightColor>(value.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : TrafficHighlightColor.None;
    }

    public static string ToStorageValue(TrafficHighlightColor color) =>
        color == TrafficHighlightColor.None ? string.Empty : color.ToString().ToLowerInvariant();

    private string BuildHttpRequestText()
    {
        var sb = new StringBuilder();
        if (!HttpRequestComposer.HeadersStartWithRequestLine(RequestHeaders))
        {
            var pathAndQuery = "/";
            if (Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                pathAndQuery = uri.PathAndQuery;

            sb.AppendLine($"{Method} {pathAndQuery} HTTP/1.1");
        }

        if (!string.IsNullOrEmpty(RequestHeaders))
            sb.Append(RequestHeaders);

        sb.AppendLine();

        if (RequestBodyRaw is { Length: > 0 } requestRaw)
            sb.Append(TrafficBodyCodec.FormatBodyForRawView(requestRaw, RequestContentType));
        else if (!string.IsNullOrEmpty(RequestBody))
            sb.Append(RequestBody);

        return sb.ToString();
    }

    private string BuildHttpResponseText()
    {
        var sb = new StringBuilder();

        if (!HttpRequestComposer.HeadersStartWithResponseLine(ResponseHeaders))
            sb.AppendLine($"HTTP/1.1 {Status}");

        if (!string.IsNullOrEmpty(ResponseHeaders))
            sb.Append(ResponseHeaders);

        sb.AppendLine();

        if (ResponseBodyRaw is { Length: > 0 } responseRaw)
            sb.Append(TrafficBodyCodec.FormatBodyForRawView(responseRaw, ResponseContentType));
        else if (!string.IsNullOrEmpty(ResponseBody))
            sb.Append(ResponseBody);

        return sb.ToString();
    }

    private static string ExtractContentType(string headers)
    {
        foreach (var line in headers.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["Content-Type:".Length..].Trim();
                var semi = value.IndexOf(';');
                return semi >= 0 ? value[..semi].Trim().ToLowerInvariant() : value.ToLowerInvariant();
            }
        }
        return string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
