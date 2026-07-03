using System;
using System.Linq;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Traffic;
using ZeroFall.Traffic.Capture;
using ZeroFall.Traffic.Metadata;
using TrafficMetadata = ZeroFall.Traffic.Metadata.TrafficEntryDerivedMetadata;

namespace ZeroFall.Browser.Services;

/// <summary>派生元数据计算器：优先使用捕获层已算清的 <see cref="TrafficCaptureFields"/>。</summary>
public static class TrafficEntryMetadataComputer
{
    public static TrafficMetadata Compute(TrafficLogEntryViewModel entry)
    {
        if (entry.HasDerivedMetadata)
            return entry.DerivedMetadata;

        var mime = TrafficMimeSnapshot.FromStructured(
            TrafficHttpHeaders.FromWireText(entry.ResponseHeaders),
            entry.Url);
        return new TrafficMetadata
        {
            Mime = mime,
            SessionDocumentHost = TrafficSessionDocumentHost.Resolve(entry.TopLevelUrl, entry.Url),
            HasQuery = TrafficUriFacts.FromUrl(entry.Url).HasQuery,
            FingerprintEligible = TrafficFingerprintScope.ShouldFingerprint(BuildProbeEvent(entry, mime)),
            ResponseBodyLength = MeasureResponseBodyLength(entry.ResponseBody, entry.ResponseBodyRaw),
            StatusCode = ParseStatusCode(entry.Status),
            Host = TrafficUriFacts.FromUrl(entry.Url).Host,
            Path = TrafficUriFacts.FromUrl(entry.Url).Path,
            Extension = TrafficUriFacts.FromUrl(entry.Url).Extension,
            RequestContentType = TrafficHttpHeaders.FromWireText(entry.RequestHeaders).GetContentTypeMediaType(),
            ResponseContentType = mime.MediaType
        };
    }

    public static TrafficMetadata ComputeFromCapture(TrafficCaptureRecord capture, TrafficLogEntryViewModel entry)
    {
        var bodyLen = MeasureResponseBodyLength(entry.ResponseBody, entry.ResponseBodyRaw);
        var meta = TrafficMetadata.FromCaptureFields(capture.Fields, bodyLen);
        var probe = BuildProbeEvent(entry, meta.Mime);
        return meta with { FingerprintEligible = TrafficFingerprintScope.ShouldFingerprint(probe) };
    }

    public static TrafficMetadata Compute(WebTrafficRecordedEvent e)
    {
        var mime = e.MimeFilterCategory >= 0
            ? new TrafficMimeSnapshot
            {
                FilterCategory = (TrafficMimeCategory)e.MimeFilterCategory,
                PrimaryClass = e.MimePrimaryClass ?? string.Empty,
                MediaType = e.MimeType ?? string.Empty
            }
            : TrafficMimeSnapshot.FromStructured(
                TrafficHttpHeaders.FromWireText(e.ResponseHeaders),
                e.Url);

        var uri = TrafficUriFacts.FromUrl(e.Url);
        return new TrafficMetadata
        {
            Mime = mime,
            SessionDocumentHost = !string.IsNullOrWhiteSpace(e.SessionDocumentHost)
                ? e.SessionDocumentHost
                : TrafficSessionDocumentHost.Resolve(e.TopLevelUrl, e.Url),
            HasQuery = e.HasQuery || uri.HasQuery,
            FingerprintEligible = e.FingerprintEligible || TrafficFingerprintScope.ShouldFingerprint(e),
            ResponseBodyLength = e.ResponseBodyLength > 0
                ? e.ResponseBodyLength
                : MeasureResponseBodyLength(e.ResponseBody, e.ResponseBodyRaw),
            StatusCode = e.StatusCode ?? ParseStatusCode(e.Status),
            Host = uri.Host,
            Path = uri.Path,
            Extension = uri.Extension,
            RequestContentType = TrafficHttpHeaders.FromWireText(e.RequestHeaders).GetContentTypeMediaType(),
            ResponseContentType = mime.MediaType
        };
    }

    public static void Apply(TrafficLogEntryViewModel entry) =>
        entry.ApplyDerivedMetadata(Compute(entry));

    public static void Apply(TrafficLogEntryViewModel entry, TrafficCaptureRecord capture) =>
        entry.ApplyDerivedMetadata(ComputeFromCapture(capture, entry));

    public static WebTrafficRecordedEvent ToEvent(TrafficLogEntryViewModel entry)
    {
        var meta = entry.HasDerivedMetadata ? entry.DerivedMetadata : Compute(entry);
        return BuildEvent(entry, meta);
    }

    public static WebTrafficRecordedEvent BuildEvent(TrafficLogEntryViewModel entry, TrafficMetadata meta) =>
        new(
            entry.EntryId,
            entry.Time,
            entry.Tab,
            entry.BrowserTabId,
            entry.PageSessionId,
            entry.TopLevelUrl,
            entry.Method,
            entry.Url,
            entry.Status,
            entry.RequestHeaders,
            entry.RequestBody,
            entry.ResponseHeaders,
            entry.ResponseBody,
            entry.LatencyMs,
            entry.RequestBodyRaw,
            entry.ResponseBodyRaw,
            entry.ResourceContext,
            (int)meta.Mime.FilterCategory,
            meta.Mime.PrimaryClass,
            meta.Mime.MediaType,
            meta.SessionDocumentHost,
            meta.HasQuery,
            meta.FingerprintEligible,
            meta.ResponseBodyLength,
            meta.StatusCode);

    public static string ResolveSessionDocumentHost(string? topLevelUrl, string? requestUrl) =>
        TrafficSessionDocumentHost.Resolve(topLevelUrl, requestUrl);

    private static WebTrafficRecordedEvent BuildProbeEvent(TrafficLogEntryViewModel entry, TrafficMimeSnapshot mime) =>
        new(
            entry.EntryId,
            entry.Time,
            entry.Tab,
            entry.BrowserTabId,
            entry.PageSessionId,
            entry.TopLevelUrl,
            entry.Method,
            entry.Url,
            entry.Status,
            entry.RequestHeaders,
            entry.RequestBody,
            entry.ResponseHeaders,
            entry.ResponseBody,
            entry.LatencyMs,
            entry.RequestBodyRaw,
            entry.ResponseBodyRaw,
            entry.ResourceContext,
            (int)mime.FilterCategory,
            mime.PrimaryClass,
            mime.MediaType);

    private static int MeasureResponseBodyLength(string responseBody, byte[]? responseBodyRaw)
    {
        if (responseBodyRaw is { Length: > 0 })
            return responseBodyRaw.Length;
        return string.IsNullOrEmpty(responseBody) ? 0 : responseBody.Length;
    }

    private static int? ParseStatusCode(string statusText)
    {
        if (string.IsNullOrWhiteSpace(statusText))
            return null;
        var first = statusText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, out var code) ? code : null;
    }
}
