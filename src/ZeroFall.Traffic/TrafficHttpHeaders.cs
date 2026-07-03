using System;
using System.Collections.Generic;
using System.Text;

namespace ZeroFall.Traffic;

/// <summary>结构�?HTTP 头：捕获端直接写入，避免 COM→字符串→再 Split 解析�?/summary>
public sealed class TrafficHttpHeaders
{
    private readonly List<(string Name, string Value)> _entries = [];

    public static TrafficHttpHeaders Empty { get; } = new();

    public IReadOnlyList<(string Name, string Value)> Entries => _entries;

    public void Add(string name, string value)
    {
        if (string.IsNullOrEmpty(name))
            return;
        _entries.Add((name, value ?? string.Empty));
    }

    public string? GetFirst(string name)
    {
        foreach (var (entryName, value) in _entries)
        {
            if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    public string GetContentTypeMediaType()
    {
        var raw = GetFirst("Content-Type");
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var value = raw.Trim().ToLowerInvariant();
        var semi = value.IndexOf(';');
        return semi >= 0 ? value[..semi].Trim() : value;
    }

    public bool ContainsName(string name)
    {
        foreach (var (entryName, _) in _entries)
        {
            if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public bool HasFingerprintableHeaders() =>
        ContainsName("Content-Type")
        || ContainsName("Server")
        || ContainsName("Set-Cookie")
        || ContainsName("X-Powered-By");

    /// <summary>详情面板 / SQLite 持久化用�?wire 文本（仅边界序列化一次）�?/summary>
    public string ToWireText()
    {
        if (_entries.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var (name, value) in _entries)
            sb.Append(name).Append(": ").AppendLine(value);
        return sb.ToString();
    }

    public static TrafficHttpHeaders FromWireText(string? wire)
    {
        var headers = new TrafficHttpHeaders();
        if (string.IsNullOrWhiteSpace(wire))
            return headers;

        foreach (var line in wire.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;

            headers.Add(trimmed[..colon].Trim(), trimmed[(colon + 1)..].Trim());
        }

        return headers;
    }
}
