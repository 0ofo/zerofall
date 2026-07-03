using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;

namespace ZeroFall.Platform.Services;

/// <summary>
/// 统一代理切换：后台执行网关切换，UI 线程更新 WebView2 选项并通知浏览器重建。
/// </summary>
public sealed class ProxyRuntimeCoordinator
{
    private readonly IProxyGatewayService _proxyGatewayService;
    private readonly IEventBus _eventBus;
    private int _applyVersion;

    public ProxyRuntimeCoordinator(IProxyGatewayService proxyGatewayService, IEventBus eventBus)
    {
        _proxyGatewayService = proxyGatewayService;
        _eventBus = eventBus;
    }

    public void ScheduleApply(ProxySettings settings)
    {
        var version = Interlocked.Increment(ref _applyVersion);
        _ = ApplyCoreAsync(settings, version).ContinueWith(
            task => Debug.WriteLine($"[ProxyRuntimeCoordinator] Scheduled proxy apply failed: {task.Exception}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    public async Task<ProxyRuntimeState> ApplyAsync(ProxySettings settings, CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref _applyVersion);
        return await ApplyCoreAsync(settings, version, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProxyRuntimeState> ApplyCoreAsync(
        ProxySettings settings,
        int version,
        CancellationToken cancellationToken = default)
    {
        ProxyRuntimeState state;
        try
        {
            state = await Task.Run(
                async () => await _proxyGatewayService.SwitchAsync(settings, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProxyRuntimeCoordinator] SwitchAsync failed: {ex}");
            throw;
        }

        if (version != _applyVersion)
            return state;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _applyVersion)
                return;
            WebView2ProxyOptions.SetFromGatewayState(state);
            _eventBus.Publish(new ProxyRuntimeStateChangedEvent(state));
        });

        return state;
    }
}
