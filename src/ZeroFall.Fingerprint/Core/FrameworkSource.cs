namespace ZeroFall.Fingerprint.Core;

public enum FrameworkSource
{
    Default,
    Active,
    Ico,
    NotFound,
    Guess,
    Redirect,
    Fingers,
    FingerprintHub,
    Wappalyzer,
    Ehole,
    Goby,
    Nmap,
    Arl
}

internal static class FrameworkSourceExtensions
{
    private static readonly Dictionary<FrameworkSource, string> Names = new()
    {
        [FrameworkSource.Default] = "default",
        [FrameworkSource.Active] = "active",
        [FrameworkSource.Ico] = "ico",
        [FrameworkSource.NotFound] = "404",
        [FrameworkSource.Guess] = "guess",
        [FrameworkSource.Redirect] = "redirect",
        [FrameworkSource.Fingers] = "fingers",
        [FrameworkSource.FingerprintHub] = "fingerprinthub",
        [FrameworkSource.Wappalyzer] = "wappalyzer",
        [FrameworkSource.Ehole] = "ehole",
        [FrameworkSource.Goby] = "goby",
        [FrameworkSource.Nmap] = "nmap",
        [FrameworkSource.Arl] = "arl"
    };

    public static string ToKey(this FrameworkSource source) =>
        Names.TryGetValue(source, out var name) ? name : "default";
}
