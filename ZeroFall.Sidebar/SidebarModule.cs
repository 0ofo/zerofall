using System;
using Microsoft.Extensions.DependencyInjection;
using ZeroFall.Base;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using ZeroFall.Sidebar.ViewModels;
using ZeroFall.Sidebar.Views;

namespace ZeroFall.Sidebar;

public class SidebarModule : IModule
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<SidebarViewModel>();
    }

    public void Initialize(IServiceProvider sp)
    {
        var dockRegistry = sp.GetRequiredService<IDockLayoutRegistry>();
        var menuRegistry = sp.GetRequiredService<IMenuRegistry>();
        var eventBus = sp.GetRequiredService<IEventBus>();

        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Left,
            TabId = "sidebar",
            Title = "资源",
            IconKey = "SemiIconFolder",
            CreateTab = () =>
            {
                var sidebarVm = sp.GetRequiredService<SidebarViewModel>();
                var viewEventBus = sp.GetRequiredService<IEventBus>();
                var icon = IconHelper.GetIcon("SemiIconFolder");
                var view = new SidebarView { DataContext = sidebarVm };
                view.SetEventBus(viewEventBus);

                return new DockTabItemViewModel
                {
                    Id = "sidebar",
                    Title = "资源",
                    Icon = icon,
                    Content = view,
                    IsClosable = false
                };
            }
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "打开文件夹...",
            MenuPath = "文件",
            MenuGroupOrder = 0,
            Order = 0,
            CommandId = UiMenuCommandIds.OpenFolder,
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
                eventBus.Publish(new OpenFolderRequestedEvent()))
        });
    }
}
