using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Base.AiTools;
using ZeroFall.Platform.Services;

namespace ZeroFall.Browser.Services;

/// <summary>AI <c>fetch</c> 工具出站请求：完整 Header/Cookie 控制，并合并浏览器 CDP Cookie。</summary>
internal static class HttpAiOutboundClient
{
    private const int DefaultTimeoutSeconds = 12;

    public static async Task<HttpAiOutboundResult> SendAsync(
        IOutboundHttpClientFactory httpClientFactory,
        string url,
        string method,
        string body,
        string? headersJson,
        string? cookies,
        Func<string, Task<string?>> resolveBrowserCookieHeaderAsync,
        CancellationToken cancellationToken = default)
    {
        var entryId = Guid.NewGuid().ToString("N");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return HttpAiOutboundResult.Fail("url 必须是绝对地址");

        var headerLines = await BuildHeaderLinesAsync(headersJson, cookies, resolveBrowserCookieHeaderAsync, uri)
            .ConfigureAwait(false);
        if (headerLines == null)
            return HttpAiOutboundResult.Fail("headers 必须是 JSON 对象，如 {\"User-Agent\":\"…\",\"Cookie\":\"…\"}");

        var preparedHeaders = HttpRequestComposer.PrepareOutboundRequestHeaders(
            headerLines,
            method,
            body ?? string.Empty);
        var requestHeadersWire = preparedHeaders;
        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        var isHttps = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

        // 连接目标始终为 URL 的 authority；Host 头可与之不同（如反代按 Host 分流）
        if (!HttpRequestComposer.TryBuildReplayUri(uri.Authority, isHttps, pathAndQuery, out var replayUri))
            return HttpAiOutboundResult.Fail("无法解析请求 URL");

        using var httpClient = httpClientFactory.CreateClient("ai-http-tool", TimeSpan.FromSeconds(DefaultTimeoutSeconds), acceptAnyServerCertificate: true);
        using var requestMessage = HttpRequestComposer.BuildHttpRequestMessage(method, replayUri, preparedHeaders, body ?? string.Empty);

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient
                .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var bodyText = await ReadContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers)
                responseHeaders[h.Key] = string.Join(", ", h.Value);
            foreach (var h in response.Content.Headers)
                responseHeaders[h.Key] = string.Join(", ", h.Value);

            return new HttpAiOutboundResult
            {
                EntryId = entryId,
                Status = (int)response.StatusCode,
                StatusText = response.ReasonPhrase ?? string.Empty,
                Url = response.RequestMessage?.RequestUri?.ToString() ?? url.Trim(),
                Method = method,
                LatencyMs = (int)sw.ElapsedMilliseconds,
                ResponseHeaders = responseHeaders,
                ResponseBodyFull = bodyText,
                ResponseBodyChars = bodyText.Length,
                RequestHeadersWire = requestHeadersWire,
                RequestBody = body ?? string.Empty
            };
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return HttpAiOutboundResult.FromException(entryId, ex, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return HttpAiOutboundResult.FromException(entryId, ex, (int)sw.ElapsedMilliseconds);
        }
    }

    private static async Task<string?> BuildHeaderLinesAsync(
        string? headersJson,
        string? cookies,
        Func<string, Task<string?>> resolveBrowserCookieHeaderAsync,
        Uri targetUri)
    {
        var lines = new List<string>();
        var hasCookie = false;
        var hasUserAgent = false;
        var hasHost = false;

        if (!string.IsNullOrWhiteSpace(headersJson))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(headersJson);
            }
            catch
            {
                return null;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var value = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.GetRawText();
                    lines.Add($"{prop.Name}: {value}");

                    if (string.Equals(prop.Name, "Cookie", StringComparison.OrdinalIgnoreCase))
                        hasCookie = true;
                    if (string.Equals(prop.Name, "User-Agent", StringComparison.OrdinalIgnoreCase))
                        hasUserAgent = true;
                    if (string.Equals(prop.Name, "Host", StringComparison.OrdinalIgnoreCase))
                        hasHost = true;
                }
            }
        }

        if (!hasHost)
            lines.Insert(0, $"Host: {targetUri.Authority}");

        if (!string.IsNullOrWhiteSpace(cookies))
        {
            lines.RemoveAll(l => l.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase));
            lines.Add($"Cookie: {cookies.Trim()}");
            hasCookie = true;
        }
        else if (!hasCookie)
        {
            var cookieHeader = await resolveBrowserCookieHeaderAsync(targetUri.ToString()).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
                lines.Add($"Cookie: {cookieHeader}");
        }

        if (!hasUserAgent)
        {
            lines.Add("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        }

        if (!lines.Any(l => l.StartsWith("Accept:", StringComparison.OrdinalIgnoreCase)))
            lines.Add("Accept: */*");

        return string.Join('\n', lines) + "\n";
    }

    private static async Task<string> ReadContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var encoding = ResolveEncoding(content.Headers.ContentType?.CharSet);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192,
            leaveOpen: false);

        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(charset.Trim().Trim('"'));
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    public static string? ParseCookieHeaderFromCdpJson(string? cdpJson)
    {
        if (string.IsNullOrWhiteSpace(cdpJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(cdpJson);
            if (doc.RootElement.TryGetProperty("error", out _))
                return null;

            if (!doc.RootElement.TryGetProperty("cookies", out var cookiesEl)
                || cookiesEl.ValueKind != JsonValueKind.Array)
                return null;

            var parts = new List<string>();
            foreach (var cookie in cookiesEl.EnumerateArray())
            {
                if (!cookie.TryGetProperty("name", out var nameEl)
                    || !cookie.TryGetProperty("value", out var valueEl))
                    continue;

                var name = nameEl.GetString();
                if (string.IsNullOrEmpty(name))
                    continue;

                parts.Add($"{name}={valueEl.GetString() ?? string.Empty}");
            }

            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
        catch
        {
            return null;
        }
    }
}
