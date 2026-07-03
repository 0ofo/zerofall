using System.Text.Json;
using System.Collections.Generic;
using ZeroFall.Platform.Models;

namespace ZeroFall.Browser.Services;

/// <summary>单次被动/主动指纹识别的审计上下文，写入 .zerofall.db 供后期引擎质量研判。</summary>
public sealed class FingerprintAuditRecord
{
    public required string Trigger { get; init; }
    public string EntryId { get; init; } = string.Empty;
    public string BrowserTabId { get; init; } = string.Empty;
    public required string Url { get; init; }
    public string TopLevelUrl { get; init; } = string.Empty;
    public required string RequestAuthority { get; init; }
    public required string RootAuthority { get; init; }
    public bool IsCrossHost { get; init; }
    public TrafficAssetKind AssetKind { get; init; }
    public WebTrafficResourceContext ResourceContext { get; init; } = WebTrafficResourceContext.Unknown;
    public string Status { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public int BodyBytes { get; init; }
    public int HeaderBytes { get; init; }
    public int DurationMs { get; init; }
    public IReadOnlyList<string> EnabledEngines { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RawHitsByEngine { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AppliedHitsByEngine { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyList<string> MergedToRequestHost { get; init; } = [];
    public IReadOnlyList<string> RolledUpToRoot { get; init; } = [];
    public IReadOnlyList<string> RequestHostOnly { get; init; } = [];
    public string? PageTitle { get; init; }
    public string? ServerHeader { get; init; }
}

/// <summary>引擎质量汇总行（由 SQL 聚合生成）。</summary>
public sealed class FingerprintEngineQualityRow
{
    public required string Engine { get; init; }
    public int RunCount { get; init; }
    public int HitRows { get; init; }
    public int UniqueFrameworks { get; init; }
    public double HitsPerRun { get; init; }
    public int RolledUpHits { get; init; }
    public double RollupRate { get; init; }
}

/// <summary>多引擎一致命中（交集研判）。</summary>
public sealed class FingerprintEngineAgreementRow
{
    public required string FrameworkName { get; init; }
    public required string Engines { get; init; }
    public int EngineCount { get; init; }
    public int HitRows { get; init; }
}
