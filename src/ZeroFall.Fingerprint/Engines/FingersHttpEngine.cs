using System.Text.Json;
using System.Text.RegularExpressions;
using ZeroFall.Fingerprint.Core;
using ZeroFall.Fingerprint.Hashing;
using ZeroFall.Fingerprint.Json;

namespace ZeroFall.Fingerprint.Engines;

public sealed class FingersHttpEngine : IWebMatchEngine, IFaviconContributor, IActiveFingerprintEngine
{
    private readonly List<FingersCompiledFinger> _fingers;

    private FingersHttpEngine(List<FingersCompiledFinger> fingers) => _fingers = fingers;

    public string Name => "fingers";
    public bool SupportWeb => true;

    public IEnumerable<(string Name, string? Vendor, string? Product)> GetFingerBaselines() =>
        _fingers.Select(f => (f.Name, f.Vendor, f.Product));

    public static FingersHttpEngine Load(string path)
    {
        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize(json, FingerprintJsonContext.Default.FingersEntryDtoArray) ?? [];
        return new FingersHttpEngine(entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.Rule is { Count: > 0 })
            .Select(FingersCompiledFinger.Create)
            .ToList());
    }

    public FrameworkSet WebMatch(byte[] rawContent, WebMatchContext context)
    {
        var result = new FrameworkSet();
        foreach (var finger in _fingers)
        {
            var frame = finger.PassiveMatch(context);
            if (frame is not null)
                result.Add(frame);
        }

        return result;
    }

    public void ContributeFaviconHashes(FaviconEngine faviconEngine)
    {
        foreach (var finger in _fingers)
        {
            foreach (var (md5, mmh3) in finger.FaviconHashes)
            {
                if (!string.IsNullOrEmpty(mmh3))
                    faviconEngine.Mmh3Fingers[mmh3] = finger.Name;
                if (!string.IsNullOrEmpty(md5))
                    faviconEngine.Md5Fingers[md5] = finger.Name;
            }
        }
    }

    public Task<FrameworkSet> ActiveMatchAsync(
        string baseUrl,
        int level,
        Func<string, Task<byte[]?>> sender,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ActiveMatch(baseUrl, level, path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return sender(path).GetAwaiter().GetResult();
        }));

    internal FrameworkSet ActiveMatch(string baseUrl, int level, Func<string, byte[]?> sender)
    {
        var result = new FrameworkSet();
        foreach (var finger in _fingers)
        {
            var frame = finger.ActiveMatch(level, sender);
            if (frame is not null)
                result.Add(frame);
        }

        return result;
    }

    public sealed class FingersCompiledFinger
    {
        private readonly List<FingersCompiledRule> _rules;
        private readonly string? _sendData;

        public string Name { get; }
        public string? Vendor { get; }
        public string? Product { get; }
        public List<(string? Md5, string? Mmh3)> FaviconHashes { get; } = [];

        private FingersCompiledFinger(
            string name,
            string? vendor,
            string? product,
            string? sendData,
            List<FingersCompiledRule> rules)
        {
            Name = name;
            Vendor = vendor;
            Product = product;
            _sendData = sendData;
            _rules = rules;
            foreach (var rule in rules)
                FaviconHashes.AddRange(rule.FaviconHashes);
        }

        internal static FingersCompiledFinger Create(FingersEntryDto dto)
        {
            var rules = (dto.Rule ?? []).Select(r => FingersCompiledRule.Create(dto.Name!, r)).ToList();
            return new FingersCompiledFinger(dto.Name!, dto.Vendor, dto.Product, dto.SendData, rules);
        }

        public Framework? PassiveMatch(WebMatchContext context)
        {
            foreach (var rule in _rules)
            {
                if (rule.TryMatch(context, out var version))
                    return CreateFramework(version);
            }

            return null;
        }

        public Framework? ActiveMatch(int level, Func<string, byte[]?> sender)
        {
            if (level <= 0 || sender is null)
                return null;

            foreach (var rule in _rules)
            {
                foreach (var payload in rule.GetActivePayloads(level, _sendData))
                {
                    var response = sender(payload);
                    if (response is null || response.Length == 0)
                        continue;

                    var activeContext = WebMatchContext.FromRaw(response);
                    if (rule.TryMatchFavicon(response))
                        return CreateFramework(string.Empty);
                    if (rule.TryMatch(activeContext, out var version))
                        return CreateFramework(version);
                }
            }

            return null;
        }

        private Framework CreateFramework(string version)
        {
            var frame = string.IsNullOrEmpty(version)
                ? new Framework(Name, FrameworkSource.Fingers)
                : new Framework(Name, FrameworkSource.Fingers) { Version = version };
            return frame;
        }
    }

    internal sealed class FingersCompiledRule
    {
        private readonly Regex[] _regexp;
        private readonly Regex[] _versionRegexp;
        private readonly byte[][] _header;
        private readonly byte[][] _body;
        private readonly string[] _md5;
        private readonly string[] _mmh3;
        private readonly string[] _cert;
        private readonly string? _sendData;
        private readonly int _level;
        public string Version { get; }
        public List<(string? Md5, string? Mmh3)> FaviconHashes { get; } = [];

        private FingersCompiledRule(
            Regex[] regexp,
            Regex[] versionRegexp,
            byte[][] header,
            byte[][] body,
            string[] md5,
            string[] mmh3,
            string[] cert,
            string version,
            string? sendData,
            int level,
            List<(string? Md5, string? Mmh3)> faviconHashes)
        {
            _regexp = regexp;
            _versionRegexp = versionRegexp;
            _header = header;
            _body = body;
            _md5 = md5;
            _mmh3 = mmh3;
            _cert = cert;
            Version = version;
            _sendData = sendData;
            _level = level;
            FaviconHashes = faviconHashes;
        }

        public static FingersCompiledRule Create(string fingerName, FingersRuleDto dto)
        {
            var regexps = dto.Regexps;
            var regexp = (regexps?.Regexp ?? [])
                .Select(p => RegexFactory.TryCreate("(?i)" + p))
                .Where(r => r is not null)
                .Cast<Regex>()
                .ToArray();
            var versionRegexp = (regexps?.Version ?? [])
                .Select(RegexFactory.TryCreate)
                .Where(r => r is not null)
                .Cast<Regex>()
                .ToArray();
            var header = (regexps?.Header ?? []).Select(h => h.ToLowerInvariant().GetBytes()).ToArray();
            var body = (regexps?.Body ?? []).Select(b => b.ToLowerInvariant().GetBytes()).ToArray();
            var md5 = regexps?.Md5 ?? [];
            var mmh3 = regexps?.Mmh3 ?? [];
            var cert = regexps?.Cert ?? [];
            var version = string.IsNullOrWhiteSpace(dto.Version) ? "_" : dto.Version!;
            var faviconHashes = new List<(string? Md5, string? Mmh3)>();
            foreach (var hash in dto.Favicon?.Md5 ?? [])
                faviconHashes.Add((hash, null));
            foreach (var hash in dto.Favicon?.Mmh3 ?? [])
                faviconHashes.Add((null, hash));

            return new FingersCompiledRule(
                regexp,
                versionRegexp,
                header,
                body,
                md5,
                mmh3,
                cert,
                version,
                dto.SendData,
                dto.Level ?? 0,
                faviconHashes);
        }

        public bool TryMatch(WebMatchContext context, out string version)
        {
            version = string.Empty;
            var content = context.LowerContent;

            foreach (var reg in _regexp)
            {
                var match = reg.Match(context.LowerText);
                if (!match.Success)
                    continue;
                version = ExtractVersion(content, match);
                return true;
            }

            if (context.Header.Length > 0)
            {
                foreach (var headerNeedle in _header)
                {
                    if (ContainsBytes(context.Header, headerNeedle))
                    {
                        version = ExtractVersion(content, null);
                        return true;
                    }
                }
            }

            var body = context.Body.Length > 0 ? context.Body : content;
            foreach (var bodyNeedle in _body)
            {
                if (ContainsBytes(body, bodyNeedle))
                {
                    version = ExtractVersion(content, null);
                    return true;
                }
            }

            var bodyMd5 = ContentHasher.Md5Hex(body);
            if (_md5.Contains(bodyMd5, StringComparer.OrdinalIgnoreCase))
            {
                version = ExtractVersion(content, null);
                return true;
            }

            var bodyMmh3 = ContentHasher.Mmh3Hash32(body);
            if (_mmh3.Contains(bodyMmh3, StringComparer.Ordinal))
            {
                version = ExtractVersion(content, null);
                return true;
            }

            if (!string.IsNullOrEmpty(context.Cert) && _cert.Any(c => context.Cert.Contains(c, StringComparison.OrdinalIgnoreCase)))
            {
                version = ExtractVersion(content, null);
                return true;
            }

            return false;
        }

        public bool TryMatchFavicon(byte[] rawContent)
        {
            if (FaviconHashes.Count == 0)
                return false;

            var split = RawHttpSplitter.Split(rawContent);
            var body = split.Body.IsEmpty ? rawContent : split.Body.Span;
            if (body.IsEmpty)
                return false;

            var md5 = ContentHasher.Md5Hex(body);
            var mmh3 = ContentHasher.Mmh3Hash32(body);
            return FaviconHashes.Any(h =>
                (!string.IsNullOrEmpty(h.Md5) && h.Md5.Equals(md5, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(h.Mmh3) && h.Mmh3 == mmh3));
        }

        public IEnumerable<string> GetActivePayloads(int level, string? fingerSendData)
        {
            if (level <= 0)
                yield break;
            if (_level > 0 && level < _level)
                yield break;

            if (level >= 1 && !string.IsNullOrWhiteSpace(fingerSendData))
                yield return fingerSendData;
            if (level >= 2 && !string.IsNullOrWhiteSpace(_sendData))
                yield return _sendData!;
        }

        private string ExtractVersion(ReadOnlySpan<byte> content, Match? regexpMatch)
        {
            if (regexpMatch is { Success: true, Groups.Count: > 1 })
            {
                var captured = regexpMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(captured))
                    return captured;
            }

            foreach (var reg in _versionRegexp)
            {
                var match = reg.Match(System.Text.Encoding.UTF8.GetString(content));
                if (match.Success && match.Groups.Count > 1)
                {
                    var captured = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(captured))
                        return captured;
                }
            }

            return Version == "_" ? string.Empty : Version;
        }

        private static bool ContainsBytes(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
            haystack.IndexOf(needle) >= 0;
    }
}

internal static class StringByteExtensions
{
    public static byte[] GetBytes(this string value) => System.Text.Encoding.UTF8.GetBytes(value);
}
