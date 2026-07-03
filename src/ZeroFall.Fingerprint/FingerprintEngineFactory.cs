using ZeroFall.Fingerprint.Core;
using ZeroFall.Fingerprint.Engines;

namespace ZeroFall.Fingerprint;

public static class FingerprintEngineFactory
{
    public static FingerprintEngine Create(FingerprintEngineOptions options)
    {
        var dir = options.FingerprintsDirectory;
        var engines = new List<IWebMatchEngine>();
        FingersHttpEngine? fingers = null;
        EholeEngine? ehole = null;

        void TryAdd<T>(Func<string, T> loader, string? customPath, string defaultFile, string engineName) where T : IWebMatchEngine
        {
            if (!IsEngineEnabled(options, engineName))
                return;

            var path = customPath ?? (options.NoDefault ? null : Path.Combine(dir, defaultFile));
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            var engine = loader(path);
            engines.Add(engine);
            if (engine is FingersHttpEngine f)
                fingers = f;
            if (engine is EholeEngine e)
                ehole = e;
        }

        if (!options.NoDefault || options.EholePath is not null)
            TryAdd(EholeEngine.Load, options.EholePath, "ehole.json", "ehole");
        if (!options.NoDefault || options.GobyPath is not null)
            TryAdd(GobyEngine.Load, options.GobyPath, "goby.json", "goby");
        if (!options.NoDefault || options.WappalyzerPath is not null)
            TryAdd(WappalyzerEngine.Load, options.WappalyzerPath, "wappalyzer.json", "wappalyzer");
        if (!options.NoDefault || options.FingersPath is not null)
            TryAdd(FingersHttpEngine.Load, options.FingersPath, "fingers_http.json", "fingers");
        if (!options.NoDefault || options.FingerprintHubPath is not null)
            TryAdd(FingerprintHubEngine.Load, options.FingerprintHubPath, "fingerprinthub_web.json", "fingerprinthub");
        if (!options.NoDefault || options.ArlPath is not null)
            TryAdd(ArlEngine.Load, options.ArlPath, "ARL.yaml", "arl");

        var favicon = new FaviconEngine();
        fingers?.ContributeFaviconHashes(favicon);
        ehole?.ContributeFaviconHashes(favicon);
        if (ehole is null && IsEngineEnabled(options, "favicon"))
        {
            var eholePath = options.EholePath ?? Path.Combine(dir, "ehole.json");
            EholeEngine.ContributeFaviconHashesFromFile(eholePath, favicon);
        }

        var aliasesPath = options.AliasesPath
            ?? Path.Combine(dir, "aliases.yaml");
        if (!File.Exists(aliasesPath))
            aliasesPath = Path.Combine(AppContext.BaseDirectory, "aliases.yaml");

        var aliases = AliasRegistry.Load(aliasesPath);
        if (fingers is not null)
        {
            aliases.AppendBaseline(fingers.GetFingerBaselines());
        }

        return new FingerprintEngine(engines, favicon, aliases, IsEngineEnabled(options, "favicon"));
    }

    private static bool IsEngineEnabled(FingerprintEngineOptions options, string engineName)
    {
        if (options.EnabledEngines is not { Count: > 0 })
            return true;

        foreach (var enabled in options.EnabledEngines)
        {
            if (string.Equals(enabled, engineName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
