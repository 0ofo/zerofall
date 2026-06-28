using System;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroFall.Dock.Services;

public static class UiMenuRegistration
{
    public static void Register(IServiceProvider sp)
    {
        var menuRegistry = sp.GetRequiredService<IMenuRegistry>();
        var commands = sp.GetRequiredService<IUiMenuCommandService>();

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "新建浏览器标签",
            MenuPath = "视图",
            MenuGroupOrder = 2,
            Order = 10,
            CommandId = UiMenuCommandIds.NewBrowser,
            Command = new RelayCommand(() => commands.Execute(UiMenuCommandIds.NewBrowser))
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "新建终端会话",
            MenuPath = "视图",
            MenuGroupOrder = 2,
            Order = 11,
            CommandId = UiMenuCommandIds.NewTerminal,
            Command = new RelayCommand(() => commands.Execute(UiMenuCommandIds.NewTerminal))
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "打开终端面板",
            MenuPath = "视图",
            MenuGroupOrder = 2,
            Order = 12,
            CommandId = UiMenuCommandIds.OpenTerminalPanel,
            Command = new RelayCommand(() => commands.Execute(UiMenuCommandIds.OpenTerminalPanel))
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "打开侦察面板",
            MenuPath = "视图",
            MenuGroupOrder = 2,
            Order = 13,
            CommandId = UiMenuCommandIds.OpenReconPanel,
            Command = new RelayCommand(() => commands.Execute(UiMenuCommandIds.OpenReconPanel))
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "打开端口扫描",
            MenuPath = "视图",
            MenuGroupOrder = 2,
            Order = 14,
            CommandId = UiMenuCommandIds.OpenPortScanPanel,
            Command = new RelayCommand(() => commands.Execute(UiMenuCommandIds.OpenPortScanPanel))
        });

        menuRegistry.Register(new MenuItemEntry
        {
            Header = "打开文件...",
            MenuPath = "文件",
            MenuGroupOrder = 0,
            Order = 3,
            CommandId = UiMenuCommandIds.OpenFile,
            Command = new RelayCommand(() => commands.Execute(UiMenuCommandIds.OpenFile))
        });
    }
}
