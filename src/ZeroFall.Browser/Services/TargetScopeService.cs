using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeroFall.Browser.Services;

public sealed class TargetScopeService : ITargetScopeService
{
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Hosts => _hosts;

    public bool HasEntries => _hosts.Count > 0;

    public event Action? Changed;

    public bool AddHost(string host)
    {
        var normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized))
            return false;

        if (!_hosts.Add(normalized))
            return false;

        Changed?.Invoke();
        return true;
    }

    public bool RemoveHost(string host)
    {
        var normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized))
            return false;

        if (!_hosts.Remove(normalized))
            return false;

        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (_hosts.Count == 0)
            return;

        _hosts.Clear();
        Changed?.Invoke();
    }

    public bool IsUrlInScope(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return IsHostInScope(uri.Host);
    }

    public bool IsHostInScope(string host)
    {
        var normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized))
            return false;

        if (_hosts.Count == 0)
            return false;

        foreach (var scoped in _hosts)
        {
            if (HostMatchesScope(normalized, scoped))
                return true;
        }

        return false;
    }

    internal static bool HostMatchesScope(string host, string scopedHost)
    {
        if (string.Equals(host, scopedHost, StringComparison.OrdinalIgnoreCase))
            return true;

        return host.EndsWith("." + scopedHost, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var text = host.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
            text = uri.Host;

        text = text.Trim().TrimEnd('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    internal static string? ExtractHostFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            ? NormalizeHost(uri.Host)
            : null;
    }
}
