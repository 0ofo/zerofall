using System;
using System.IO;
using System.Text;

namespace ZeroFall.Browser.Services;

internal static class TrafficBodyCodec
{
    public static byte[] ReadStreamBytes(Func<byte[], uint, uint> read, int maxBytes = 0)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        while (true)
        {
            var allowed = maxBytes > 0 ? Math.Min(buffer.Length, maxBytes - (int)ms.Length) : buffer.Length;
            if (allowed <= 0)
                break;

            var readCount = read(buffer, (uint)allowed);
            if (readCount == 0)
                break;

            ms.Write(buffer, 0, (int)readCount);
        }

        return ms.ToArray();
    }

    public static (byte[] Raw, string Text) EncodeBody(byte[] bytes)
    {
        if (bytes.Length == 0)
            return ([], string.Empty);

        var text = DecodeBodyText(bytes);
        return (bytes, text);
    }

    public static string DecodeBodyText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        return TryDecodeUtf8(bytes);
    }

    public static byte[]? ResolveRawBody(byte[]? raw, string textBody)
    {
        if (raw is { Length: > 0 })
            return raw;

        return string.IsNullOrEmpty(textBody) ? null : Encoding.UTF8.GetBytes(textBody);
    }

    /// <summary>Raw 视图 body 段：文本类型 UTF-8；二进制用 Latin-1 逐字节映射，避免 SQLite/UTF-8 在 NUL 处截断。</summary>
    public static string FormatBodyForRawView(ReadOnlySpan<byte> raw, string contentType)
    {
        if (raw.IsEmpty)
            return string.Empty;

        if (IsTextLikeContentType(contentType))
            return Encoding.UTF8.GetString(raw);

        // 与 Burp Raw 类似：每个字节对应一个可见字符；NUL 替换为 '.'，避免编辑器在 \0 处截断。
        var text = Encoding.Latin1.GetString(raw);
        return text.Contains('\0') ? text.Replace('\0', '.') : text;
    }

    public static bool IsTextLikeContentType(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        return contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
               || contentType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
               || contentType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
               || contentType.Equals("application/x-javascript", StringComparison.OrdinalIgnoreCase)
               || contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
               || contentType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
               || contentType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
               || contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryDecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        return Encoding.UTF8.GetString(bytes);
    }
}
