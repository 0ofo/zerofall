using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ZeroFall.AssetRecon.Models;
using ZeroFall.AssetRecon.Serialization;
using ZeroFall.Platform.Services;

namespace ZeroFall.AssetRecon.Services;

public class HunterReconSource : IReconSource
{
    private readonly HttpClient _httpClient;
    private readonly AssetReconSettings _config;

    public HunterReconSource(AssetReconSettings config, IOutboundHttpClientFactory httpClientFactory)
    {
        _config = config;
        _httpClient = httpClientFactory.CreateClient("asset-recon-hunter", TimeSpan.FromSeconds(60));
    }

    public string Name => "hunter";
    public string DisplayName => "Hunter";
    public int MaxPageSize => 100;

    public bool IsConfigured =>
        _config.HunterEnabled && !string.IsNullOrWhiteSpace(_config.HunterKey);

    public string TranslateQuery(UnifiedQuery query) => query.ToHunter();

    public async Task<ReconResult> QueryAsync(string target, int page = 1, int pageSize = 100)
    {
        var result = new ReconResult { SourceName = DisplayName, Query = target, Page = page, PageSize = pageSize };

        try
        {
            var key = _config.HunterKey;
            var baseUrl = _config.HunterBaseUrl.TrimEnd('/');
            var size = Math.Min(pageSize, MaxPageSize);

            var queryBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(target));
            var url = $"{baseUrl}/openApi/search?api-key={Uri.EscapeDataString(key)}&search={Uri.EscapeDataString(queryBase64)}&page={page}&page_size={size}";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var apiResult = JsonSerializer.Deserialize(json, AssetReconJsonContext.Default.HunterApiResponse);

            if (apiResult == null)
            {
                result.ErrorMessage = "解析响应失败";
                return result;
            }

            if (apiResult.Code != 200)
            {
                result.ErrorMessage = $"API错误: {apiResult.Message}";
                return result;
            }

            result.Success = true;
            result.TotalCount = apiResult.Data?.Total ?? 0;
            HunterQuotaCache.UpdateFrom(apiResult.Data);

            if (apiResult.Data?.Arr != null)
            {
                foreach (var item in apiResult.Data.Arr)
                {
                    var componentName = string.Empty;
                    var componentVersion = string.Empty;
                    if (item.Component != null && item.Component.Count > 0)
                    {
                        componentName = item.Component[0].Name ?? string.Empty;
                        componentVersion = item.Component[0].Version ?? string.Empty;
                    }

                    result.Rows.Add(new UnifiedAssetRow
                    {
                        Url = item.Url ?? string.Empty,
                        Link = item.Url ?? string.Empty,
                        Ip = item.Ip ?? string.Empty,
                        Port = item.Port?.ToString() ?? string.Empty,
                        Protocol = item.Protocol ?? string.Empty,
                        Title = item.WebTitle ?? string.Empty,
                        Domain = item.Domain ?? string.Empty,
                        Country = item.Country ?? string.Empty,
                        Province = item.Province ?? string.Empty,
                        City = item.City ?? string.Empty,
                        Org = item.AsOrg ?? string.Empty,
                        Company = item.Company ?? string.Empty,
                        Isp = item.Isp ?? string.Empty,
                        Os = item.Os ?? string.Empty,
                        Server = item.HeaderServer ?? string.Empty,
                        Banner = item.Banner ?? string.Empty,
                        StatusCode = item.StatusCode?.ToString() ?? string.Empty,
                        Product = componentName,
                        Version = componentVersion,
                        Cert = item.SslCertificate ?? string.Empty,
                        CertSha256 = item.CertSha256 ?? string.Empty,
                        Icp = item.Number ?? string.Empty,
                        Header = item.Header ?? string.Empty,
                        BaseProtocol = item.BaseProtocol ?? string.Empty,
                        UpdatedAt = item.UpdatedAt ?? string.Empty,
                        IpTag = item.IpTag ?? string.Empty,
                        IsRiskProtocol = item.IsRiskProtocol ?? string.Empty,
                        IcpException = item.IcpException != null
                            ? string.Join("; ", item.IcpException)
                            : string.Empty,
                        IsWeb = item.IsWeb ?? string.Empty,
                        AssetTag = item.AssetTag ?? string.Empty
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
}
