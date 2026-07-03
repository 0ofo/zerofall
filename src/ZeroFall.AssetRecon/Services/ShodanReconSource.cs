using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ZeroFall.AssetRecon.Models;
using ZeroFall.AssetRecon.Serialization;
using ZeroFall.Platform.Services;

namespace ZeroFall.AssetRecon.Services;

public class ShodanReconSource : IReconSource
{
    private readonly HttpClient _httpClient;
    private readonly AssetReconSettings _config;

    public ShodanReconSource(AssetReconSettings config, IOutboundHttpClientFactory httpClientFactory)
    {
        _config = config;
        _httpClient = httpClientFactory.CreateClient("asset-recon-shodan", TimeSpan.FromSeconds(60));
    }

    public string Name => "shodan";
    public string DisplayName => "Shodan";
    public int MaxPageSize => 100;

    public bool IsConfigured =>
        _config.ShodanEnabled && !string.IsNullOrWhiteSpace(_config.ShodanKey);

    public string TranslateQuery(UnifiedQuery query) => query.ToShodan();

    public async Task<ReconResult> QueryAsync(string target, int page = 1, int pageSize = 100)
    {
        var result = new ReconResult { SourceName = DisplayName, Query = target, Page = page, PageSize = pageSize };

        try
        {
            var key = _config.ShodanKey;
            var baseUrl = _config.ShodanBaseUrl.TrimEnd('/');

            var url = $"{baseUrl}/shodan/host/search?key={Uri.EscapeDataString(key)}&query={Uri.EscapeDataString(target)}&page={page}";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var apiResult = JsonSerializer.Deserialize(json, AssetReconJsonContext.Default.ShodanApiResponse);

            if (apiResult == null)
            {
                result.ErrorMessage = "解析响应失败";
                return result;
            }

            if (!string.IsNullOrEmpty(apiResult.Error))
            {
                result.ErrorMessage = $"API错误: {apiResult.Error}";
                return result;
            }

            result.Success = true;
            result.TotalCount = apiResult.Total ?? 0;

            if (apiResult.Matches != null)
            {
                foreach (var item in apiResult.Matches)
                {
                    var link = string.Empty;
                    if (!string.IsNullOrEmpty(item.IpStr))
                        link = $"{item.Transport ?? "http"}://{item.IpStr}:{item.Port}";

                    result.Rows.Add(new UnifiedAssetRow
                    {
                        Ip = item.IpStr ?? string.Empty,
                        Port = item.Port.ToString(),
                        Protocol = item.Transport ?? string.Empty,
                        Link = link,
                        Org = item.Org ?? string.Empty,
                        Isp = item.Isp ?? string.Empty,
                        Os = item.Os ?? string.Empty,
                        Domain = item.Hostnames != null ? string.Join(", ", item.Hostnames) : string.Empty,
                        Country = item.Location?.CountryName ?? string.Empty,
                        City = item.Location?.City ?? string.Empty,
                        Title = item.Title ?? string.Empty,
                        Product = item.Product ?? string.Empty,
                        Version = item.Version ?? string.Empty,
                        Banner = item.Data ?? string.Empty,
                        AsNumber = item.Asn ?? string.Empty,
                        Header = item.Http ?? string.Empty,
                        Vuln = item.Vulns != null ? string.Join(", ", item.Vulns) : string.Empty
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
