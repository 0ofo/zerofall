using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ZeroFall.AssetRecon.Models;
using ZeroFall.AssetRecon.Serialization;
using ZeroFall.Platform.Models;

namespace ZeroFall.AssetRecon.Services;

/// <summary>
/// 从各情报源官方接口拉取额度/配额摘要（见 <c>doc/资产侦察api.md</c>）。
/// Hunter 无独立额度接口，通过一次最小搜索读取响应中的 <c>rest_quota</c>（可能产生积分消耗）。
/// </summary>
public sealed class AssetReconQuotaClient
{
    private readonly HttpClient _httpClient;

    public AssetReconQuotaClient(IOutboundHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("asset-recon-quota", TimeSpan.FromSeconds(25));
    }

    internal async Task<string> BuildFofaColumnAsync(AssetReconSettings c, bool include)
    {
        if (!include) return "—";
        if (c.FofaEnabled && !string.IsNullOrWhiteSpace(c.FofaEmail) && !string.IsNullOrWhiteSpace(c.FofaKey))
            return await FetchFofaQuotaLineAsync(c).ConfigureAwait(false);
        return "FOFA：已关闭或未配置";
    }

    internal async Task<string> BuildFofaCompactAsync(AssetReconSettings c, bool include)
    {
        if (!include)
            return "F —";
        if (!c.FofaEnabled || string.IsNullOrWhiteSpace(c.FofaEmail) || string.IsNullOrWhiteSpace(c.FofaKey))
            return "F —";

        try
        {
            var baseUrl = c.FofaBaseUrl.TrimEnd('/');
            var url =
                $"{baseUrl}/api/v1/info/my?email={Uri.EscapeDataString(c.FofaEmail.Trim())}&key={Uri.EscapeDataString(c.FofaKey.Trim())}";
            var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.True)
                return "F -1";

            if (TryGetLong(root, "fcoin", out var fcoin))
                return $"F {fcoin.ToString(CultureInfo.InvariantCulture)}";

            return "F -1";
        }
        catch
        {
            return "F -1";
        }
    }

    internal async Task<string> BuildHunterCompactAsync(AssetReconSettings c, bool include)
    {
        if (!include)
            return "H —";
        if (!c.HunterEnabled || string.IsNullOrWhiteSpace(c.HunterKey))
            return "H —";

        if (HunterQuotaCache.TryGetRecentNumber(TimeSpan.FromMinutes(3), out var cached))
            return $"H {cached}";

        try
        {
            var baseUrl = c.HunterBaseUrl.TrimEnd('/');
            var probe = "ip=\"127.0.0.1\"";
            var queryBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(probe));
            var url =
                $"{baseUrl}/openApi/search?api-key={Uri.EscapeDataString(c.HunterKey.Trim())}&search={Uri.EscapeDataString(queryBase64)}&page=1&page_size=1";
            var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            var apiResult = JsonSerializer.Deserialize(json, AssetReconJsonContext.Default.HunterApiResponse);
            if (apiResult == null || apiResult.Code != 200)
                return "H —";

            var rem = TryParseHunterRemainingNumber(apiResult.Data?.RestQuota);
            return rem != null ? $"H {rem}" : "H —";
        }
        catch
        {
            return "H —";
        }
    }

    internal async Task<string> BuildQuakeCompactAsync(AssetReconSettings c, bool include)
    {
        if (!include)
            return "Q —";
        if (!c.QuakeEnabled || string.IsNullOrWhiteSpace(c.QuakeKey))
            return "Q —";

        try
        {
            var baseUrl = c.QuakeBaseUrl.TrimEnd('/');
            var url = $"{baseUrl}/api/v3/user/info";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-QuakeToken", c.QuakeKey.Trim());
            using var resp = await _httpClient.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var trimmed = body.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                return "Q —";

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var codeEl) || codeEl.GetInt32() != 0)
                return "Q —";

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return "Q —";

            if (!TryGetLong(data, "credit", out var credit))
                return "Q —";

            return $"Q {credit.ToString(CultureInfo.InvariantCulture)}";
        }
        catch
        {
            return "Q —";
        }
    }

    internal async Task<string> BuildHunterColumnAsync(AssetReconSettings c, bool include)
    {
        if (!include) return "—";
        if (c.HunterEnabled && !string.IsNullOrWhiteSpace(c.HunterKey))
            return await FetchHunterQuotaLineAsync(c).ConfigureAwait(false);
        return "Hunter：已关闭或未配置";
    }

    internal async Task<string> BuildQuakeColumnAsync(AssetReconSettings c, bool include)
    {
        if (!include) return "—";
        if (c.QuakeEnabled && !string.IsNullOrWhiteSpace(c.QuakeKey))
            return await FetchQuakeQuotaLineAsync(c).ConfigureAwait(false);
        return "360 Quake：已关闭或未配置";
    }

    private async Task<string> FetchFofaQuotaLineAsync(AssetReconSettings c)
    {
        try
        {
            var baseUrl = c.FofaBaseUrl.TrimEnd('/');
            var url =
                $"{baseUrl}/api/v1/info/my?email={Uri.EscapeDataString(c.FofaEmail.Trim())}&key={Uri.EscapeDataString(c.FofaKey.Trim())}";
            var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.True)
            {
                var msg = root.TryGetProperty("errmsg", out var m) ? m.GetString() ?? "未知错误" : "未知错误";
                return $"FOFA：失败（{msg}）";
            }

            var hasF = TryGetLong(root, "fcoin", out var fcoin);
            var hasV = TryGetInt(root, "vip_level", out var vip);
            if (!hasF && !hasV)
                return "FOFA：已连接（无 F 币/VIP 等级字段）";
            var bits = new List<string>(2);
            if (hasF)
                bits.Add($"F币 {fcoin.ToString(CultureInfo.InvariantCulture)}");
            if (hasV)
                bits.Add($"VIP等级 {vip.ToString(CultureInfo.InvariantCulture)}");
            return $"FOFA：{string.Join("　", bits)}";
        }
        catch (Exception ex)
        {
            return $"FOFA：请求失败（{ex.Message}）";
        }
    }

    private async Task<string> FetchHunterQuotaLineAsync(AssetReconSettings c)
    {
        if (HunterQuotaCache.TryGetRecentNumber(TimeSpan.FromMinutes(3), out var cached))
            return $"Hunter：积分 {cached}";

        try
        {
            var baseUrl = c.HunterBaseUrl.TrimEnd('/');
            var probe = "ip=\"127.0.0.1\"";
            var queryBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(probe));
            var url =
                $"{baseUrl}/openApi/search?api-key={Uri.EscapeDataString(c.HunterKey.Trim())}&search={Uri.EscapeDataString(queryBase64)}&page=1&page_size=1";
            var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            var apiResult = JsonSerializer.Deserialize(json, AssetReconJsonContext.Default.HunterApiResponse);
            if (apiResult == null)
                return "Hunter：解析失败";
            if (apiResult.Code != 200)
                return $"Hunter：{apiResult.Message}";

            var d = apiResult.Data;
            if (d == null)
                return "Hunter：无 data";

            var rem = TryParseHunterRemainingNumber(d.RestQuota);
            return rem != null
                ? $"Hunter：积分 {rem}"
                : "Hunter：无积分字段";
        }
        catch (Exception ex)
        {
            return $"Hunter：请求失败（{ex.Message}）";
        }
    }

    private async Task<string> FetchQuakeQuotaLineAsync(AssetReconSettings c)
    {
        try
        {
            var baseUrl = c.QuakeBaseUrl.TrimEnd('/');
            var url = $"{baseUrl}/api/v3/user/info";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-QuakeToken", c.QuakeKey.Trim());
            using var resp = await _httpClient.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var trimmed = body.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                return "360 Quake：响应非 JSON（请检查 Key 与 BaseUrl）";

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var codeEl) || codeEl.GetInt32() != 0)
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "错误" : "错误";
                return $"360 Quake：{msg}";
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return "360 Quake：无 data";

            if (!TryGetLong(data, "credit", out var credit))
                return "360 Quake：无积分字段";
            return $"360 Quake：积分 {credit.ToString(CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex)
        {
            return $"360 Quake：请求失败（{ex.Message}）";
        }
    }

    private static bool TryGetLong(JsonElement obj, string name, out long value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el))
            return false;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(el.GetString(), out value),
            _ => false
        };
    }

    private static bool TryGetInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el))
            return false;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(el.GetString(), out value),
            _ => false
        };
    }

    /// <summary>从 Hunter 返回的「今日剩余积分：499」类文案中取出数字。</summary>
    internal static string? TryParseHunterRemainingNumber(string? restQuota)
    {
        if (string.IsNullOrWhiteSpace(restQuota))
            return null;
        var s = restQuota.Trim();
        var idx = s.LastIndexOf('：');
        if (idx < 0)
            idx = s.LastIndexOf(':');
        if (idx >= 0 && idx < s.Length - 1)
        {
            var tail = s[(idx + 1)..].Trim();
            if (long.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n.ToString(CultureInfo.InvariantCulture);
            var digits = new string(tail.Where(char.IsDigit).ToArray());
            return digits.Length > 0 ? digits : null;
        }

        var allDigits = new string(s.Where(char.IsDigit).ToArray());
        return allDigits.Length > 0 ? allDigits : null;
    }
}
