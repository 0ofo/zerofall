using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroFall.App.ViewModels;
using ZeroFall.App.Views;
using ZeroFall.Base;
using ZeroFall.Dock.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.App;

public class CoreModule : IModule
{
    public const string WorkspaceGuideTabId = "workspace-guide";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddZeroFallCoreServices();
        services.AddSingleton<WorkspaceHomeViewModel>();
    }

    public void Initialize(IServiceProvider sp)
    {
        var menuRegistry = sp.GetRequiredService<IMenuRegistry>();
        var eventBus = sp.GetRequiredService<IEventBus>();
        var settingsService = sp.GetRequiredService<ISettingsService>();
        var proxyCoordinator = sp.GetRequiredService<ProxyRuntimeCoordinator>();
        var dockRegistry = sp.GetRequiredService<IDockLayoutRegistry>();

        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Content,
            TabId = WorkspaceGuideTabId,
            Title = "欢迎",
            IconKey = "SemiIconDesktop",
            IsDefaultVisible = false,
            CreateTab = () =>
            {
                var icon = IconHelper.GetIcon("SemiIconDesktop");
                var vm = sp.GetRequiredService<WorkspaceHomeViewModel>();
                return new DockTabItemViewModel
                {
                    Id = WorkspaceGuideTabId,
                    Title = "欢迎",
                    Icon = icon,
                    Content = new WorkspaceHomeView { DataContext = vm },
                    IsClosable = true
                };
            }
        });

        var proxySettings = settingsService.Load().Proxy;
        EnsureProxyGatewayForOpenSource(settingsService, ref proxySettings);
        StartupPerformance.RunAfterDelay(
            () => proxyCoordinator.ScheduleApply(proxySettings),
            delayMs: 2000);
        eventBus.Subscribe<ProxySettingsChangedEvent>(e => proxyCoordinator.ScheduleApply(e.Settings));

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "退出",
            MenuPath = "文件",
            MenuGroupOrder = 0,
            Order = 100,
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
                eventBus.Publish(new ExitRequestedEvent()))
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "撤销",
            MenuPath = "编辑",
            MenuGroupOrder = 1,
            Order = 0
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "重做",
            MenuPath = "编辑",
            MenuGroupOrder = 1,
            Order = 1
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "终端",
            MenuPath = "视图",
            MenuGroupOrder = 2,
            Order = 0,
            CommandId = UiMenuCommandIds.OpenTerminalPanel,
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
                sp.GetRequiredService<IUiMenuCommandService>().Execute(UiMenuCommandIds.OpenTerminalPanel))
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "AI 面板",
            MenuPath = "视图",
            MenuGroupOrder = 2,
            Order = 1
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "",
            IsSeparator = true,
            MenuPath = "视图",
            MenuGroupOrder = 2,
            Order = 2
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "刷新",
            MenuPath = "视图",
            MenuGroupOrder = 2,
            Order = 3
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "设置",
            MenuPath = "帮助",
            MenuGroupOrder = 99,
            Order = 0,
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
                eventBus.Publish(new SettingsRequestedEvent()))
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "关于",
            MenuPath = "帮助",
            MenuGroupOrder = 99,
            Order = 1
        });
    }

    /// <summary>浏览器流量走 Fluxzy 代理截获，启动时确保网关模式已启用。</summary>
    private static void EnsureProxyGatewayForOpenSource(ISettingsService settingsService, ref ProxySettings proxySettings)
    {
        var settings = settingsService.Load();
        var changed = false;

        if (!string.Equals(settings.Proxy.Mode, ProxyModes.FluxzyGateway, StringComparison.Ordinal))
        {
            settings.Proxy.Mode = ProxyModes.FluxzyGateway;
            changed = true;
        }

        if (!settings.Proxy.ListenerEnabled)
        {
            settings.Proxy.ListenerEnabled = true;
            changed = true;
        }

        if (changed)
            settingsService.Save(settings);

        proxySettings = settings.Proxy;
    }
}
