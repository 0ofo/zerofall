using ZeroFall.Fingerprint.Hashing;

namespace ZeroFall.Fingerprint.Engines;

public sealed class FaviconEngine : IWebMatchEngine
{
    public Dictionary<string, string> Md5Fingers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Mmh3Fingers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ShodanMmh3Fingers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "favicon";
    public bool SupportWeb => true;

    public Core.FrameworkSet WebMatch(byte[] rawContent, Core.WebMatchContext context) =>
        MatchFavicon(rawContent);

    public Core.FrameworkSet MatchFavicon(byte[] content)
    {
        var result = new Core.FrameworkSet();
        var frame = HashMatch(ContentHasher.Md5Hex(content), ContentHasher.Mmh3Hash32(content));
        if (frame is not null)
            result.Add(frame);

        var shodanHash = FaviconMmh3Hasher.Compute(content);
        if (ShodanMmh3Fingers.TryGetValue(shodanHash, out var shodanName))
            result.Add(new Core.Framework(shodanName, Core.FrameworkSource.Ico));

        return result;
    }

    public Core.Framework? HashMatch(string md5, string mmh3)
    {
        if (Md5Fingers.TryGetValue(md5, out var md5Name))
            return new Core.Framework(md5Name, Core.FrameworkSource.Ico);
        if (Mmh3Fingers.TryGetValue(mmh3, out var mmh3Name))
            return new Core.Framework(mmh3Name, Core.FrameworkSource.Ico);
        return null;
    }
}
