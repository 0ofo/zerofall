namespace ZeroFall.Fingerprint.Core;

public sealed class FrameworkSet
{
    private readonly Dictionary<string, Framework> _items = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _items.Count;

    public IEnumerable<Framework> Values => _items.Values;

    public bool Add(Framework? framework)
    {
        if (framework is null)
            return false;

        framework.Name = framework.Name.ToLowerInvariant();
        if (_items.TryGetValue(framework.Name, out var existing))
        {
            foreach (var source in framework.Sources)
                existing.Sources.Add(source);
            foreach (var tag in framework.Tags)
            {
                if (!existing.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    existing.Tags.Add(tag);
            }

            if (!string.IsNullOrEmpty(framework.Version))
                existing.Version = framework.Version;
            return false;
        }

        _items[framework.Name] = framework;
        return true;
    }

    public void Merge(FrameworkSet other)
    {
        foreach (var framework in other.Values)
            Add(framework);
    }

    public FrameworkSet WithoutSource(FrameworkSource source)
    {
        var filtered = new FrameworkSet();
        foreach (var framework in Values)
        {
            if (framework.Source == source)
                continue;
            filtered.Add(framework);
        }

        return filtered;
    }

    public IReadOnlyList<string> GetNames() =>
        _items.Values.Select(f => f.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public string ToCmsString() => string.Join(',', GetNames());
}
