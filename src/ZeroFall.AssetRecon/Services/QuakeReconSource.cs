using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ZeroFall.AssetRecon.Models;
using ZeroFall.AssetRecon.Serialization;
using ZeroFall.Platform.Services;

namespace ZeroFall.AssetRecon.Services;

public class QuakeReconSource : IReconSource
{
    private readonly HttpClient _httpClient;
    private readonly AssetReconSettings _config;

    public QuakeReconSource(AssetReconSettings config, IOutboundHttpClientFactory httpClientFactory)
    {
        _config = config;
        _httpClient = httpClientFactory.CreateClient("asset-recon-quake", TimeSpan.FromSeconds(60));
    }

    public string Name => "quake";
    public string DisplayName => "360 Quake";
    public int MaxPageSize => 100;

    public bool IsConfigured =>
        _config.QuakeEnabled && !string.IsNullOrWhiteSpace(_config.QuakeKey);

    public string TranslateQuery(UnifiedQuery query) => query.ToQuake();

    public async Task<ReconResult> QueryAsync(string target, int page = 1, int pageSize = 100)
    {
        var size = Math.Min(pageSize, MaxPageSize);
        var start = (page - 1) * size;
        var result = new ReconResult { SourceName = DisplayName, Query = target, Page = page, PageSize = size };

        try
        {
            var key = _config.QuakeKey;
            var baseUrl = _config.QuakeBaseUrl.TrimEnd('/');

            var url = $"{baseUrl}/api/v3/search/quake_service";
            var jsonBody = $"{{\"query\":\"{JsonEncode(target)}\",\"start\":{start},\"size\":{size}}}";
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;
            request.Headers.Add("X-QuakeToken", key);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var apiResult = JsonSerializer.Deserialize(json, AssetReconJsonContext.Default.QuakeApiResponse);

            if (apiResult == null)
            {
                result.ErrorMessage = "解析响应失败";
                return result;
            }

            if (apiResult.Code != 0)
            {
                result.ErrorMessage = $"API错误: {apiResult.Message}";
                return result;
            }

            result.Success = true;
            result.TotalCount = apiResult.Meta?.Pagination?.Total ?? 0;

            if (apiResult.Data != null)
            {
                foreach (var item in apiResult.Data)
                {
                    var service = item.Service;
                    var http = service?.Http;
                    var location = item.Location;

                    var link = http?.Host ?? string.Empty;
                    if (string.IsNullOrEmpty(link) && !string.IsNullOrEmpty(item.Ip) && item.Port != null)
                        link = $"{item.Transport ?? "http"}://{item.Ip}:{item.Port}";

                    var product = string.Empty;
                    var version = string.Empty;
                    if (item.Products != null && item.Products.Count > 0)
                    {
                        var main = item.Products[0];
                        product = main.ProductNameEn ?? main.ProductNameCn ?? string.Empty;
                        version = main.Version ?? string.Empty;
                    }

                    result.Rows.Add(new UnifiedAssetRow
                    {
                        Ip = item.Ip ?? string.Empty,
                        Port = item.Port?.ToString() ?? string.Empty,
                        Domain = item.Domain ?? item.Hostname ?? string.Empty,
                        Protocol = item.Transport ?? string.Empty,
                        Url = http?.Host ?? string.Empty,
                        Link = link,
                        Product = product,
                        Version = version,
                        Org = item.Org ?? string.Empty,
                        AsNumber = item.Asn?.ToString() ?? string.Empty,
                        Isp = location?.Isp ?? string.Empty,
                        Country = location?.CountryCn ?? string.Empty,
                        Province = location?.ProvinceCn ?? string.Empty,
                        City = location?.CityCn ?? string.Empty,
                        Title = http?.Title ?? string.Empty,
                        StatusCode = http?.StatusCodeElement.ToString() ?? string.Empty,
                        Server = http?.Server ?? string.Empty,
                        Banner = http?.Body ?? string.Empty,
                        Header = service?.Response ?? string.Empty,
                        Cert = service?.Cert ?? string.Empty,
                        ServiceName = service?.Name ?? string.Empty,
                        Scene = location?.SceneCn ?? string.Empty,
                        District = location?.DistrictCn ?? string.Empty,
                        XPoweredBy = http?.XPoweredBy ?? string.Empty,
                        QuakeId = item.Id ?? string.Empty
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

    private static string JsonEncode(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
