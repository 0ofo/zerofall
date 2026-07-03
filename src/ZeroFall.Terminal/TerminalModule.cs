using System;
using ZeroFall.Base.AiTools;
using Microsoft.Extensions.DependencyInjection;
using ZeroFall.Base;
using ZeroFall.Dock.Controls;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using ZeroFall.Terminal.Services;
using ZeroFall.Terminal.Tools;
using ZeroFall.Terminal.ViewModels;
using ZeroFall.Terminal.Views;

namespace ZeroFall.Terminal;

public class TerminalModule : IModule
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<TerminalTranscriptService>();
        services.AddSingleton<ITerminalTranscriptService>(sp => sp.GetRequiredService<TerminalTranscriptService>());
        services.AddSingleton<TerminalScreenService>();
        services.AddSingleton<ITerminalScreenService>(sp => sp.GetRequiredService<TerminalScreenService>());
        services.AddSingleton<TerminalCommandService>();
        services.AddSingleton<ITerminalCommandService>(sp => sp.GetRequiredService<TerminalCommandService>());
        services.AddSingleton<TerminalSessionStateService>();
        services.AddSingleton<ITerminalSessionStateService>(sp => sp.GetRequiredService<TerminalSessionStateService>());
        services.AddSingleton<TerminalAiToolService>();
        services.AddSingleton<IUiLayoutTabExtraProvider, TerminalUiLayoutTabExtraProvider>();
        services.AddTransient<TerminalHostViewModel>();
    }

    public void Initialize(IServiceProvider sp)
    {
        var dockRegistry = sp.GetRequiredService<IDockLayoutRegistry>();
        var aiToolRegistry = sp.GetRequiredService<AiToolRegistry>();
        AiToolRegistration_TerminalAiToolService.Register(aiToolRegistry, sp);

        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom,
            TabId = "terminal",
            Title = "终端",
            IconKey = "SemiIconTerminal",
            CreateTab = () =>
            {
                var hostVm = sp.GetRequiredService<TerminalHostViewModel>();
                hostVm.EnsureInitialSession();
                var icon = IconHelper.GetIcon("SemiIconTerminal");
                var hostView = new TerminalHostView
                {
                    DataContext = hostVm,
                    Tag = hostVm
                };
                return new DockTabItemViewModel
                {
                    Id = "terminal",
                    Title = "终端",
                    Icon = icon,
                    IsClosable = false,
                    Content = TabContent.NonReloadable(hostView)
                };
            }
        });
    }
}
