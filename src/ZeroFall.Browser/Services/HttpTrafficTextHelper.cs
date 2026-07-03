using ZeroFall.Browser.ViewModels;

namespace ZeroFall.Browser.Services;

internal static class HttpTrafficTextHelper
{
    public static string GetText(TrafficLogEntryViewModel entry, HttpTrafficTextPart part) =>
        part switch
        {
            HttpTrafficTextPart.Request => entry.HttpRequestText,
            HttpTrafficTextPart.Response => entry.HttpResponseText,
            HttpTrafficTextPart.RequestBody => GetBodyText(
                entry.RequestBodyRaw,
                entry.RequestBody,
                entry.RequestContentType),
            HttpTrafficTextPart.ResponseBody => GetBodyText(
                entry.ResponseBodyRaw,
                entry.ResponseBody,
                entry.ResponseContentType),
            _ => string.Empty
        };

    public static string BuildLabel(TrafficLogEntryViewModel entry, HttpTrafficTextPart part)
    {
        var shortId = entry.EntryId.Length > 8 ? entry.EntryId[..8] : entry.EntryId;
        var suffix = part switch
        {
            HttpTrafficTextPart.Request => "请求",
            HttpTrafficTextPart.Response => "响应",
            HttpTrafficTextPart.RequestBody => "请求体",
            HttpTrafficTextPart.ResponseBody => "响应体",
            _ => string.Empty
        };

        return $"{entry.Method} {entry.Url} ({suffix}, {shortId})";
    }

    private static string GetBodyText(byte[]? raw, string text, string? contentType)
    {
        if (raw is { Length: > 0 })
            return TrafficBodyCodec.FormatBodyForRawView(raw, contentType ?? string.Empty);

        return text;
    }
}
