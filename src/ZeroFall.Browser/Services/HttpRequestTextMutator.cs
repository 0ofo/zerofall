using System;

namespace ZeroFall.Browser.Services;

public static class HttpRequestTextMutator
{
    public static readonly string[] CommonMethods =
        ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE", "CONNECT"];

    public static string ChangeMethod(string httpText, string newMethod)
    {
        if (string.IsNullOrEmpty(httpText))
            return $"{newMethod} / HTTP/1.1";

        var lineEnding = httpText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = httpText.Replace("\r\n", "\n");
        var firstBreak = normalized.IndexOf('\n');
        var firstLine = firstBreak >= 0 ? normalized[..firstBreak] : normalized;
        var remainder = firstBreak >= 0 ? normalized[(firstBreak + 1)..] : string.Empty;

        var parts = firstLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string newFirstLine;
        if (parts.Length >= 2 && parts[^1].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            newFirstLine = $"{newMethod} {string.Join(' ', parts, 1, parts.Length - 1)}";
        else if (parts.Length >= 2)
            newFirstLine = $"{newMethod} {parts[1]} HTTP/1.1";
        else
            newFirstLine = $"{newMethod} / HTTP/1.1";

        if (string.IsNullOrEmpty(remainder))
            return newFirstLine;

        return newFirstLine + lineEnding + remainder.Replace("\n", lineEnding);
    }
}
