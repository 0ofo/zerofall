namespace ZeroFall.Fingerprint.Engines;

internal interface IWebMatchEngine
{
    string Name { get; }
    bool SupportWeb { get; }
    Core.FrameworkSet WebMatch(byte[] rawContent, Core.WebMatchContext context);
}

internal interface IFaviconContributor
{
    void ContributeFaviconHashes(FaviconEngine faviconEngine);
}

internal interface IActiveFingerprintEngine
{
    Task<Core.FrameworkSet> ActiveMatchAsync(
        string baseUrl,
        int level,
        Func<string, Task<byte[]?>> sender,
        CancellationToken cancellationToken = default);
}
