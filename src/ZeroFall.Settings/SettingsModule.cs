using System;
using Microsoft.Extensions.DependencyInjection;
using ZeroFall.Base;
using ZeroFall.Platform.Registries;
using ZeroFall.Settings.ViewModels;
using ZeroFall.Settings.Views.Settings;

namespace ZeroFall.Settings;

public class SettingsModule : IModule
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<GeneralSettingsViewModel>();
        services.AddTransient<ProxySettingsViewModel>();
        services.AddTransient<AiSettingsViewModel>();
        services.AddTransient<McpSettingsViewModel>();
        services.AddTransient<TerminalSettingsViewModel>();
        services.AddTransient<AssetReconSettingsViewModel>();
    }

    public void Initialize(IServiceProvider sp)
    {
        var settingsRegistry = sp.GetRequiredService<ISettingsRegistry>();

        settingsRegistry.Register(new SettingsPageEntry
        {
            Title = "常规",
            IconKey = "SemiIconSetting",
            Order = 0,
            CreateView = () => new GeneralSettingsView { DataContext = sp.GetRequiredService<GeneralSettingsViewModel>() }
        });

        settingsRegistry.Register(new SettingsPageEntry
        {
            Title = "代理",
            IconKey = "SemiIconServer",
            Order = 1,
            CreateView = () => new ProxySettingsView { DataContext = sp.GetRequiredService<ProxySettingsViewModel>() }
        });

        settingsRegistry.Register(new SettingsPageEntry
        {
            Title = "AI设置",
            IconKey = "SemiIconAIFilled",
            Order = 2,
            CreateView = () => new AiSettingsView { DataContext = sp.GetRequiredService<AiSettingsViewModel>() }
        });

        settingsRegistry.Register(new SettingsPageEntry
        {
            Title = "MCP 服务",
            IconKey = "SemiIconLink",
            Order = 3,
            CreateView = () => new McpSettingsView { DataContext = sp.GetRequiredService<McpSettingsViewModel>() }
        });

        settingsRegistry.Register(new SettingsPageEntry
        {
            Title = "终端",
            IconKey = "SemiIconTerminal",
            Order = 4,
            CreateView = () => new TerminalSettingsView { DataContext = sp.GetRequiredService<TerminalSettingsViewModel>() }
        });

        settingsRegistry.Register(new SettingsPageEntry
        {
            Title = "资产侦察",
            IconKey = "SemiIconGlobeStroked",
            Order = 5,
            CreateView = () => new AssetReconSettingsView { DataContext = sp.GetRequiredService<AssetReconSettingsViewModel>() }
        });

        settingsRegistry.Register(new SettingsPageEntry
        {
            Title = "快捷键",
            IconKey = "SemiIconKey",
            Order = 6,
            CreateView = () => new ShortcutSettingsView()
        });

        settingsRegistry.Register(new SettingsPageEntry
        {
            Title = "关于",
            IconKey = "SemiIconInfoCircle",
            Order = 7,
            CreateView = () => new AboutSettingsView()
        });
    }
}
