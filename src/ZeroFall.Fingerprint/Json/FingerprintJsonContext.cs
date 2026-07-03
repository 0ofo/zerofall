using System.Text.Json.Serialization;

namespace ZeroFall.Fingerprint.Json;

[JsonSerializable(typeof(EholeRootDto))]
[JsonSerializable(typeof(GobyEntryDto[]))]
[JsonSerializable(typeof(FingersEntryDto[]))]
[JsonSerializable(typeof(FingerprintHubEntryDto[]))]
[JsonSerializable(typeof(WappalyzerRootDto))]
[JsonSerializable(typeof(FingerprintScanResult))]
[JsonSerializable(typeof(FingerprintScanResult[]))]
[JsonSerializable(typeof(List<FingerprintScanResult>))]
internal partial class FingerprintJsonContext : JsonSerializerContext;

internal sealed class EholeRootDto
{
    [JsonPropertyName("fingerprint")]
    public List<EholeRuleDto>? Fingerprint { get; set; }
}

internal sealed class EholeRuleDto
{
    [JsonPropertyName("cms")]
    public string? Cms { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("keyword")]
    public string[]? Keyword { get; set; }
}

internal sealed class GobyEntryDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("logic")]
    public string? Logic { get; set; }

    [JsonPropertyName("rule")]
    public List<GobyRuleDto>? Rule { get; set; }
}

internal sealed class GobyRuleDto
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("feature")]
    public string? Feature { get; set; }

    [JsonPropertyName("is_equal")]
    public bool IsEqual { get; set; } = true;
}

internal sealed class FingersEntryDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; set; }

    [JsonPropertyName("product")]
    public string? Product { get; set; }

    [JsonPropertyName("send_data")]
    public string? SendData { get; set; }

    [JsonPropertyName("rule")]
    public List<FingersRuleDto>? Rule { get; set; }
}

internal sealed class FingersRuleDto
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("level")]
    public int? Level { get; set; }

    [JsonPropertyName("send_data")]
    public string? SendData { get; set; }

    [JsonPropertyName("regexps")]
    public FingersRegexpsDto? Regexps { get; set; }

    [JsonPropertyName("favicon")]
    public FingersFaviconDto? Favicon { get; set; }
}

internal sealed class FingersRegexpsDto
{
    [JsonPropertyName("body")]
    public string[]? Body { get; set; }

    [JsonPropertyName("header")]
    public string[]? Header { get; set; }

    [JsonPropertyName("regexp")]
    public string[]? Regexp { get; set; }

    [JsonPropertyName("version")]
    public string[]? Version { get; set; }

    [JsonPropertyName("md5")]
    public string[]? Md5 { get; set; }

    [JsonPropertyName("mmh3")]
    public string[]? Mmh3 { get; set; }

    [JsonPropertyName("cert")]
    public string[]? Cert { get; set; }
}

internal sealed class FingersFaviconDto
{
    [JsonPropertyName("mmh3")]
    public string[]? Mmh3 { get; set; }

    [JsonPropertyName("md5")]
    public string[]? Md5 { get; set; }
}

internal sealed class FingerprintHubEntryDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("info")]
    public FingerprintHubInfoDto? Info { get; set; }

    [JsonPropertyName("http")]
    public List<FingerprintHubHttpDto>? Http { get; set; }
}

internal sealed class FingerprintHubInfoDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class FingerprintHubHttpDto
{
    [JsonPropertyName("matchers")]
    public List<FingerprintHubMatcherDto>? Matchers { get; set; }
}

internal sealed class FingerprintHubMatcherDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("words")]
    public string[]? Words { get; set; }

    [JsonPropertyName("case-insensitive")]
    public bool CaseInsensitive { get; set; }
}

internal sealed class WappalyzerRootDto
{
    [JsonPropertyName("apps")]
    public Dictionary<string, WappalyzerAppDto>? Apps { get; set; }
}

internal sealed class WappalyzerAppDto
{
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("html")]
    public string[]? Html { get; set; }

    [JsonPropertyName("scriptSrc")]
    public string[]? ScriptSrc { get; set; }

    [JsonPropertyName("cookies")]
    public Dictionary<string, string>? Cookies { get; set; }
}
