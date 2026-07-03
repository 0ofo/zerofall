namespace ZeroFall.Fingerprint.Core;

public readonly struct RawHttpSplitResult
{
    public ReadOnlyMemory<byte> Header { get; init; }
    public ReadOnlyMemory<byte> Body { get; init; }
    public bool HasHttpHeader { get; init; }
}

public static class RawHttpSplitter
{
    public static RawHttpSplitResult Split(ReadOnlySpan<byte> rawContent)
    {
        var separator = FindHeaderBodySeparator(rawContent);
        if (separator < 0)
            return new RawHttpSplitResult { Header = default, Body = rawContent.ToArray(), HasHttpHeader = false };

        var headerLength = separator + (rawContent[separator] == '\r' ? 4 : 2);
        return new RawHttpSplitResult
        {
            Header = rawContent[..headerLength].ToArray(),
            Body = rawContent[headerLength..].ToArray(),
            HasHttpHeader = true
        };
    }

    public static RawHttpSplitResult SplitLower(ReadOnlySpan<byte> rawContent)
    {
        var lower = rawContent.ToArray();
        for (var i = 0; i < lower.Length; i++)
        {
            var b = lower[i];
            if (b is >= (byte)'A' and <= (byte)'Z')
                lower[i] = (byte)(b + 32);
        }

        return Split(lower);
    }

    private static int FindHeaderBodySeparator(ReadOnlySpan<byte> rawContent)
    {
        var crlf = rawContent.IndexOf("\r\n\r\n"u8);
        if (crlf >= 0)
            return crlf;
        return rawContent.IndexOf("\n\n"u8);
    }
}
