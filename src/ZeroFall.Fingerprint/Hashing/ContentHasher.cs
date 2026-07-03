using System.Security.Cryptography;
using System.Text;

namespace ZeroFall.Fingerprint.Hashing;

public static class ContentHasher
{
    public static string Md5Hex(ReadOnlySpan<byte> data)
    {
        var hash = MD5.HashData(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Go encode.Mmh3Hash32 — murmur3 32-bit on raw bytes.</summary>
    public static string Mmh3Hash32(ReadOnlySpan<byte> data)
    {
        var hash = MurmurHash3.Hash32(data);
        return unchecked((int)hash).ToString();
    }
}

internal static class MurmurHash3
{
    public static uint Hash32(ReadOnlySpan<byte> data, uint seed = 0)
    {
        const uint c1 = 0xcc9e2d51;
        const uint c2 = 0x1b873593;
        var hash = seed;
        var nblocks = data.Length / 4;

        for (var i = 0; i < nblocks; i++)
        {
            var k = BitConverter.ToUInt32(data[(i * 4)..]);
            k *= c1;
            k = RotateLeft(k, 15);
            k *= c2;
            hash ^= k;
            hash = RotateLeft(hash, 13);
            hash = hash * 5 + 0xe6546b64;
        }

        var tail = nblocks * 4;
        var k1 = 0u;
        switch (data.Length & 3)
        {
            case 3:
                k1 ^= (uint)data[tail + 2] << 16;
                goto case 2;
            case 2:
                k1 ^= (uint)data[tail + 1] << 8;
                goto case 1;
            case 1:
                k1 ^= data[tail];
                k1 *= c1;
                k1 = RotateLeft(k1, 15);
                k1 *= c2;
                hash ^= k1;
                break;
        }

        hash ^= (uint)data.Length;
        return FMix32(hash);
    }

    private static uint RotateLeft(uint x, int r) => (x << r) | (x >> (32 - r));

    private static uint FMix32(uint h)
    {
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return h;
    }
}
