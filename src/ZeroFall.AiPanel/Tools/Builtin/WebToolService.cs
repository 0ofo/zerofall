using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using ZeroFall.AiPanel.Services;
using ZeroFall.Base.AiTools;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Tools.Builtin;

public sealed class WebToolService
{
    private const int DefaultResultCount = 8;
    private const int MaxResultCount = 20;

    private readonly ISettingsService _settingsService;
    private readonly IOutboundHttpClientFactory _httpClientFactory;

    public WebToolService(
        ISettingsService settingsService,
        IOutboundHttpClientFactory httpClientFactory)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
    }

    [AiTool("web_search", "使用 Bing 搜索互联网。返回标题、链接与摘要的 Markdown 列表。可在 AI 设置中配置 Bing Web Search API 密钥以获得更稳定结果；未配置时尝试 Bing RSS。")]
    public async Task<string> WebSearchAsync(
        [ToolParam("搜索关键词或自然语言查询")] string query,
        [ToolParam("返回条数，默认 8，最大 20", Required = false)] int count = DefaultResultCount)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ToolResultJson.Error("query 不能为空");

        count = Math.Clamp(count, 1, MaxResultCount);
        var settings = _settingsService.Load().Ai;
        using var client = _httpClientFactory.CreateClient("web-search", TimeSpan.FromSeconds(30));

        IReadOnlyList<WebSearchHit> hits;
        var apiKey = settings.BingSearchApiKey?.Trim();
        if (!string.IsNullOrEmpty(apiKey))
        {
            hits = await SearchBingApiAsync(client, query.Trim(), count, apiKey);
            if (hits.Count > 0)
                return FormatSearchResultsJson(query.Trim(), hits);
        }

        hits = await SearchBingRssAsync(client, query.Trim(), count);
        if (hits.Count > 0)
            return FormatSearchResultsJson(query.Trim(), hits);

        hits = ParseBingHtml(await FetchBingHtmlAsync(client, query.Trim()), count);
        if (hits.Count > 0)
            return FormatSearchResultsJson(query.Trim(), hits);

        return ToolResultJson.Error(string.IsNullOrEmpty(apiKey)
            ? "未找到搜索结果。可在 AI 设置中填写 Bing Web Search API 密钥，或检查网络/代理。"
            : "未找到搜索结果，请检查 Bing API 密钥或网络/代理。");
    }

    private static async Task<IReadOnlyList<WebSearchHit>> SearchBingApiAsync(
        HttpClient client, string query, int count, string apiKey)
    {
        var url = $"https://api.bing.microsoft.com/v7.0/search?q={Uri.EscapeDataString(query)}&count={count}&mkt=zh-CN&textFormat=Raw";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", apiKey);

        using var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return Array.Empty<WebSearchHit>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("webPages", out var webPages)
                || !webPages.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
                return Array.Empty<WebSearchHit>();

            var list = new List<WebSearchHit>();
            foreach (var item in value.EnumerateArray())
            {
                var title = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var link = item.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                var snippet = item.TryGetProperty("snippet", out var snEl) ? snEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(link))
                    continue;
                list.Add(new WebSearchHit(title ?? link, link, snippet ?? string.Empty));
                if (list.Count >= count)
                    break;
            }

            return list;
        }
        catch
        {
            return Array.Empty<WebSearchHit>();
        }
    }

    private static async Task<IReadOnlyList<WebSearchHit>> SearchBingRssAsync(HttpClient client, string query, int count)
    {
        var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&format=rss";
        try
        {
            var xml = await client.GetStringAsync(url);
            var doc = XDocument.Parse(xml);
            XNamespace dc = "http://purl.org/dc/elements/1.1/";

            var hits = new List<WebSearchHit>();
            foreach (var item in doc.Descendants("item"))
            {
                var title = item.Element("title")?.Value?.Trim();
                var link = item.Element("link")?.Value?.Trim();
                var desc = item.Element("description")?.Value?.Trim()
                           ?? item.Element(dc + "description")?.Value?.Trim()
                           ?? string.Empty;
                desc = StripTags(desc);
                if (string.IsNullOrWhiteSpace(link))
                    continue;
                hits.Add(new WebSearchHit(title ?? link, link, desc));
                if (hits.Count >= count)
                    break;
            }

            return hits;
        }
        catch
        {
            return Array.Empty<WebSearchHit>();
        }
    }

    private static async Task<string> FetchBingHtmlAsync(HttpClient client, string query)
    {
        var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&setlang=zh-Hans";
        return await client.GetStringAsync(url);
    }

    private static IReadOnlyList<WebSearchHit> ParseBingHtml(string html, int count)
    {
        if (string.IsNullOrWhiteSpace(html))
            return Array.Empty<WebSearchHit>();

        var hits = new List<WebSearchHit>();
        var blockPattern = new Regex(
            @"<li[^>]*class=""[^""]*b_algo[^""]*""[^>]*>(.*?)</li>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var linkPattern = new Regex(@"<a[^>]+href=""(?<url>https?://[^""]+)""[^>]*>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var snippetPattern = new Regex(@"<p[^>]*>(?<sn>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match block in blockPattern.Matches(html))
        {
            var segment = block.Groups[1].Value;
            var linkMatch = linkPattern.Match(segment);
            if (!linkMatch.Success)
                continue;

            var rawUrl = System.Net.WebUtility.HtmlDecode(linkMatch.Groups["url"].Value);
            if (rawUrl.Contains("bing.com/ck/a", StringComparison.OrdinalIgnoreCase))
                continue;

            var title = StripTags(linkMatch.Groups["title"].Value).Trim();
            var snippet = string.Empty;
            var snMatch = snippetPattern.Match(segment);
            if (snMatch.Success)
                snippet = StripTags(snMatch.Groups["sn"].Value).Trim();

            if (string.IsNullOrWhiteSpace(rawUrl))
                continue;

            hits.Add(new WebSearchHit(string.IsNullOrEmpty(title) ? rawUrl : title, rawUrl, snippet));
            if (hits.Count >= count)
                break;
        }

        return hits;
    }

    private static string FormatSearchResultsJson(string query, IReadOnlyList<WebSearchHit> hits)
    {
        var arr = new JsonArray();
        foreach (var h in hits)
        {
            arr.Add(new JsonObject
            {
                ["query"] = query,
                ["title"] = h.Title,
                ["url"] = h.Url,
                ["snippet"] = h.Snippet
            });
        }

        return ToolResultJson.Data(arr);
    }

    private static string StripTags(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;
        var text = Regex.Replace(html, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(text, @"\s+", " ")).Trim();
    }

    private sealed record WebSearchHit(string Title, string Url, string Snippet);
}
