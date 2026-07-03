using System;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ZeroFall.AssetRecon.ViewModels;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;

namespace ZeroFall.AssetRecon.Services;

public sealed class PortScanTargetDockCoordinator : IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly IServiceProvider _services;
    private readonly Action<PortScanTargetHostRequestedEvent> _handler;

    public PortScanTargetDockCoordinator(IEventBus eventBus, IServiceProvider services)
    {
        _eventBus = eventBus;
        _services = services;
        _handler = OnTargetRequested;
        _eventBus.Subscribe(_handler);
    }

    private void OnTargetRequested(PortScanTargetHostRequestedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var portVm = _services.GetRequiredService<PortScanViewModel>();
            portVm.TargetHost = e.Host.Trim();
            _eventBus.Publish(new SwitchDockTabRequestedEvent(DockPosition.Content, "port-scan"));
        });
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe(_handler);
    }
}
