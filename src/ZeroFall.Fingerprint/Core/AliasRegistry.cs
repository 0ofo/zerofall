namespace ZeroFall.Fingerprint.Core;

public sealed class AliasEntry
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, List<string>> AliasMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> BlockedEngines { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Vendor { get; set; }
    public string? Product { get; set; }
}

public sealed class AliasRegistry
{
    private readonly Dictionary<string, AliasEntry> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _map = new(StringComparer.OrdinalIgnoreCase);

    public static AliasRegistry Load(string path)
    {
        var registry = new AliasRegistry();
        if (!File.Exists(path))
            return registry;

        registry.Compile(MinimalYamlAliasParser.Parse(File.ReadAllText(path)));
        return registry;
    }

    public void AppendBaseline(IEnumerable<(string Name, string? Vendor, string? Product)> fingers)
    {
        foreach (var finger in fingers)
        {
            var entry = new AliasEntry
            {
                Name = finger.Name,
                Vendor = finger.Vendor,
                Product = finger.Product
            };
            entry.AliasMap["fingers"] = [entry.Name];
            Append(entry);
        }
    }

    public void Compile(IEnumerable<AliasEntry> entries)
    {
        foreach (var entry in entries)
            Append(entry);
    }

    public void Append(AliasEntry entry)
    {
        entry.Name = entry.Name.ToLowerInvariant();
        _aliases[entry.Name] = entry;

        foreach (var (engine, names) in entry.AliasMap)
        {
            if (!_map.TryGetValue(engine, out var engineMap))
            {
                engineMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _map[engine] = engineMap;
            }

            foreach (var name in names)
                engineMap[NormalizeName(name)] = entry.Name;
        }
    }

    public (AliasEntry? Alias, bool Accepted) FindFramework(Framework framework)
    {
        var engine = framework.Source.ToKey();
        if (!_map.TryGetValue(engine, out var engineMap))
            return (null, false);

        if (!engineMap.TryGetValue(NormalizeName(framework.Name), out var aliasName))
            return (null, false);

        if (!_aliases.TryGetValue(aliasName, out var alias))
            return (null, false);

        return alias.BlockedEngines.Contains(engine) ? (alias, false) : (alias, true);
    }

    public static string NormalizeName(string value) =>
        value.ToLowerInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
}

internal static class MinimalYamlAliasParser
{
    private enum Section
    {
        None,
        Alias,
        Block
    }

    public static List<AliasEntry> Parse(string yaml)
    {
        var entries = new List<AliasEntry>();
        AliasEntry? current = null;
        var section = Section.None;
        string? aliasEngine = null;

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            if (line.StartsWith("- name:", StringComparison.Ordinal))
            {
                if (current is not null)
                    entries.Add(current);
                current = new AliasEntry { Name = line["- name:".Length..].Trim() };
                section = Section.None;
                aliasEngine = null;
                continue;
            }

            if (current is null)
                continue;

            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (trimmed.StartsWith("alias:", StringComparison.Ordinal))
            {
                section = Section.Alias;
                aliasEngine = null;
                continue;
            }

            if (trimmed.StartsWith("block:", StringComparison.Ordinal))
            {
                section = Section.Block;
                aliasEngine = null;
                continue;
            }

            if (section == Section.Alias)
            {
                if (indent >= 4 && trimmed.EndsWith(':') && !trimmed.StartsWith('-'))
                {
                    aliasEngine = trimmed[..^1].Trim();
                    if (!current.AliasMap.ContainsKey(aliasEngine))
                        current.AliasMap[aliasEngine] = [];
                    continue;
                }

                if (indent >= 6 && trimmed.StartsWith("- ", StringComparison.Ordinal) && aliasEngine is not null)
                {
                    current.AliasMap[aliasEngine].Add(trimmed[2..].Trim());
                    continue;
                }
            }

            if (section == Section.Block && indent >= 4 && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                current.BlockedEngines.Add(trimmed[2..].Trim());
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();
            if (value is "null" or "~")
                value = string.Empty;

            switch (key)
            {
                case "vendor":
                    current.Vendor = value;
                    section = Section.None;
                    break;
                case "product":
                    current.Product = value;
                    section = Section.None;
                    break;
            }
        }

        if (current is not null)
            entries.Add(current);

        return entries;
    }
}
