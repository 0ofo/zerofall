using System;

namespace ZeroFall.Browser.Services;

internal static class DocumentMimeHelper
{
    public static bool IsHtmlDocument(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        var mime = contentType.Split(';', 2)[0].Trim();
        return mime.Contains("html", StringComparison.OrdinalIgnoreCase)
               || mime.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }
}
