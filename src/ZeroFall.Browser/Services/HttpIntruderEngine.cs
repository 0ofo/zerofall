using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeroFall.Browser.Services;

public enum HttpIntruderAttackType
{
    Sniper,
    BatteringRam,
    Pitchfork
}

public readonly record struct HttpIntruderMarker(int Start, int End, string Seed);

public readonly record struct HttpIntruderIteration(string Label, string RequestText);

public static class HttpIntruderEngine
{
    public const char MarkerChar = '§';

    public static IReadOnlyList<HttpIntruderMarker> FindMarkers(string template)
    {
        var markers = new List<HttpIntruderMarker>();
        var index = 0;
        while (index < template.Length)
        {
            var start = template.IndexOf(MarkerChar, index);
            if (start < 0)
                break;
            var end = template.IndexOf(MarkerChar, start + 1);
            if (end < 0)
                break;

            var seed = template.Substring(start + 1, end - start - 1);
            markers.Add(new HttpIntruderMarker(start, end + 1, seed));
            index = end + 1;
        }

        return markers;
    }

    public static IEnumerable<HttpIntruderIteration> GenerateIterations(
        string template,
        IReadOnlyList<string> payloads,
        HttpIntruderAttackType attackType)
    {
        if (payloads.Count == 0)
            yield break;

        var markers = FindMarkers(template);
        if (markers.Count == 0)
        {
            yield return new HttpIntruderIteration(payloads[0], template);
            yield break;
        }

        switch (attackType)
        {
            case HttpIntruderAttackType.Sniper:
                for (var markerIndex = 0; markerIndex < markers.Count; markerIndex++)
                {
                    foreach (var payload in payloads)
                    {
                        var values = new string[markers.Count];
                        for (var i = 0; i < markers.Count; i++)
                            values[i] = i == markerIndex ? payload : markers[i].Seed;
                        var request = ApplyValues(template, markers, values);
                        yield return new HttpIntruderIteration(
                            $"P{markerIndex + 1}:{TruncateLabel(payload)}",
                            request);
                    }
                }

                break;

            case HttpIntruderAttackType.BatteringRam:
                foreach (var payload in payloads)
                {
                    var values = Enumerable.Repeat(payload, markers.Count).ToArray();
                    var request = ApplyValues(template, markers, values);
                    yield return new HttpIntruderIteration(TruncateLabel(payload), request);
                }

                break;

            case HttpIntruderAttackType.Pitchfork:
                foreach (var payload in payloads)
                {
                    var values = new string[markers.Count];
                    for (var i = 0; i < markers.Count; i++)
                        values[i] = payload;
                    var request = ApplyValues(template, markers, values);
                    yield return new HttpIntruderIteration(TruncateLabel(payload), request);
                }

                break;
        }
    }

    public static string ApplyValues(string template, IReadOnlyList<HttpIntruderMarker> markers, IReadOnlyList<string> values)
    {
        if (markers.Count == 0)
            return template;

        var sb = new System.Text.StringBuilder(template);
        for (var i = markers.Count - 1; i >= 0; i--)
        {
            var marker = markers[i];
            var value = i < values.Count ? values[i] : marker.Seed;
            sb.Remove(marker.Start, marker.End - marker.Start);
            sb.Insert(marker.Start, value);
        }

        return sb.ToString();
    }

    private static string TruncateLabel(string payload)
    {
        if (payload.Length <= 48)
            return payload;
        return payload[..45] + "...";
    }
}
