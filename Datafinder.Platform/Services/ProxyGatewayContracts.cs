using System;
using System.Threading;
using System.Threading.Tasks;
using Datafinder.Platform.Models;

namespace Datafinder.Platform.Services;

public sealed record ProxyRuntimeState(
    bool IsRunning,
    string EffectiveMode,
    string? EffectiveEndpoint,
    bool IsDegraded,
    string? Message,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProxyConnectivityResult(
    bool Success,
    string Message,
    long? LatencyMs = null);

public interface IProxyGatewayService
{
    ProxyRuntimeState CurrentState { get; }

    Task<ProxyRuntimeState> StartAsync(ProxySettings settings, CancellationToken cancellationToken = default);
    Task<ProxyRuntimeState> StopAsync(CancellationToken cancellationToken = default);
    Task<ProxyRuntimeState> SwitchAsync(ProxySettings settings, CancellationToken cancellationToken = default);
    Task<ProxyConnectivityResult> TestConnectivityAsync(CancellationToken cancellationToken = default);

    /// <summary>导出 Fluxzy 根 CA 证书（PEM）。</summary>
    byte[]? ExportRootCertificatePem();
}
