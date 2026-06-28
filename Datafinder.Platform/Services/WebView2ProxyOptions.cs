using System;
using Datafinder.Platform.Models;

namespace Datafinder.Platform.Services;

/// <summary>
/// 内置 WebView2 代理来源于统一代理运行态；修改后需重建 WebView2 环境（由 <c>ProxySettingsChangedEvent</c> 触发各标签页替换控件）。
/// </summary>
public static class WebView2ProxyOptions
{
    private static ProxyRuntimeState? _gatewayState;

    public static void SetFromGatewayState(ProxyRuntimeState state)
    {
        _gatewayState = state;
    }

    /// <summary>返回 Chromium <c>--proxy-server=</c> 的值（不含前缀），无则 null。</summary>
    public static string? Resolve()
    {
        if (_gatewayState is { } gateway)
        {
            if (string.Equals(gateway.EffectiveMode, ProxyModes.Fixed, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(gateway.EffectiveEndpoint))
            {
                return gateway.EffectiveEndpoint.Trim().Trim('"');
            }

            if (string.Equals(gateway.EffectiveMode, ProxyModes.FluxzyGateway, StringComparison.Ordinal)
                && gateway.IsRunning
                && !string.IsNullOrWhiteSpace(gateway.EffectiveEndpoint))
            {
                return gateway.EffectiveEndpoint.Trim().Trim('"');
            }

            if (gateway.EffectiveMode == ProxyModes.Direct)
                return null;
        }
        return null;
    }
}
