using System.Collections.Concurrent;
using System.Text.Json;
using ZeroFall.Fingerprint.Json;

namespace ZeroFall.Fingerprint;

public sealed class FingerprintScanner
{
    private readonly Core.FingerprintEngine _engine;
    private readonly Http.HttpProbeClient _http = new();

    public FingerprintScanner(Core.FingerprintEngine engine) => _engine = engine;

    public static FingerprintScanner CreateDefault(FingerprintEngineOptions? engineOptions = null) =>
        new(FingerprintEngineFactory.Create(engineOptions ?? new FingerprintEngineOptions()));

    public async Task<IReadOnlyList<FingerprintScanResult>> ScanManyAsync(
        IEnumerable<string> urls,
        FingerprintScanOptions options,
        CancellationToken cancellationToken = default)
    {
        var list = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new ConcurrentBag<FingerprintScanResult>();
        await Parallel.ForEachAsync(
            list,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.ThreadCount), CancellationToken = cancellationToken },
            async (url, ct) =>
            {
                var result = await ScanOneAsync(url, options, ct).ConfigureAwait(false);
                if (result is not null)
                    results.Add(result);
            }).ConfigureAwait(false);

        return results.OrderBy(r => r.Url, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<FingerprintScanResult?> ScanOneAsync(
        string url,
        FingerprintScanOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.FetchAsync(url, options, options.RedirectPolicy.FollowContent(), cancellationToken)
                .ConfigureAwait(false);

            if (options.RedirectPolicy.FollowContent())
            {
                foreach (var redirect in response.ContentRedirectUrls)
                {
                    if (string.IsNullOrWhiteSpace(redirect))
                        continue;
                    var redirected = await _http.FetchAsync(redirect, options, false, cancellationToken).ConfigureAwait(false);
                    response = redirected;
                    break;
                }
            }

            var frameworks = _engine.WebMatch(response.RawContent);
            if (response.FaviconBytes is { Length: > 0 })
            {
                var faviconMatches = _engine.MatchFavicon(response.FaviconBytes);
                _engine.MergeFrameworks(frameworks, faviconMatches);
            }

            return new FingerprintScanResult
            {
                Url = response.Url,
                Cms = frameworks.ToCmsString(),
                Server = response.Server,
                StatusCode = response.StatusCode,
                Length = response.Length,
                Title = response.Title
            };
        }
        catch
        {
            return null;
        }
    }

    public static string ToJsonLine(FingerprintScanResult result) =>
        JsonSerializer.Serialize(result, FingerprintJsonContext.Default.FingerprintScanResult);

    public static string ToJsonArray(IReadOnlyList<FingerprintScanResult> results) =>
        JsonSerializer.Serialize(results.ToArray(), FingerprintJsonContext.Default.FingerprintScanResultArray);
}
