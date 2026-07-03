using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ZeroFall.Fingerprint.Text;
using ZeroFall.Fingerprint.Hashing;

namespace ZeroFall.Fingerprint.Http;

public sealed class HttpProbeResponse
{
    public required string Url { get; init; }
    public required byte[] RawContent { get; init; }
    public required string Body { get; init; }
    public required string Header { get; init; }
    public string Server { get; init; } = string.Empty;
    public int StatusCode { get; init; }
    public int Length { get; init; }
    public string Title { get; init; } = string.Empty;
    public string FaviconHash { get; init; } = string.Empty;
    public byte[]? FaviconBytes { get; init; }
    public IReadOnlyList<string> ContentRedirectUrls { get; init; } = [];
}

public sealed class HttpProbeClient
{
    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36"
    ];

    public async Task<HttpProbeResponse> FetchAsync(
        string url,
        FingerprintScanOptions options,
        bool parseContentRedirects,
        CancellationToken cancellationToken = default)
    {
        url = NormalizeUrl(url);
        using var handler = CreateHandler(options);
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgents[Random.Shared.Next(UserAgents.Length)]);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Connection", "close");
        request.Headers.Add("Cookie", "rememberMe=me");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var rawBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.ToString();
        var body = ResponseTextDecoder.DecodeToUtf8(rawBody, contentType);
        var header = BuildHeaderString(response.Headers, response.Content.Headers);
        var server = response.Headers.Server?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(server) && response.Headers.TryGetValues("X-Powered-By", out var powered))
            server = powered.FirstOrDefault() ?? string.Empty;

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        var faviconHash = string.Empty;
        byte[]? faviconBytes = null;
        if (response.IsSuccessStatusCode)
            (faviconHash, faviconBytes) = await TryFetchFaviconAsync(client, body, finalUrl, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> redirects = [];
        if (parseContentRedirects)
            redirects = ContentRedirectParser.Parse(body, finalUrl);

        return new HttpProbeResponse
        {
            Url = finalUrl,
            RawContent = BuildRawResponse(response, rawBody),
            Body = body,
            Header = header,
            Server = server,
            StatusCode = (int)response.StatusCode,
            Length = body.Length,
            Title = ResponseTextDecoder.ExtractTitle(body),
            FaviconHash = faviconHash,
            FaviconBytes = faviconBytes,
            ContentRedirectUrls = redirects
        };
    }

    private static HttpClientHandler CreateHandler(FingerprintScanOptions options)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = options.RedirectPolicy.FollowHttp(),
            MaxAutomaticRedirections = 8
        };

        if (!string.IsNullOrWhiteSpace(options.Proxy))
            handler.Proxy = new WebProxy(options.Proxy);
        return handler;
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "https://" + url;
        return url;
    }

    private static string BuildHeaderString(HttpResponseHeaders headers, HttpContentHeaders contentHeaders)
    {
        var sb = new StringBuilder();
        foreach (var pair in headers.NonValidated)
        {
            foreach (var value in pair.Value)
            {
                sb.Append(pair.Key);
                sb.Append(": ");
                sb.AppendLine(value);
            }
        }

        foreach (var pair in contentHeaders.NonValidated)
        {
            foreach (var value in pair.Value)
            {
                sb.Append(pair.Key);
                sb.Append(": ");
                sb.AppendLine(value);
            }
        }

        return sb.ToString();
    }

    private static byte[] BuildRawResponse(HttpResponseMessage response, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/");
        sb.Append(response.Version);
        sb.Append(' ');
        sb.Append((int)response.StatusCode);
        sb.Append(' ');
        sb.AppendLine(response.ReasonPhrase ?? string.Empty);
        foreach (var pair in response.Headers.NonValidated)
        {
            foreach (var value in pair.Value)
                sb.AppendLine($"{pair.Key}: {value}");
        }

        foreach (var pair in response.Content.Headers.NonValidated)
        {
            foreach (var value in pair.Value)
                sb.AppendLine($"{pair.Key}: {value}");
        }

        sb.AppendLine();
        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var result = new byte[headerBytes.Length + body.Length];
        headerBytes.CopyTo(result, 0);
        body.CopyTo(result, headerBytes.Length);
        return result;
    }

    private static async Task<(string Hash, byte[]? Bytes)> TryFetchFaviconAsync(
        HttpClient client,
        string body,
        string pageUrl,
        CancellationToken cancellationToken)
    {
        var faviconUrl = FaviconUrlExtractor.Extract(body, pageUrl);
        if (string.IsNullOrEmpty(faviconUrl))
            return (string.Empty, null);

        try
        {
            using var response = await client.GetAsync(faviconUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (string.Empty, null);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length == 0
                ? (string.Empty, null)
                : (FaviconMmh3Hasher.Compute(bytes), bytes);
        }
        catch
        {
            return (string.Empty, null);
        }
    }
}
