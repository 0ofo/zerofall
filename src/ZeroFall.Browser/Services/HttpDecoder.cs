using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ZeroFall.Browser.Services;

public enum HttpDecodeOperation
{
    UrlDecode,
    UrlEncode,
    Base64Decode,
    Base64Encode,
    HexDecode,
    HexEncode,
    HtmlDecode,
    UnicodeDecode,
    Smart
}

public static partial class HttpDecoder
{
    public static string Transform(string input, HttpDecodeOperation operation)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        try
        {
            return operation switch
            {
                HttpDecodeOperation.UrlDecode => WebUtility.UrlDecode(input),
                HttpDecodeOperation.UrlEncode => WebUtility.UrlEncode(input),
                HttpDecodeOperation.Base64Decode => DecodeBase64(input),
                HttpDecodeOperation.Base64Encode => Convert.ToBase64String(Encoding.UTF8.GetBytes(input)),
                HttpDecodeOperation.HexDecode => DecodeHex(input),
                HttpDecodeOperation.HexEncode => EncodeHex(input),
                HttpDecodeOperation.HtmlDecode => WebUtility.HtmlDecode(input),
                HttpDecodeOperation.UnicodeDecode => DecodeUnicodeEscapes(input),
                HttpDecodeOperation.Smart => SmartDecode(input),
                _ => input
            };
        }
        catch (Exception ex)
        {
            return $"[解码失败] {ex.Message}";
        }
    }

    private static string SmartDecode(string input)
    {
        var trimmed = input.Trim();
        if (LooksLikeBase64(trimmed))
        {
            var decoded = DecodeBase64(trimmed);
            if (!decoded.StartsWith("[解码失败]", StringComparison.Ordinal))
                return decoded;
        }

        if (trimmed.Contains('%'))
            return WebUtility.UrlDecode(trimmed);

        if (HexLike().IsMatch(trimmed))
            return DecodeHex(trimmed);

        return trimmed;
    }

    private static string DecodeBase64(string input)
    {
        var normalized = input.Trim().Replace('\n', ' ').Replace('\r', ' ');
        var bytes = Convert.FromBase64String(normalized);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string DecodeHex(string input)
    {
        var hex = HexLike().Replace(input, string.Empty);
        if (hex.Length % 2 != 0)
            throw new FormatException("十六进制长度必须为偶数");

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

        return Encoding.UTF8.GetString(bytes);
    }

    private static string EncodeHex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));

        return sb.ToString();
    }

    private static string DecodeUnicodeEscapes(string input) =>
        UnicodeEscape().Replace(input, m =>
        {
            var hex = m.Groups[1].Value;
            return char.ConvertFromUtf32(Convert.ToInt32(hex, 16));
        });

    private static bool LooksLikeBase64(string input)
    {
        if (input.Length < 4 || input.Length % 4 != 0)
            return false;

        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch) || ch is '+' or '/' or '=')
                continue;

            return false;
        }

        return true;
    }

    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscape();

    [GeneratedRegex(@"[^0-9a-fA-F]")]
    private static partial Regex HexLike();
}
