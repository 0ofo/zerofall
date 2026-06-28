using System;
using System.Text.RegularExpressions;

namespace Datafinder.Platform.Services;

internal static class ProxyHostMatcher
{
    public static bool Matches(string? pattern, string? host)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var normalizedPattern = pattern.Trim();
        var normalizedHost = host.Trim();
        if (string.Equals(normalizedPattern, "*", StringComparison.Ordinal))
            return true;

        if (!normalizedPattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(normalizedHost, normalizedPattern, StringComparison.OrdinalIgnoreCase)
                || normalizedHost.EndsWith("." + normalizedPattern, StringComparison.OrdinalIgnoreCase);
        }

        var regexPattern = "^" + Regex.Escape(normalizedPattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(normalizedHost, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
