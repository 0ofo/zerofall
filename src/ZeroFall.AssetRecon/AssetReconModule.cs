using System;
using Avalonia.Threading;
using ZeroFall.AssetRecon.Services;
using ZeroFall.AssetRecon.Tools;
using ZeroFall.AssetRecon.ViewModels;
using ZeroFall.AssetRecon.Views;
using ZeroFall.Base;
using ZeroFall.Base.AiTools;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroFall.AssetRecon;

public class AssetReconModule : IModule
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<AssetReconViewModel>();
        services.AddSingleton<AssetReconLeftPanelViewModel>();
        services.AddSingleton<AssetReconPanelHostViewModel>();
        services.AddSingleton<PortScanViewModel>();
        services.AddSingleton<PortScanTargetDockCoordinator>();
        services.AddSingleton<AssetReconQuotaClient>();
        services.AddSingleton<AssetReconQueryService>();
        services.AddSingleton<AssetReconResultDockPresenter>();
        services.AddSingleton<AssetReconAiToolService>();
    }

    public void Initialize(IServiceProvider sp)
    {
        var dockRegistry = sp.GetRequiredService<IDockLayoutRegistry>();
        var aiToolRegistry = sp.GetRequiredService<AiToolRegistry>();
        _ = sp.GetRequiredService<PortScanTargetDockCoordinator>();
        _ = sp.GetRequiredService<AssetReconResultDockPresenter>();
        AiToolRegistration_AssetReconAiToolService.Register(aiToolRegistry, sp);

        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Left,
            TabId = "asset-recon-left",
            Title = "侦察面板",
            IconKey = "SemiIconSearch",
            IsDefaultVisible = true,
            CreateTab = () =>
            {
                var host = sp.GetRequiredService<AssetReconPanelHostViewModel>();
                var icon = IconHelper.GetIcon("SemiIconSearch");

                return new DockTabItemViewModel
                {
                    Id = "asset-recon-left",
                    Title = "侦察面板",
                    Icon = icon,
                    Content = new AssetReconLeftPanelView { DataContext = host },
                    IsClosable = false
                };
            }
        });

        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Content,
            TabId = "port-scan",
            Title = "端口扫描",
            IconKey = "SemiIconSignal",
            IsDefaultVisible = false,
            CreateTab = () =>
            {
                var vm = sp.GetRequiredService<PortScanViewModel>();
                var icon = IconHelper.GetIcon("SemiIconSignal");

                return new DockTabItemViewModel
                {
                    Id = "port-scan",
                    Title = "端口扫描",
                    Icon = icon,
                    Content = new PortScanView { DataContext = vm }
                };
            }
        });
    }
}
