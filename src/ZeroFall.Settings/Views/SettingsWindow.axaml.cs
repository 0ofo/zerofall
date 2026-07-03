using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ZeroFall.Platform.Registries;
using ZeroFall.Settings.Helpers;
using ZeroFall.Settings.ViewModels;

namespace ZeroFall.Settings.Views;

public partial class SettingsWindow : Window
{
    private ISettingsRegistry? _settingsRegistry;
    private SettingsTabsHost? _tabsHost;

    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    public SettingsWindow(ISettingsRegistry settingsRegistry, SettingsWindowViewModel viewModel) : this()
    {
        _settingsRegistry = settingsRegistry;
        DataContext = viewModel;
    }

    public void SetSettingsRegistry(ISettingsRegistry registry)
    {
        _settingsRegistry = registry;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            BuildTabs(vm);
    }

    private void BuildTabs(SettingsWindowViewModel vm)
    {
        if (SettingsTabs == null)
            return;

        var registry = _settingsRegistry;
        if (registry == null)
            return;

        _tabsHost?.Dispose();
        _tabsHost = new SettingsTabsHost(SettingsTabs, registry);
        _tabsHost.Build(vm.TargetTabTitle);
    }

    public bool SaveAll()
    {
        if (DataContext is not SettingsWindowViewModel vm)
            return false;

        if (_tabsHost == null)
            return false;

        if (_tabsHost.TrySaveSelected(vm))
        {
            Close();
            return true;
        }

        return false;
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        SaveAll();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DataContextChanged -= OnDataContextChanged;
        Closed -= OnClosed;

        _tabsHost?.Dispose();
        _tabsHost = null;

        if (DataContext is IDisposable disposable)
            disposable.Dispose();

        DataContext = null;
    }
}
