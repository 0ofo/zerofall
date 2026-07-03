using System;
using System.Collections.Generic;

namespace ZeroFall.Traffic.Ingest;

/// <summary>浏览器 CDP 与 Fluxzy 代理同时可见时，优先保留浏览器（含 Tab/会话归因）。</summary>
public sealed class TrafficCaptureDedup
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private sealed record Slot(string EntryId, bool IsBrowser, long UtcTicks);

    public enum Decision
    {
        Accept,
        DropProxyDuplicate,
        SupersedeProxy
    }

    public (Decision Decision, string? SupersededEntryId) Evaluate(
        string entryId,
        Capture.TrafficCaptureSource source,
        string method,
        string url)
    {
        if (string.IsNullOrWhiteSpace(entryId)
            || string.IsNullOrWhiteSpace(method)
            || string.IsNullOrWhiteSpace(url))
            return (Decision.Accept, null);

        if (source is not (Capture.TrafficCaptureSource.Browser or Capture.TrafficCaptureSource.Proxy))
            return (Decision.Accept, null);

        var isBrowser = source == Capture.TrafficCaptureSource.Browser;
        var key = BuildKey(method, url);

        lock (_gate)
        {
            PruneExpired();

            if (_slots.TryGetValue(key, out var existing) && IsRecent(existing))
            {
                if (!isBrowser && existing.IsBrowser)
                    return (Decision.DropProxyDuplicate, null);

                if (isBrowser && !existing.IsBrowser)
                {
                    _slots[key] = new Slot(entryId, true, DateTime.UtcNow.Ticks);
                    return (Decision.SupersedeProxy, existing.EntryId);
                }
            }

            _slots[key] = new Slot(entryId, isBrowser, DateTime.UtcNow.Ticks);
            return (Decision.Accept, null);
        }
    }

    public void Reset()
    {
        lock (_gate)
            _slots.Clear();
    }

    private static bool IsRecent(Slot slot) =>
        DateTime.UtcNow - new DateTime(slot.UtcTicks, DateTimeKind.Utc) <= Window;

    private static void PruneExpired(Dictionary<string, Slot> slots)
    {
        if (slots.Count == 0)
            return;

        var cutoff = DateTime.UtcNow.Subtract(Window).Ticks;
        var remove = new List<string>();
        foreach (var (key, slot) in slots)
        {
            if (slot.UtcTicks < cutoff)
                remove.Add(key);
        }

        foreach (var key in remove)
            slots.Remove(key);
    }

    private void PruneExpired() => PruneExpired(_slots);

    public static string BuildKey(string method, string url) =>
        $"{method.Trim().ToUpperInvariant()}|{NormalizeUrlKey(url)}";

    private static string NormalizeUrlKey(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url.Trim();

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
