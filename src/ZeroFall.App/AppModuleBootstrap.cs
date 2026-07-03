using ZeroFall.AiPanel;
using ZeroFall.AssetRecon;
using ZeroFall.Base;
using ZeroFall.Browser;
using ZeroFall.Dock.Services;
using ZeroFall.Settings;
using ZeroFall.Sidebar;
using ZeroFall.SqlEditor;
using ZeroFall.Terminal;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace ZeroFall.App;

internal static class AppModuleBootstrap
{
    internal static IServiceProvider Build()
    {
        var modules = CreateModules();
        var services = new ServiceCollection();
        foreach (var module in modules)
            module.RegisterServices(services);
        services.AddTransient<ViewModels.MainWindowViewModel>();
        var provider = services.BuildServiceProvider();
        foreach (var module in modules)
            module.Initialize(provider);
        return provider;
    }

    private static IModule[] CreateModules() =>
    [
        new CoreModule(),
        new DockModule(),
        new SidebarModule(),
        new AiPanelModule(),
        new TerminalModule(),
        new AssetReconModule(),
        new SettingsModule(),
        new SqlEditorModule(),
        new BrowserModule(),
    ];
}
