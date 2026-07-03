using System.Text.RegularExpressions;

namespace ZeroFall.Fingerprint.Engines;

internal static class RegexFactory
{
    public static Regex? TryCreate(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            return null;
        }
    }
}
