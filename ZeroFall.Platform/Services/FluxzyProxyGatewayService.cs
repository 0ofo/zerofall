using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Platform.Models;

namespace ZeroFall.Platform.Services;

/// <summary>
/// 统一代理网关服务：Fluxzy 本地 MITM 监听 + 出站代理模式解析。
/// </summary>
public sealed class FluxzyProxyGatewayService : IProxyGatewayService, IAsyncDisposable
{
    private readonly FluxzyMitmProxyHost _mitmHost;
    private ProxyRuntimeState _state = new(
        IsRunning: false,
        EffectiveMode: ProxyModes.Direct,
        EffectiveEndpoint: null,
        IsDegraded: false,
        Message: "Proxy gateway is not started.",
        UpdatedAtUtc: DateTimeOffset.UtcNow);

    public FluxzyProxyGatewayService(FluxzyMitmProxyHost mitmHost)
    {
        _mitmHost = mitmHost;
    }

    public ProxyRuntimeState CurrentState => _state;

    public Task<ProxyRuntimeState> StartAsync(ProxySettings settings, CancellationToken cancellationToken = default) =>
        SwitchAsync(settings, cancellationToken);

    public async Task<ProxyRuntimeState> StopAsync(CancellationToken cancellationToken = default)
    {
        await _mitmHost.StopAsync(cancellationToken).ConfigureAwait(false);
        _state = new ProxyRuntimeState(
            IsRunning: false,
            EffectiveMode: ProxyModes.Direct,
            EffectiveEndpoint: null,
            IsDegraded: false,
            Message: "Proxy gateway stopped.",
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        return _state;
    }

    public async Task<ProxyRuntimeState> SwitchAsync(ProxySettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mode = string.IsNullOrWhiteSpace(settings.Mode) ? ProxyModes.Direct : settings.Mode.Trim();
        var shouldListen = settings.ListenerEnabled
            || string.Equals(mode, ProxyModes.FluxzyGateway, StringComparison.Ordinal);

        if (shouldListen)
        {
            try
            {
                await _mitmHost.StartAsync(settings, cancellationToken).ConfigureAwait(false);
                var endpoint = BuildEndpoint(settings.GatewayHost, settings.GatewayPort);
                _state = new ProxyRuntimeState(
                    IsRunning: true,
                    EffectiveMode: ProxyModes.FluxzyGateway,
                    EffectiveEndpoint: endpoint,
                    IsDegraded: false,
                    Message: settings.HttpsInterceptionEnabled
                        ? "Fluxzy 拦截代理运行中（HTTPS 解密已启用）。"
                        : "Fluxzy 拦截代理运行中。",
                    UpdatedAtUtc: DateTimeOffset.UtcNow);
                return _state;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FluxzyProxyGatewayService] Start failed: {ex}");
                await StopMitmQuietlyAsync(cancellationToken).ConfigureAwait(false);
                _state = new ProxyRuntimeState(
                    IsRunning: false,
                    EffectiveMode: mode,
                    EffectiveEndpoint: null,
                    IsDegraded: true,
                    Message: $"Fluxzy 代理启动失败: {ex.Message}",
                    UpdatedAtUtc: DateTimeOffset.UtcNow);
                return _state;
            }
        }

        await _mitmHost.StopAsync(cancellationToken).ConfigureAwait(false);

        string? fixedEndpoint = null;
        if (string.Equals(mode, ProxyModes.Fixed, StringComparison.Ordinal))
            fixedEndpoint = string.IsNullOrWhiteSpace(settings.UpstreamProxyUrl) ? null : settings.UpstreamProxyUrl.Trim();

        _state = new ProxyRuntimeState(
            IsRunning: false,
            EffectiveMode: mode,
            EffectiveEndpoint: fixedEndpoint,
            IsDegraded: false,
            Message: $"Proxy mode switched to {mode}.",
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        return _state;
    }

    public async Task<ProxyConnectivityResult> TestConnectivityAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.GetAsync("https://example.com", cts.Token).ConfigureAwait(false);
            sw.Stop();
            if (!response.IsSuccessStatusCode)
                return new ProxyConnectivityResult(false, $"连通性检查失败: HTTP {(int)response.StatusCode}", sw.ElapsedMilliseconds);
            return new ProxyConnectivityResult(true, "连通性检查成功", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProxyConnectivityResult(false, $"连通性检查异常: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    public byte[]? ExportRootCertificatePem() => _mitmHost.ExportRootCertificatePem();

    private static string BuildEndpoint(string host, int port)
    {
        var h = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        var p = port <= 0 || port > 65535 ? 18080 : port;
        return $"http://{h}:{p}";
    }

    private async Task StopMitmQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _mitmHost.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception stopEx)
        {
            Debug.WriteLine($"[FluxzyProxyGatewayService] Stop after failed start failed: {stopEx}");
        }
    }

    public async ValueTask DisposeAsync() => await _mitmHost.DisposeAsync().ConfigureAwait(false);
}
