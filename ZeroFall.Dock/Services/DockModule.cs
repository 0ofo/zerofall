using System;
using Microsoft.Extensions.DependencyInjection;
using ZeroFall.Base;
using ZeroFall.Dock.ViewModels;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.Dock.Services;

public class DockModule : IModule
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IDockLayoutRegistry, DockLayoutRegistry>();
        services.AddSingleton<IContentFactoryRegistry, ContentFactoryRegistry>();
        services.AddSingleton<ISettingsRegistry, SettingsRegistry>();
        services.AddSingleton<IMenuRegistry, MenuRegistry>();
        services.AddSingleton<IFileTypeInspector, FileTypeInspector>();
        services.AddSingleton<ContentCreationService>();
        services.AddSingleton<UiLayoutService>();
        services.AddSingleton<IUiLayoutService>(sp => sp.GetRequiredService<UiLayoutService>());
        services.AddSingleton<UiMenuCommandService>();
        services.AddSingleton<IUiMenuCommandService>(sp => sp.GetRequiredService<UiMenuCommandService>());
        services.AddTransient<DockLayoutViewModel>();
    }

    public void Initialize(IServiceProvider sp)
    {
        FileTypeInspector.EnsureInitialized();
        UiMenuRegistration.Register(sp);
    }
}
