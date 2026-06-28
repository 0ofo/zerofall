using System;
using System.Text.RegularExpressions;
using Datafinder.Platform.Models;

namespace Datafinder.Platform.Services;

internal static class ProxyReplaceApplier
{
    public static string? ReplaceAll(string? input, ProxyReplaceRule rule)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(rule.Match))
            return null;

        try
        {
            if (rule.IsRegex)
            {
                var replaced = Regex.Replace(input, rule.Match, rule.Replacement ?? string.Empty);
                return string.Equals(replaced, input, StringComparison.Ordinal) ? null : replaced;
            }

            if (!input.Contains(rule.Match, StringComparison.Ordinal))
                return null;

            var result = input.Replace(rule.Match, rule.Replacement ?? string.Empty, StringComparison.Ordinal);
            return string.Equals(result, input, StringComparison.Ordinal) ? null : result;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        catch (RegexParseException)
        {
            return null;
        }
    }
}
