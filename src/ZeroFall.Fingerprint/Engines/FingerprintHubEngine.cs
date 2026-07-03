using System.Text;
using System.Text.Json;
using ZeroFall.Fingerprint.Core;
using ZeroFall.Fingerprint.Json;

namespace ZeroFall.Fingerprint.Engines;

public sealed class FingerprintHubEngine : IWebMatchEngine
{
    private readonly List<HubCompiledEntry> _entries;

    private FingerprintHubEngine(List<HubCompiledEntry> entries) => _entries = entries;

    public string Name => "fingerprinthub";
    public bool SupportWeb => true;

    public static FingerprintHubEngine Load(string path)
    {
        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize(json, FingerprintJsonContext.Default.FingerprintHubEntryDtoArray) ?? [];
        return new FingerprintHubEngine(entries
            .Select(HubCompiledEntry.Create)
            .Where(e => e is not null)
            .Cast<HubCompiledEntry>()
            .ToList());
    }

    public FrameworkSet WebMatch(byte[] rawContent, WebMatchContext context)
    {
        var result = new FrameworkSet();
        var body = context.BodyText;
        var lowerBody = body.ToLowerInvariant();
        var header = BuildHeaderString(context.HeadersMap, caseInsensitive: true);

        foreach (var entry in _entries)
        {
            if (entry.IsMatch(header, lowerBody))
                result.Add(new Framework(entry.Name, FrameworkSource.FingerprintHub));
        }

        return result;
    }

    private static string BuildHeaderString(IReadOnlyDictionary<string, string[]> headers, bool caseInsensitive)
    {
        var sb = new StringBuilder();
        foreach (var (key, values) in headers)
        {
            foreach (var value in values)
            {
                sb.Append(key.ToLowerInvariant());
                sb.Append(": ");
                sb.AppendLine(caseInsensitive ? value.ToLowerInvariant() : value);
            }
        }

        return sb.ToString();
    }

    private sealed class HubCompiledEntry
    {
        private readonly string[][] _wordGroups;
        private readonly bool _caseInsensitive;

        public string Name { get; }

        private HubCompiledEntry(string name, string[][] wordGroups, bool caseInsensitive)
        {
            Name = name;
            _wordGroups = wordGroups;
            _caseInsensitive = caseInsensitive;
        }

        public static HubCompiledEntry? Create(FingerprintHubEntryDto dto)
        {
            var name = dto.Info?.Name ?? dto.Id;
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var groups = new List<string[]>();
            var caseInsensitive = true;
            foreach (var http in dto.Http ?? [])
            {
                foreach (var matcher in http.Matchers ?? [])
                {
                    if (!string.Equals(matcher.Type, "word", StringComparison.OrdinalIgnoreCase)
                        || matcher.Words is not { Length: > 0 })
                        continue;
                    groups.Add(matcher.Words.Select(w => w.ToLowerInvariant()).ToArray());
                    caseInsensitive = matcher.CaseInsensitive;
                }
            }

            if (groups.Count == 0)
                return null;

            return new HubCompiledEntry(name, groups.ToArray(), caseInsensitive);
        }

        public bool IsMatch(string header, string lowerBody)
        {
            var comparison = _caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var haystack = lowerBody;
            if (_caseInsensitive)
                header = header.ToLowerInvariant();

            return _wordGroups.Any(words => words.All(word =>
            {
                if (haystack.Contains(word, comparison))
                    return true;
                return header.Contains(word, comparison);
            }));
        }
    }
}
