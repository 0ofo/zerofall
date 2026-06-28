using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Datafinder.Base.Events;
using Datafinder.Traffic;
using Datafinder.Traffic.Capture;
using Datafinder.Traffic.Ingest;
using Datafinder.Platform.Models;
using Fluxzy;
using Fluxzy.Certificates;
using Fluxzy.Core;
using Fluxzy.Writers;

namespace Datafinder.Platform.Services;

/// <summary>
/// 基于 Fluxzy.Core 的本地 MITM 代理监听；捕获流量经 <see cref="ITrafficCaptureSink"/> 入库（CDP 共存时由去重丢弃）。
/// </summary>
public sealed class FluxzyMitmProxyHost : IAsyncDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ITrafficCaptureSink _captureSink;
    private Proxy? _proxy;
    private readonly object _gate = new();

    public FluxzyMitmProxyHost(IEventBus eventBus, ITrafficCaptureSink captureSink)
    {
        _eventBus = eventBus;
        _captureSink = captureSink;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _proxy is not null;
        }
    }

    public async Task StartAsync(ProxySettings settings, CancellationToken cancellationToken = default)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new NotSupportedException("当前 Native AOT 运行时暂不启用 Fluxzy MITM 监听，已自动降级。");

        await StopAsync(cancellationToken).ConfigureAwait(false);

        var listenHost = ResolveListenAddress(settings.GatewayHost);
        var port = settings.GatewayPort is > 0 and <= 65535 ? settings.GatewayPort : 18080;

        var fluxzySetting = FluxzySetting
            .CreateDefault(listenHost, port)
            .SetSkipGlobalSslDecryption(!settings.HttpsInterceptionEnabled);

        if (settings.BypassHosts is { Count: > 0 })
            fluxzySetting.SetByPassedHosts(settings.BypassHosts.ToArray());

        ProxyReplaceFluxzyConfigurator.ApplyRules(fluxzySetting, settings.ReplaceRules);

        var certCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Datafinder",
            "fluxzy-cert-cache");
        Directory.CreateDirectory(certCacheDir);
        fluxzySetting.SetCertificateCacheDirectory(certCacheDir);

        var proxy = new Proxy(fluxzySetting);
        proxy.Writer.ExchangeUpdated += OnExchangeUpdated;

        var endpoints = await Task.Run(() => proxy.Run(), cancellationToken).ConfigureAwait(false);
        if (endpoints is null || endpoints.Count == 0)
        {
            proxy.Writer.ExchangeUpdated -= OnExchangeUpdated;
            await proxy.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Fluxzy 代理启动失败：未获得监听端点。");
        }

        lock (_gate)
            _proxy = proxy;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Proxy? proxy;
        lock (_gate)
        {
            proxy = _proxy;
            _proxy = null;
        }

        if (proxy is null)
            return;

        proxy.Writer.ExchangeUpdated -= OnExchangeUpdated;
        await proxy.DisposeAsync().ConfigureAwait(false);
    }

    public byte[]? ExportRootCertificatePem()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return null;

        try
        {
            var cert = Certificate.UseDefault().GetX509Certificate();
            using var ms = new MemoryStream();
            cert.ExportToPem(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private void OnExchangeUpdated(object? sender, ExchangeUpdateEventArgs e)
    {
        if (e.UpdateType != ArchiveUpdateType.AfterResponse)
            return;

        try
        {
            var exchange = e.Original;
            var info = e.ExchangeInfo;
            var url = info.FullUrl ?? exchange.FullUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
                return;

            var method = info.Method ?? exchange.Method ?? "GET";
            var statusCode = info.StatusCode > 0
                ? info.StatusCode
                : exchange.StatusCode > 0 ? exchange.StatusCode : (int?)null;

            var requestHeaders = TrafficHttpHeaders.FromWireText(exchange.Request.Header?.ToString());
            var responseHeaders = TrafficHttpHeaders.FromWireText(exchange.Response.Header?.ToString());
            var requestBodyResult = ReadBody(exchange.Request.Body);
            var responseBodyResult = ReadBody(exchange.Response.Body);
            var latencyMs = ResolveLatencyMs(exchange.Metrics);

            var capture = TrafficCaptureRecord.FromProxy(
                Guid.NewGuid().ToString("N"),
                DateTime.Now.ToString("HH:mm:ss.fff"),
                method,
                url,
                statusCode,
                latencyMs,
                requestHeaders,
                responseHeaders,
                requestBodyResult.Text,
                responseBodyResult.Text,
                requestBodyResult.Raw,
                responseBodyResult.Raw);

            PublishCaptureOnUiThread(capture);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FluxzyMitmProxyHost] ExchangeUpdated failed: {ex.Message}");
        }
    }

    private void PublishCaptureOnUiThread(TrafficCaptureRecord capture)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _captureSink.Submit(capture);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _captureSink.Submit(capture);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FluxzyMitmProxyHost] Publish traffic failed: {ex.Message}");
            }
        }, DispatcherPriority.Background);
    }

    private static long? ResolveLatencyMs(ExchangeMetrics? metrics)
    {
        if (metrics is null)
            return null;

        var start = metrics.ReceivedFromProxy;
        if (start == default)
            return null;

        var end = metrics.ResponseBodyEnd != default ? metrics.ResponseBodyEnd
            : metrics.ResponseHeaderEnd != default ? metrics.ResponseHeaderEnd
            : metrics.ResponseHeaderStart;

        if (end == default || end < start)
            return null;

        return (long)(end - start).TotalMilliseconds;
    }

    private static (string Text, byte[]? Raw) ReadBody(Stream? bodyStream)
    {
        if (bodyStream is null || !bodyStream.CanRead)
            return (string.Empty, null);

        try
        {
            if (bodyStream.CanSeek && bodyStream.Length == 0)
                return (string.Empty, null);

            using var ms = new MemoryStream();
            bodyStream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0)
                return (string.Empty, null);

            const int maxBytes = 256 * 1024;
            var truncated = bytes.Length > maxBytes;
            var slice = truncated ? bytes.AsSpan(0, maxBytes).ToArray() : bytes;
            var text = Encoding.UTF8.GetString(slice);
            if (truncated)
                text += $"\n\n--- 响应体超过 {maxBytes / 1024}KB，已截断 ---";

            return (text, slice);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    private static IPAddress ResolveListenAddress(string? host)
    {
        var value = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        if (string.Equals(value, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "*", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Any;

        if (IPAddress.TryParse(value, out var parsed))
            return parsed;

        return IPAddress.Loopback;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
