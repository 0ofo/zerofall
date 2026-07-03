using System;
using System.Collections.Generic;

namespace ZeroFall.Browser.Services;

public enum HttpDiffLineKind
{
    Same,
    Different,
    LeftOnly,
    RightOnly
}

public sealed record HttpDiffLine(string Left, string Right, HttpDiffLineKind Kind);

public static class HttpLineDiff
{
    public static IReadOnlyList<HttpDiffLine> Compare(string? left, string? right)
    {
        var leftLines = SplitLines(left);
        var rightLines = SplitLines(right);
        var max = Math.Max(leftLines.Count, rightLines.Count);
        var result = new List<HttpDiffLine>(max);

        for (var i = 0; i < max; i++)
        {
            var hasLeft = i < leftLines.Count;
            var hasRight = i < rightLines.Count;
            var leftText = hasLeft ? leftLines[i] : string.Empty;
            var rightText = hasRight ? rightLines[i] : string.Empty;

            HttpDiffLineKind kind;
            if (hasLeft && hasRight)
                kind = string.Equals(leftText, rightText, StringComparison.Ordinal) ? HttpDiffLineKind.Same : HttpDiffLineKind.Different;
            else if (hasLeft)
                kind = HttpDiffLineKind.LeftOnly;
            else
                kind = HttpDiffLineKind.RightOnly;

            result.Add(new HttpDiffLine(leftText, rightText, kind));
        }

        return result;
    }

    private static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        return [.. text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None)];
    }
}
