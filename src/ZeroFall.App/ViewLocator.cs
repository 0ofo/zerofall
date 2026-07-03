using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ZeroFall.Base.Mvvm;

namespace ZeroFall;

public class ViewLocator : IDataTemplate
{
    private Dictionary<Type, Func<Control>>? _map;

    private Dictionary<Type, Func<Control>> Map => _map ??= CreateMap();

    private static Dictionary<Type, Func<Control>> CreateMap() => new()
    {
        [typeof(ZeroFall.AiPanel.ViewModels.AiPanelViewModel)] = () => new ZeroFall.AiPanel.Views.AiPanelView(),
        [typeof(ZeroFall.Dock.ViewModels.DockLayoutViewModel)] = () => new ZeroFall.Dock.Views.DockTabControl(),
        [typeof(ZeroFall.DataTable.ViewModels.DataTableViewModel)] = () => new ZeroFall.DataTable.Views.DataTableView(),
        [typeof(ZeroFall.AssetRecon.ViewModels.AssetReconPanelHostViewModel)] = () => new ZeroFall.AssetRecon.Views.AssetReconLeftPanelView(),
        [typeof(App.ViewModels.MainWindowViewModel)] = () => new App.Views.MainContentView(),
        [typeof(ZeroFall.Sidebar.ViewModels.SidebarViewModel)] = () => new ZeroFall.Sidebar.Views.SidebarView(),
        [typeof(ZeroFall.SqlEditor.ViewModels.SqlEditorViewModel)] = () => new ZeroFall.SqlEditor.Views.SqlEditorView(),
        [typeof(ZeroFall.SqlEditor.ViewModels.FilePreviewViewModel)] = () => new ZeroFall.SqlEditor.Views.FilePreviewView(),
        [typeof(ZeroFall.Terminal.ViewModels.TerminalViewModel)] = () => new ZeroFall.Terminal.Views.TerminalView(),
        [typeof(ZeroFall.Dock.ViewModels.TopBarViewModel)] = () => new ZeroFall.Dock.Views.TopBarView(),
        [typeof(ZeroFall.Dock.ViewModels.StatusBarViewModel)] = () => new ZeroFall.Dock.Views.StatusBarView(),
        [typeof(ZeroFall.Settings.ViewModels.GeneralSettingsViewModel)] = () => new ZeroFall.Settings.Views.Settings.GeneralSettingsView(),
        [typeof(ZeroFall.Settings.ViewModels.AiSettingsViewModel)] = () => new ZeroFall.Settings.Views.Settings.AiSettingsView(),
        [typeof(ZeroFall.Settings.ViewModels.TerminalSettingsViewModel)] = () => new ZeroFall.Settings.Views.Settings.TerminalSettingsView(),
        [typeof(ZeroFall.Settings.ViewModels.AssetReconSettingsViewModel)] = () => new ZeroFall.Settings.Views.Settings.AssetReconSettingsView(),
        [typeof(ZeroFall.Settings.ViewModels.SettingsWindowViewModel)] = () => new ZeroFall.Settings.Views.SettingsWindow(),
    };

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var type = param.GetType();
        if (Map.TryGetValue(type, out var factory))
            return factory();

        return new TextBlock { Text = "Not Found: " + type.Name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
