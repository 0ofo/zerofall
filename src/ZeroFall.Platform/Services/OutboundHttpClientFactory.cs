using System;
using System.Net;
using System.Net.Http;
using ZeroFall.Platform.Models;

namespace ZeroFall.Platform.Services;

/// <summary>
/// 统一出站 HTTP 客户端工厂：根据统一代理状态创建 HttpClient。
/// </summary>
public sealed class OutboundHttpClientFactory : IOutboundHttpClientFactory
{
    private readonly IProxyGatewayService _proxyGatewayService;
    private readonly ISettingsService _settingsService;

    public OutboundHttpClientFactory(IProxyGatewayService proxyGatewayService, ISettingsService settingsService)
    {
        _proxyGatewayService = proxyGatewayService;
        _settingsService = settingsService;
    }

    public HttpClient CreateClient(string purpose, TimeSpan timeout, bool acceptAnyServerCertificate = false)
    {
        var state = _proxyGatewayService.CurrentState;
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ServerCertificateCustomValidationCallback = acceptAnyServerCertificate
                ? static (_, _, _, _) => true
                : null
        };

        var mode = state.EffectiveMode;
        var endpoint = state.EffectiveEndpoint;
        if (string.IsNullOrWhiteSpace(mode))
            mode = _settingsService.Load().Proxy.Mode;

        if (string.Equals(mode, ProxyModes.Direct, StringComparison.Ordinal))
        {
            handler.UseProxy = false;
        }
        else if (string.Equals(mode, ProxyModes.System, StringComparison.Ordinal))
        {
            handler.UseProxy = true;
            handler.Proxy = null;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = _settingsService.Load().Proxy.UpstreamProxyUrl;
            }

            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(endpoint.Trim());
            }
            else
            {
                handler.UseProxy = false;
            }
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ZeroFall/{purpose}");
        return client;
    }
}
