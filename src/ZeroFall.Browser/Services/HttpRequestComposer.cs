using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Platform.Services;

namespace ZeroFall.Browser.Services;

public static class HttpRequestComposer
{
    public static HttpReplayDraft BuildDraft(
        string sourceEntryId,
        string method,
        string url,
        string requestHeaders,
        string requestBody)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var originalUri))
            originalUri = new Uri("http://invalid.local/");

        var requestText = BuildRequestText(method, originalUri.PathAndQuery, requestHeaders, requestBody);
        // 连接地址取自 URL authority；Host 头仅留在 requestHeaders 中供服务端分流
        var connectAuthority = originalUri.Authority;
        if (string.IsNullOrWhiteSpace(connectAuthority))
            connectAuthority = originalUri.Host;

        return new HttpReplayDraft
        {
            SourceEntryId = sourceEntryId,
            Method = method,
            OriginalUrl = url,
            RequestText = requestText,
            RealHost = connectAuthority,
            IsHttps = string.Equals(originalUri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
        };
    }

    public static async Task<HttpSendResult> SendAsync(
        IOutboundHttpClientFactory httpClientFactory,
        string purpose,
        TimeSpan timeout,
        string requestText,
        string fallbackMethod,
        string realHost,
        bool isHttps,
        CancellationToken cancellationToken = default)
    {
        var (method, pathAndQuery) = ParseRequestLine(requestText, fallbackMethod);
        var headers = ExtractHeadersFromHttpText(requestText);
        var body = ExtractBodyFromHttpText(requestText);
        if (!TryBuildReplayUri(realHost, isHttps, pathAndQuery, out var replayUri))
            return HttpSendResult.Failed("real host 无效");

        using var httpClient = httpClientFactory.CreateClient(purpose, timeout);
        using var requestMessage = BuildHttpRequestMessage(method, replayUri, headers, body);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await httpClient
                .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return new HttpSendResult
            {
                Success = true,
                StatusCode = (int)response.StatusCode,
                LatencyMs = sw.ElapsedMilliseconds,
                ResponseText = BuildResponseText(response, responseBody),
                ResponseLength = responseBody.Length
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return HttpSendResult.Failed(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public static bool TryBuildReplayUri(string realHost, bool isHttps, string pathAndQuery, out Uri uri)
    {
        var scheme = isHttps ? "https" : "http";
        if (!Uri.TryCreate($"{scheme}://{realHost}", UriKind.Absolute, out var baseUri))
        {
            uri = default!;
            return false;
        }

        uri = new Uri(baseUri, string.IsNullOrWhiteSpace(pathAndQuery) ? "/" : pathAndQuery);
        return true;
    }

    public static (string Method, string PathAndQuery) ParseRequestLine(string httpText, string fallbackMethod)
    {
        var firstLine = httpText.Replace("\r\n", "\n").Split('\n')[0].Trim();
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return (parts[0].Trim(), parts[1].Trim());

        return (fallbackMethod, "/");
    }

    public static string PrepareOutboundRequestHeaders(string wireHeaders, string method, string body)
    {
        wireHeaders = EnsureRequestContentType(wireHeaders, method, body);
        return EnsureRequestContentLength(wireHeaders, method, body);
    }

    public static string EnsureRequestContentType(string wireHeaders, string method, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return wireHeaders;

        var normalizedMethod = method.Trim().ToUpperInvariant();
        if (normalizedMethod is not ("POST" or "PUT" or "PATCH"))
            return wireHeaders;

        if (!string.IsNullOrWhiteSpace(TryGetHeader(wireHeaders, "Content-Type")))
            return wireHeaders;

        if (!LooksLikeFormUrlEncoded(body))
            return wireHeaders;

        var trimmed = wireHeaders.TrimEnd('\r', '\n');
        return trimmed.Length == 0
            ? "Content-Type: application/x-www-form-urlencoded\n"
            : trimmed + "\nContent-Type: application/x-www-form-urlencoded\n";
    }

    private static bool LooksLikeFormUrlEncoded(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        if (body.Contains('\n') || body.Contains('\r'))
            return false;

        var trimmed = body.Trim();
        if (!trimmed.Contains('='))
            return false;

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return false;

        foreach (var segment in trimmed.Split('&'))
        {
            var eq = segment.IndexOf('=');
            if (eq <= 0)
                return false;
        }

        return true;
    }

    public static string EnsureRequestContentLength(string wireHeaders, string method, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return wireHeaders;

        var normalizedMethod = method.Trim().ToUpperInvariant();
        if (normalizedMethod is not ("POST" or "PUT" or "PATCH"))
            return wireHeaders;

        if (!string.IsNullOrWhiteSpace(TryGetHeader(wireHeaders, "Content-Length")))
            return wireHeaders;

        var byteCount = Encoding.UTF8.GetByteCount(body);
        var trimmed = wireHeaders.TrimEnd('\r', '\n');
        return trimmed.Length == 0
            ? $"Content-Length: {byteCount}\n"
            : trimmed + $"\nContent-Length: {byteCount}\n";
    }

    public static HttpRequestMessage BuildHttpRequestMessage(string method, Uri uri, string headers, string body)
    {
        headers = PrepareOutboundRequestHeaders(headers, method, body);
        var requestMessage = new HttpRequestMessage(new HttpMethod(method), uri);
        if (!string.IsNullOrWhiteSpace(body))
            requestMessage.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));

        var headerHost = TryGetHeader(headers, "Host");
        foreach (var rawLine in headers.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var idx = line.IndexOf(':');
            if (idx <= 0)
                continue;

            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!requestMessage.Headers.TryAddWithoutValidation(name, value))
                requestMessage.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        if (!string.IsNullOrWhiteSpace(headerHost))
            requestMessage.Headers.Host = headerHost;
        return requestMessage;
    }

    public static string BuildRequestText(string method, string pathAndQuery, string headers, string body)
    {
        var sb = new StringBuilder();
        if (!HeadersStartWithRequestLine(headers))
            sb.AppendLine($"{method} {pathAndQuery} HTTP/1.1");
        if (!string.IsNullOrWhiteSpace(headers))
            sb.Append(headers.TrimEnd()).AppendLine();
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(body))
            sb.Append(body);
        return sb.ToString();
    }

    public static bool HeadersStartWithRequestLine(string headers)
    {
        var first = FirstHeaderLine(headers);
        if (string.IsNullOrEmpty(first))
            return false;

        var firstSpace = first.IndexOf(' ');
        if (firstSpace <= 0)
            return false;

        var lastSpace = first.LastIndexOf(' ');
        if (lastSpace <= firstSpace)
            return false;

        var methodToken = first[..firstSpace];
        return IsHttpMethod(methodToken) && first[(lastSpace + 1)..].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HeadersStartWithResponseLine(string headers)
    {
        var first = FirstHeaderLine(headers);
        return first.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstHeaderLine(string headers)
    {
        if (string.IsNullOrWhiteSpace(headers))
            return string.Empty;

        var trimmed = headers.TrimStart();
        var lineEnd = trimmed.IndexOfAny(['\r', '\n']);
        return (lineEnd >= 0 ? trimmed[..lineEnd] : trimmed).Trim();
    }

    private static bool IsHttpMethod(string method) =>
        method is "GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD" or "OPTIONS" or "TRACE" or "CONNECT";

    public static string BuildResponseText(HttpResponseMessage response, string body)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"HTTP/{response.Version} {(int)response.StatusCode}");
        foreach (var header in response.Headers)
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        foreach (var header in response.Content.Headers)
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        sb.AppendLine();
        sb.Append(body);
        return sb.ToString();
    }

    public static string TryGetHeader(string headers, string headerName)
    {
        foreach (var rawLine in headers.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                continue;
            return line[(headerName.Length + 1)..].Trim();
        }

        return string.Empty;
    }

    public static string ExtractHeadersFromHttpText(string httpText)
    {
        var lines = httpText.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= 1) return string.Empty;
        var sb = new StringBuilder();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) break;
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    public static string ExtractBodyFromHttpText(string httpText)
    {
        var normalized = httpText.Replace("\r\n", "\n");
        var separatorIndex = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        if (separatorIndex < 0) return string.Empty;
        return normalized[(separatorIndex + 2)..];
    }
}

public sealed class HttpReplayDraft
{
    public required string SourceEntryId { get; init; }
    public required string Method { get; init; }
    public required string OriginalUrl { get; init; }
    public required string RequestText { get; init; }
    public required string RealHost { get; init; }
    public required bool IsHttps { get; init; }
}

public sealed class HttpSendResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public long LatencyMs { get; init; }
    public int ResponseLength { get; init; }
    public string ResponseText { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;

    public static HttpSendResult Failed(string error, long latencyMs = 0) =>
        new() { Success = false, Error = error, LatencyMs = latencyMs };
}
