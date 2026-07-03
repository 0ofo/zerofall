using System.Text;

namespace ZeroFall.Fingerprint.Hashing;

/// <summary>Shodan / EHole / ARL 使用的 favicon MMH3（base64 76 列换行后 murmur3 32）。</summary>
public static class FaviconMmh3Hasher
{
    public static string Compute(ReadOnlySpan<byte> data)
    {
        var b64 = Convert.ToBase64String(data);
        var sb = new StringBuilder(b64.Length + b64.Length / 76 + 4);
        for (var i = 0; i < b64.Length; i += 76)
        {
            var len = Math.Min(76, b64.Length - i);
            sb.Append(b64.AsSpan(i, len));
            sb.Append('\n');
        }

        var hash = MurmurHash3.Hash32(Encoding.UTF8.GetBytes(sb.ToString()));
        return unchecked((int)hash).ToString();
    }
}
