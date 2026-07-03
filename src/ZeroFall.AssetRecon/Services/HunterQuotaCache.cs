using System;
using ZeroFall.AssetRecon.Models;
namespace ZeroFall.AssetRecon.Services;

/// <summary>最近一次 Hunter 搜索响应中的积分文案，避免额度刷新时重复发起探测查询。</summary>
internal static class HunterQuotaCache
{
    private static readonly object Gate = new();
    private static string? _line;
    private static DateTime _utc;

    public static bool TryGetRecentLine(TimeSpan maxAge, out string line)
    {
        lock (Gate)
        {
            if (_line != null && DateTime.UtcNow - _utc < maxAge)
            {
                line = _line;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    public static bool TryGetRecentNumber(TimeSpan maxAge, out string number)
    {
        if (!TryGetRecentLine(maxAge, out var line))
        {
            number = string.Empty;
            return false;
        }

        number = AssetReconQuotaClient.TryParseHunterRemainingNumber(line) ?? string.Empty;
        return number.Length > 0;
    }

    public static void UpdateFrom(HunterData? d)
    {
        if (d == null)
            return;
        var rem = AssetReconQuotaClient.TryParseHunterRemainingNumber(d.RestQuota);
        if (rem == null)
            return;
        var line = rem;
        lock (Gate)
        {
            _line = line;
            _utc = DateTime.UtcNow;
        }
    }
}
