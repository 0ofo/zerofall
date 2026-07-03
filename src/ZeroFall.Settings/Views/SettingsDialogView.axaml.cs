using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ZeroFall.Platform.Registries;
using ZeroFall.Settings.Helpers;
using ZeroFall.Settings.ViewModels;

namespace ZeroFall.Settings.Views;

public partial class SettingsDialogView : UserControl
{
    private ISettingsRegistry? _settingsRegistry;
    private SettingsTabsHost? _tabsHost;
    private bool _hostHooksAttached;

    public SettingsDialogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public void SetSettingsRegistry(ISettingsRegistry registry)
    {
        _settingsRegistry = registry;
        if (DataContext is SettingsWindowViewModel vm)
            BuildTabs(vm);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachHostWindowHooks();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        DetachHostWindowHooks();
        ReleaseTabs();
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachHostWindowHooks()
    {
        if (_hostHooksAttached)
            return;

        if (TopLevel.GetTopLevel(this) is not Window window)
            return;

        window.Opened += OnHostWindowOpened;
        window.Closing += OnHostWindowClosing;
        window.Closed += OnHostWindowClosed;
        _hostHooksAttached = true;
    }

    private void DetachHostWindowHooks()
    {
        if (!_hostHooksAttached)
            return;

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Opened -= OnHostWindowOpened;
            window.Closing -= OnHostWindowClosing;
            window.Closed -= OnHostWindowClosed;
        }

        _hostHooksAttached = false;
    }

    private void OnHostWindowOpened(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;

        var cancel = window.FindControl<Button>("PART_CancelButton");
        if (cancel != null)
        {
            cancel.Click -= OnCancelButtonClick;
            cancel.Click += OnCancelButtonClick;
        }
    }

    private void OnHostWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            var cancel = window.FindControl<Button>("PART_CancelButton");
            if (cancel != null)
                cancel.Click -= OnCancelButtonClick;
        }

        ReleaseTabs();
    }

    private void OnCancelButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.SkipSaveOnClose = true;
    }

    private void OnHostWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel vm)
            return;
        if (vm.SkipSaveOnClose)
            return;

        if (!SaveAll())
            e.Cancel = true;
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

    private void ReleaseTabs()
    {
        _tabsHost?.Dispose();
        _tabsHost = null;
    }

    private bool SaveAll()
    {
        if (DataContext is not SettingsWindowViewModel vm)
            return false;

        if (_tabsHost == null)
            return false;

        return _tabsHost.TrySaveSelected(vm);
    }
}
