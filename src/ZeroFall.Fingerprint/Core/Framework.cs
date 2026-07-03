namespace ZeroFall.Fingerprint.Core;

public sealed class Framework
{
    public string Name { get; set; }
    public string Version { get; set; } = string.Empty;
    public FrameworkSource Source { get; init; }
    public HashSet<FrameworkSource> Sources { get; } = [];
    public List<string> Tags { get; } = [];

    public Framework(string name, FrameworkSource source)
    {
        Name = name.ToLowerInvariant();
        Source = source;
        Sources.Add(source);
        if (source >= FrameworkSource.Fingers)
            Tags.Add(source.ToKey());
    }

    public string DisplayText
    {
        get
        {
            var text = Name ?? string.Empty;
            if (!string.IsNullOrEmpty(Version))
                text += ":" + Version.Replace(':', '_');
            if (Sources.Count > 1)
            {
                text += ":(" + string.Join(' ', Sources.Select(s => s.ToKey())) + ")";
            }
            else if (Source != FrameworkSource.Fingers)
            {
                text += ":" + Source.ToKey();
            }

            return text;
        }
    }
}
