using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeroFall.Platform.Services;

public sealed record AssetReconFieldDefinition(
    string ColumnName,
    string Header,
    bool IsDefaultDisplay,
    Func<UnifiedAssetRow, string> Read);

public static class AssetReconFieldCatalog
{
    private static readonly string[] DefaultColumnOrder =
    [
        "ip", "port", "protocol", "country", "province", "city", "url",
        "domain", "os", "server", "icp", "title", "link", "status_code"
    ];

    public static readonly IReadOnlyList<AssetReconFieldDefinition> All =
    [
        Field("url", "URL", true, r => r.Url),
        Field("ip", "IP", true, r => r.Ip),
        Field("port", "Port", true, r => r.Port),
        Field("protocol", "Protocol", true, r => r.Protocol),
        Field("title", "Title", true, r => r.Title),
        Field("domain", "Domain", true, r => r.Domain),
        Field("country", "Country", true, r => r.Country),
        Field("country_code", "CountryCode", false, r => r.CountryCode),
        Field("province", "Province", true, r => r.Province),
        Field("city", "City", true, r => r.City),
        Field("org", "Org", false, r => r.Org),
        Field("isp", "ISP", false, r => r.Isp),
        Field("os", "OS", true, r => r.Os),
        Field("server", "Server", true, r => r.Server),
        Field("banner", "Banner", false, r => r.Banner),
        Field("status_code", "StatusCode", true, r => r.StatusCode),
        Field("product", "Product", false, r => r.Product),
        Field("product_category", "ProductCategory", false, r => r.ProductCategory),
        Field("version", "Version", false, r => r.Version),
        Field("cert_issuer", "CertIssuer", false, r => r.CertIssuer),
        Field("cert_issuer_org", "CertIssuerOrg", false, r => r.CertIssuerOrg),
        Field("cert_subject", "CertSubject", false, r => r.CertSubject),
        Field("cert_subject_org", "CertSubjectOrg", false, r => r.CertSubjectOrg),
        Field("cert_sn", "CertSn", false, r => r.CertSn),
        Field("cert_not_before", "CertNotBefore", false, r => r.CertNotBefore),
        Field("cert_not_after", "CertNotAfter", false, r => r.CertNotAfter),
        Field("cert_domain", "CertDomain", false, r => r.CertDomain),
        Field("icp", "ICP", true, r => r.Icp),
        Field("as_number", "ASNumber", false, r => r.AsNumber),
        Field("header", "Header", false, r => r.Header),
        Field("header_hash", "HeaderHash", false, r => r.HeaderHash),
        Field("banner_hash", "BannerHash", false, r => r.BannerHash),
        Field("banner_fid", "BannerFid", false, r => r.BannerFid),
        Field("jarm", "JARM", false, r => r.Jarm),
        Field("tls_ja3s", "TlsJa3s", false, r => r.TlsJa3s),
        Field("tls_version", "TlsVersion", false, r => r.TlsVersion),
        Field("cname", "CNAME", false, r => r.Cname),
        Field("vuln", "Vuln", false, r => r.Vuln),
        Field("base_protocol", "BaseProtocol", false, r => r.BaseProtocol),
        Field("updated_at", "UpdatedAt", false, r => r.UpdatedAt),
        Field("link", "Link", true, r => r.Link),
        Field("cert", "Cert", false, r => r.Cert),
        Field("longitude", "Longitude", false, r => r.Longitude),
        Field("latitude", "Latitude", false, r => r.Latitude),
        Field("ip_tag", "IpTag", false, r => r.IpTag),
        Field("is_risk_protocol", "IsRiskProtocol", false, r => r.IsRiskProtocol),
        Field("icp_exception", "IcpException", false, r => r.IcpException),
        Field("is_web", "IsWeb", false, r => r.IsWeb),
        Field("cert_sha256", "CertSha256", false, r => r.CertSha256),
        Field("asset_tag", "AssetTag", false, r => r.AssetTag),
        Field("company", "Company", false, r => r.Company),
        Field("service_name", "ServiceName", false, r => r.ServiceName),
        Field("scene", "Scene", false, r => r.Scene),
        Field("district", "District", false, r => r.District),
        Field("x_powered_by", "XPoweredBy", false, r => r.XPoweredBy),
        Field("quake_id", "QuakeId", false, r => r.QuakeId),
        Field("sort_order", "SortOrder", false, r => r.SortOrder.ToString())
    ];

    public static string[] DefaultColumnNames => DefaultFields()
        .Select(definition => definition.ColumnName)
        .ToArray();

    public static string[] DefaultHeaders => DefaultFields()
        .Select(definition => definition.Header)
        .ToArray();

    public static string[] ReadDefaultValues(UnifiedAssetRow row) => DefaultFields()
        .Select(definition => definition.Read(row))
        .ToArray();

    private static IEnumerable<AssetReconFieldDefinition> DefaultFields() => All
        .Where(definition => definition.IsDefaultDisplay)
        .OrderBy(definition => Array.IndexOf(DefaultColumnOrder, definition.ColumnName));

    private static AssetReconFieldDefinition Field(
        string columnName,
        string header,
        bool isDefaultDisplay,
        Func<UnifiedAssetRow, string> read) =>
        new(columnName, header, isDefaultDisplay, read);
}
