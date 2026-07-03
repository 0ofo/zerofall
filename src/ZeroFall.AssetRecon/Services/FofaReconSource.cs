using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ZeroFall.AssetRecon.Models;
using ZeroFall.AssetRecon.Serialization;
using ZeroFall.Platform.Services;

namespace ZeroFall.AssetRecon.Services;

public class FofaReconSource : IReconSource
{
    private readonly HttpClient _httpClient;
    private readonly AssetReconSettings _config;

    public FofaReconSource(AssetReconSettings config, IOutboundHttpClientFactory httpClientFactory)
    {
        _config = config;
        _httpClient = httpClientFactory.CreateClient("asset-recon-fofa", TimeSpan.FromSeconds(60));
    }

    public string Name => "fofa";
    public string DisplayName => "FOFA";
    public int MaxPageSize => 500;

    public bool IsConfigured =>
        _config.FofaEnabled
        && !string.IsNullOrWhiteSpace(_config.FofaEmail)
        && !string.IsNullOrWhiteSpace(_config.FofaKey);

    public string TranslateQuery(UnifiedQuery query) => query.ToFofa();

    public async Task<ReconResult> QueryAsync(string target, int page = 1, int pageSize = 100)
    {
        var result = new ReconResult { SourceName = DisplayName, Query = target, Page = page, PageSize = pageSize };

        try
        {
            var email = _config.FofaEmail;
            var key = _config.FofaKey;
            var baseUrl = _config.FofaBaseUrl.TrimEnd('/');
            var size = Math.Min(pageSize, MaxPageSize);

            // FOFA fields：不含 header/banner/cert（会压低单次最大条数）；不含 product.version/icon/fid 等不支持展示的列。
            // 列顺序必须与下方 F(,) 索引一致。
            const string fields =
                "ip,port,protocol,country,country_name,region,city,longitude,latitude,asn,org,host,domain,os,server,icp,title,jarm," +
                "base_protocol,link,cert.issuer.org,cert.issuer.cn,cert.subject.org,cert.subject.cn,tls.ja3s,tls.version," +
                "cert.sn,cert.not_before,cert.not_after,cert.domain,status_code," +
                "header_hash,banner_hash,banner_fid,cname,lastupdatetime,product,product_category";
            var url = $"{baseUrl}/api/v1/search/all?email={Uri.EscapeDataString(email)}&key={Uri.EscapeDataString(key)}&qbase64={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(target))}&page={page}&size={size}&fields={Uri.EscapeDataString(fields)}";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var apiResult = JsonSerializer.Deserialize(json, AssetReconJsonContext.Default.FofaApiResponse);

            if (apiResult == null)
            {
                result.ErrorMessage = "解析响应失败";
                return result;
            }

            if (apiResult.Error)
            {
                result.ErrorMessage = $"API错误: {apiResult.Errmsg}";
                return result;
            }

            result.Success = true;
            result.TotalCount = apiResult.Size;

            if (apiResult.Results != null)
            {
                foreach (var item in apiResult.Results)
                {
                    result.Rows.Add(new UnifiedAssetRow
                    {
                        Ip = F(item, 0),
                        Port = F(item, 1),
                        Protocol = F(item, 2),
                        CountryCode = F(item, 3),
                        Country = F(item, 4),
                        Province = F(item, 5),
                        City = F(item, 6),
                        Longitude = F(item, 7),
                        Latitude = F(item, 8),
                        AsNumber = F(item, 9),
                        Org = F(item, 10),
                        Url = F(item, 11),
                        Domain = F(item, 12),
                        Os = F(item, 13),
                        Server = F(item, 14),
                        Icp = F(item, 15),
                        Title = F(item, 16),
                        Jarm = F(item, 17),
                        BaseProtocol = F(item, 18),
                        Link = F(item, 19),
                        CertIssuerOrg = F(item, 20),
                        CertIssuer = F(item, 21),
                        CertSubjectOrg = F(item, 22),
                        CertSubject = F(item, 23),
                        TlsJa3s = F(item, 24),
                        TlsVersion = F(item, 25),
                        CertSn = F(item, 26),
                        CertNotBefore = F(item, 27),
                        CertNotAfter = F(item, 28),
                        CertDomain = F(item, 29),
                        StatusCode = F(item, 30),
                        HeaderHash = F(item, 31),
                        BannerHash = F(item, 32),
                        BannerFid = F(item, 33),
                        Cname = F(item, 34),
                        UpdatedAt = F(item, 35),
                        Product = F(item, 36),
                        ProductCategory = F(item, 37),
                        Header = string.Empty,
                        Banner = string.Empty,
                        Cert = string.Empty
                    });
                }
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"查询失败: {ex.Message}";
        }

        return result;
    }

    private static string F(List<string> fields, int index)
    {
        if (fields == null || index < 0 || index >= fields.Count) return string.Empty;
        return fields[index] ?? string.Empty;
    }
}
