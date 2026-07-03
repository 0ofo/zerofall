namespace ZeroFall.Fingerprint;

public enum RedirectPolicy
{
    Never,
    Http,
    All
}

public static class RedirectPolicyParser
{
    public static RedirectPolicy Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" or "never" => RedirectPolicy.Never,
        "http" => RedirectPolicy.Http,
        "all" => RedirectPolicy.All,
        _ => throw new ArgumentException($"invalid redirect policy: {raw}")
    };

    public static bool FollowHttp(this RedirectPolicy policy) =>
        policy is RedirectPolicy.Http or RedirectPolicy.All;

    public static bool FollowContent(this RedirectPolicy policy) =>
        policy is RedirectPolicy.All;
}

public sealed class FingerprintScanResult
{
    public required string Url { get; init; }
    public string Cms { get; init; } = string.Empty;
    public string Server { get; init; } = string.Empty;
    public int StatusCode { get; init; }
    public int Length { get; init; }
    public string Title { get; init; } = string.Empty;
}

public sealed class FingerprintEngineOptions
{
    public string FingerprintsDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "fingerprints");

    public bool NoDefault { get; set; }
    public string? EholePath { get; set; }
    public string? GobyPath { get; set; }
    public string? WappalyzerPath { get; set; }
    public string? FingersPath { get; set; }
    public string? FingerprintHubPath { get; set; }
    public string? ArlPath { get; set; }
    public string? AliasesPath { get; set; }

    /// <summary>
    /// 仅加载指定引擎（如 wappalyzer、goby、favicon）。为 null 或空时加载全部默认引擎。
    /// <c>favicon</c> 只启用图标哈希匹配，不会加载 ehole 被动规则。
    /// </summary>
    public IReadOnlyList<string>? EnabledEngines { get; set; }
}

public sealed class FingerprintScanOptions
{
    public int TimeoutSeconds { get; set; } = 10;
    public string? Proxy { get; set; }
    public RedirectPolicy RedirectPolicy { get; set; } = RedirectPolicy.Never;
    public int ThreadCount { get; set; } = 50;
}
