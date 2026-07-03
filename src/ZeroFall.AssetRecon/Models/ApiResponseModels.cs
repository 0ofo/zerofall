using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ZeroFall.AssetRecon.Models;

public class FofaApiResponse
{
    [JsonPropertyName("error")] public bool Error { get; set; }
    [JsonPropertyName("errmsg")] public string Errmsg { get; set; } = string.Empty;
    [JsonPropertyName("size")] public int Size { get; set; }
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("results")] public List<List<string>>? Results { get; set; }
}

public class HunterApiResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("data")] public HunterData? Data { get; set; }
}

public class HunterData
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("time")] public int Time { get; set; }
    [JsonPropertyName("arr")] public List<HunterItem>? Arr { get; set; }
    [JsonPropertyName("account_type")] public string? AccountType { get; set; }
    [JsonPropertyName("consume_quota")] public string? ConsumeQuota { get; set; }
    [JsonPropertyName("rest_quota")] public string? RestQuota { get; set; }
}

public class HunterItem
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("web_title")] public string? WebTitle { get; set; }
    [JsonPropertyName("domain")] public string? Domain { get; set; }
    [JsonPropertyName("number")] public string? Number { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("province")] public string? Province { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("status_code")] public int? StatusCode { get; set; }
    [JsonPropertyName("company")] public string? Company { get; set; }
    [JsonPropertyName("is_risk_protocol")] public string? IsRiskProtocol { get; set; }
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }
    [JsonPropertyName("base_protocol")] public string? BaseProtocol { get; set; }
    [JsonPropertyName("os")] public string? Os { get; set; }
    [JsonPropertyName("header")] public string? Header { get; set; }
    [JsonPropertyName("header_server")] public string? HeaderServer { get; set; }
    [JsonPropertyName("banner")] public string? Banner { get; set; }
    [JsonPropertyName("isp")] public string? Isp { get; set; }
    [JsonPropertyName("as_org")] public string? AsOrg { get; set; }
    [JsonPropertyName("ssl_certificate")] public string? SslCertificate { get; set; }
    [JsonPropertyName("component")] public List<HunterComponent>? Component { get; set; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }
    [JsonPropertyName("ip_tag")] public string? IpTag { get; set; }
    [JsonPropertyName("icp_exception")] public List<string>? IcpException { get; set; }
    [JsonPropertyName("is_web")] public string? IsWeb { get; set; }
    [JsonPropertyName("cert_sha256")] public string? CertSha256 { get; set; }
    [JsonPropertyName("asset_tag")] public string? AssetTag { get; set; }
}

public class HunterComponent
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

public class QuakeApiResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("data")] public List<QuakeItem>? Data { get; set; }
    [JsonPropertyName("meta")] public QuakeMeta? Meta { get; set; }
}

public class QuakeMeta
{
    [JsonPropertyName("pagination")] public QuakePagination? Pagination { get; set; }
}

public class QuakePagination
{
    [JsonPropertyName("total")] public int Total { get; set; }
}

public class QuakeItem
{
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonPropertyName("domain")] public string? Domain { get; set; }
    [JsonPropertyName("transport")] public string? Transport { get; set; }
    [JsonPropertyName("asn")] public int? Asn { get; set; }
    [JsonPropertyName("org")] public string? Org { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("location")] public QuakeLocation? Location { get; set; }
    [JsonPropertyName("service")] public QuakeService? Service { get; set; }
    [JsonPropertyName("products")] public List<QuakeProduct>? Products { get; set; }
}

public class QuakeLocation
{
    [JsonPropertyName("country_cn")] public string? CountryCn { get; set; }
    [JsonPropertyName("province_cn")] public string? ProvinceCn { get; set; }
    [JsonPropertyName("city_cn")] public string? CityCn { get; set; }
    [JsonPropertyName("district_cn")] public string? DistrictCn { get; set; }
    [JsonPropertyName("isp")] public string? Isp { get; set; }
    [JsonPropertyName("asname")] public string? AsName { get; set; }
    [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
    [JsonPropertyName("scene_cn")] public string? SceneCn { get; set; }
}

public class QuakeService
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("response")] public string? Response { get; set; }
    [JsonPropertyName("cert")] public string? Cert { get; set; }
    [JsonPropertyName("http")] public QuakeHttp? Http { get; set; }
}

public class QuakeHttp
{
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("server")] public string? Server { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("status_code")] public System.Text.Json.JsonElement StatusCodeElement { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("x_powered_by")] public string? XPoweredBy { get; set; }
}

public class QuakeProduct
{
    [JsonPropertyName("product_name_cn")] public string? ProductNameCn { get; set; }
    [JsonPropertyName("product_name_en")] public string? ProductNameEn { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("product_vendor")] public string? ProductVendor { get; set; }
}

public class ShodanApiResponse
{
    [JsonPropertyName("total")] public int? Total { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("matches")] public List<ShodanMatch>? Matches { get; set; }
}

public class ShodanMatch
{
    [JsonPropertyName("ip_str")] public string? IpStr { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("transport")] public string? Transport { get; set; }
    [JsonPropertyName("org")] public string? Org { get; set; }
    [JsonPropertyName("isp")] public string? Isp { get; set; }
    [JsonPropertyName("os")] public string? Os { get; set; }
    [JsonPropertyName("hostnames")] public List<string>? Hostnames { get; set; }
    [JsonPropertyName("location")] public ShodanLocation? Location { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("product")] public string? Product { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("asn")] public string? Asn { get; set; }
    [JsonPropertyName("http")] public string? Http { get; set; }
    [JsonPropertyName("vulns")] public List<string>? Vulns { get; set; }
}

public class ShodanLocation
{
    [JsonPropertyName("country_name")] public string? CountryName { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
}
