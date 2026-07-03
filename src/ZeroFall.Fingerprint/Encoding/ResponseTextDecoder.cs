using System.Text;
using System.Text.RegularExpressions;

namespace ZeroFall.Fingerprint.Text;

public static partial class ResponseTextDecoder
{
    static ResponseTextDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetRegex();

    public static string ExtractTitle(string body)
    {
        var match = TitleRegex().Match(body);
        if (!match.Success)
            return string.Empty;

        return match.Groups[1].Value
            .Replace("\n", string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\t", string.Empty)
            .Trim();
    }

    public static string DecodeToUtf8(ReadOnlySpan<byte> rawBody, string? contentType)
    {
        var encodingName = DetectEncodingName(contentType, rawBody);
        if (string.IsNullOrEmpty(encodingName) || encodingName.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8.GetString(rawBody);

        try
        {
            var encoding = Encoding.GetEncoding(encodingName);
            return encoding.GetString(rawBody);
        }
        catch
        {
            return Encoding.UTF8.GetString(rawBody);
        }
    }

    private static string DetectEncodingName(string? contentType, ReadOnlySpan<byte> rawBody)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            var fromHeader = ParseCharset(contentType);
            if (!string.IsNullOrEmpty(fromHeader))
                return NormalizeEncodingName(fromHeader);
        }

        var text = Encoding.UTF8.GetString(rawBody);
        var metaIdx = text.IndexOf("<meta", StringComparison.OrdinalIgnoreCase);
        if (metaIdx >= 0)
        {
            var end = text.IndexOf('>', metaIdx);
            if (end > metaIdx)
            {
                var fromMeta = ParseCharset(text[metaIdx..(end + 1)]);
                if (!string.IsNullOrEmpty(fromMeta))
                    return NormalizeEncodingName(fromMeta);
            }
        }

        return "utf-8";
    }

    private static string? ParseCharset(string value)
    {
        var match = CharsetRegex().Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string NormalizeEncodingName(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower switch
        {
            "gbk" or "gb2312" or "gb18030" => "gb18030",
            "big5" => "big5",
            "windows-1252" => "windows-1252",
            _ => lower
        };
    }
}
