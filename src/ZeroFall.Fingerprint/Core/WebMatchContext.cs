using System.Text;

namespace ZeroFall.Fingerprint.Core;

public sealed class WebMatchContext
{
    public required byte[] RawContent { get; init; }
    public required byte[] LowerContent { get; init; }
    public required byte[] Header { get; init; }
    public required byte[] Body { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Cert { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string[]> HeadersMap { get; init; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    public static WebMatchContext FromRaw(byte[] rawContent, string cert = "")
    {
        var lower = rawContent.ToArray();
        for (var i = 0; i < lower.Length; i++)
        {
            var b = lower[i];
            if (b is >= (byte)'A' and <= (byte)'Z')
                lower[i] = (byte)(b + 32);
        }

        var split = RawHttpSplitter.Split(lower);
        var header = split.Header.IsEmpty ? [] : split.Header.ToArray();
        var body = split.Body.IsEmpty ? lower : split.Body.ToArray();
        var bodyText = Encoding.UTF8.GetString(body);
        return new WebMatchContext
        {
            RawContent = rawContent,
            LowerContent = lower,
            Header = header,
            Body = body,
            Title = Text.ResponseTextDecoder.ExtractTitle(bodyText),
            Cert = cert,
            HeadersMap = ParseHeaders(header)
        };
    }

    public string HeaderText => Encoding.UTF8.GetString(Header);
    public string BodyText => Encoding.UTF8.GetString(Body);
    public string LowerText => Encoding.UTF8.GetString(LowerContent);

    private static Dictionary<string, string[]> ParseHeaders(ReadOnlySpan<byte> headerBytes)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (headerBytes.IsEmpty)
            return map;

        var text = Encoding.UTF8.GetString(headerBytes);
        var start = text.IndexOf('\n');
        if (start < 0)
            return map;

        foreach (var line in text[(start + 1)..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = trimmed[..colon].Trim().ToLowerInvariant();
            var value = trimmed[(colon + 1)..].Trim();
            if (!map.TryGetValue(key, out var values))
            {
                map[key] = [value];
                continue;
            }

            map[key] = values.Append(value).ToArray();
        }

        return map;
    }
}
