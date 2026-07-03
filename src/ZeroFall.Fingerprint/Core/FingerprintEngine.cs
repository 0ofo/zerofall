using ZeroFall.Fingerprint.Engines;

namespace ZeroFall.Fingerprint.Core;

public sealed class FingerprintEngine
{
    private readonly IReadOnlyList<IWebMatchEngine> _engines;
    private readonly FaviconEngine _favicon;
    private readonly AliasRegistry _aliases;
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);

    internal FingerprintEngine(
        IReadOnlyList<IWebMatchEngine> engines,
        FaviconEngine favicon,
        AliasRegistry aliases,
        bool enableFavicon = false)
    {
        _engines = engines;
        _favicon = favicon;
        _aliases = aliases;
        foreach (var engine in engines)
            _enabled[engine.Name] = true;
        _enabled[favicon.Name] = enableFavicon;
    }

    public FaviconEngine Favicon => _favicon;
    public AliasRegistry Aliases => _aliases;

    public void Enable(string name)
    {
        if (_enabled.ContainsKey(name))
            _enabled[name] = true;
    }

    public void Disable(string name) => _enabled[name] = false;

    public IReadOnlyList<string> GetEnabledWebEngineNames()
    {
        var names = new List<string>();
        foreach (var engine in _engines)
        {
            if (_enabled.TryGetValue(engine.Name, out var enabled) && enabled && engine.SupportWeb)
                names.Add(engine.Name);
        }

        return names;
    }

    public FrameworkSet WebMatch(byte[] rawContent, string cert = "")
    {
        var context = WebMatchContext.FromRaw(rawContent, cert);
        var combined = new FrameworkSet();

        foreach (var engine in _engines)
        {
            if (!_enabled.TryGetValue(engine.Name, out var enabled) || !enabled || !engine.SupportWeb)
                continue;

            var matches = engine.WebMatch(rawContent, context);
            MergeFrameworks(combined, matches);
        }

        return combined;
    }

    public FingerprintWebMatchBreakdown WebMatchBreakdown(byte[] rawContent, string cert = "")
    {
        var context = WebMatchContext.FromRaw(rawContent, cert);
        var combined = new FrameworkSet();
        var byEngine = new Dictionary<string, FrameworkSet>(StringComparer.OrdinalIgnoreCase);

        foreach (var engine in _engines)
        {
            if (!_enabled.TryGetValue(engine.Name, out var enabled) || !enabled || !engine.SupportWeb)
                continue;

            var matches = engine.WebMatch(rawContent, context);
            byEngine[engine.Name] = matches;
            MergeFrameworks(combined, matches);
        }

        return new FingerprintWebMatchBreakdown(combined, byEngine, GetEnabledWebEngineNames());
    }

    public FrameworkSet WebMatch(WebMatchContext context)
    {
        var combined = new FrameworkSet();
        foreach (var engine in _engines)
        {
            if (!_enabled.TryGetValue(engine.Name, out var enabled) || !enabled || !engine.SupportWeb)
                continue;
            MergeFrameworks(combined, engine.WebMatch(context.RawContent, context));
        }

        return combined;
    }

    public FrameworkSet MatchFavicon(byte[] content)
    {
        var matches = _favicon.MatchFavicon(content);
        var combined = new FrameworkSet();
        MergeFrameworks(combined, matches);
        return combined;
    }

    public async Task<FrameworkSet> ActiveMatchAsync(
        string baseUrl,
        int level,
        Func<string, Task<byte[]?>> sender,
        CancellationToken cancellationToken = default)
    {
        var breakdown = await ActiveMatchBreakdownAsync(baseUrl, level, sender, cancellationToken)
            .ConfigureAwait(false);
        return breakdown.Combined;
    }

    public async Task<FingerprintWebMatchBreakdown> ActiveMatchBreakdownAsync(
        string baseUrl,
        int level,
        Func<string, Task<byte[]?>> sender,
        CancellationToken cancellationToken = default)
    {
        var combined = new FrameworkSet();
        var byEngine = new Dictionary<string, FrameworkSet>(StringComparer.OrdinalIgnoreCase);
        var enabled = new List<string>();

        foreach (var engine in _engines)
        {
            if (engine is not IActiveFingerprintEngine active)
                continue;
            if (!_enabled.TryGetValue(engine.Name, out var on) || !on)
                continue;

            enabled.Add(engine.Name);
            var matches = await active.ActiveMatchAsync(baseUrl, level, sender, cancellationToken)
                .ConfigureAwait(false);
            byEngine[engine.Name] = matches;
            MergeFrameworks(combined, matches);
        }

        return new FingerprintWebMatchBreakdown(combined, byEngine, enabled);
    }

    public void MergeFrameworks(FrameworkSet origin, FrameworkSet other)
    {
        foreach (var frame in other.Values)
        {
            var working = new Framework(frame.Name, frame.Source)
            {
                Version = frame.Version
            };
            foreach (var source in frame.Sources)
                working.Sources.Add(source);
            foreach (var tag in frame.Tags)
                working.Tags.Add(tag);

            var (alias, accepted) = _aliases.FindFramework(working);
            if (alias is not null)
            {
                if (accepted)
                    working.Name = alias.Name;
                else
                    continue;
            }

            origin.Add(working);
        }
    }
}
