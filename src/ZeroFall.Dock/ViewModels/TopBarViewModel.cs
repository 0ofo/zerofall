using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Dock.Controls;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.Dock.ViewModels;

public partial class TopBarViewModel : ViewModelBase
{
    private const string DefaultTitle = "ZeroFall";

    [ObservableProperty]
    private string _windowTitle = DefaultTitle;

    [ObservableProperty]
    private bool _isDarkMode = true;

    [ObservableProperty]
    private bool _isLeftPanelVisible = true;

    [ObservableProperty]
    private bool _isTerminalVisible = true;

    [ObservableProperty]
    private bool _isAiPanelVisible = true;

    [ObservableProperty]
    private Control? _activeContentToolPanel;

    [ObservableProperty]
    private bool _hasActiveContentToolPanel;

    private bool _suppressSync;
    private readonly IMenuRegistry _menuRegistry;
    private readonly IEventBus _eventBus;
    private readonly ISettingsService? _settingsService;

    [ObservableProperty]
    private ObservableCollection<MenuGroupViewModel> _menuGroups = new();

    public TopBarViewModel(IMenuRegistry menuRegistry, IEventBus eventBus, ISettingsService? settingsService = null)
    {
        _menuRegistry = menuRegistry;
        _eventBus = eventBus;
        _settingsService = settingsService;
        SubscribeEvent(eventBus, (ThemeChangedEvent e) => OnThemeChanged(e));
        SubscribeEvent(eventBus, (PanelVisibilityChangedEvent e) => OnPanelVisibilityChanged(e));

        var savedTheme = settingsService?.Load().General.Theme ?? "dark";
        _isDarkMode = savedTheme != "light";
        ApplyTheme(_isDarkMode ? ThemeVariant.Dark : ThemeVariant.Light);
    }

    public TopBarViewModel(string projectName, IMenuRegistry menuRegistry, IEventBus eventBus, ISettingsService? settingsService = null)
        : this(menuRegistry, eventBus, settingsService)
    {
        _windowTitle = $"{DefaultTitle} - {projectName}";
    }

    public void ApplyMenuRegistrations()
    {
        MenuGroups.Clear();

        var menuItems = _menuRegistry.GetItems();
        var grouped = menuItems
            .GroupBy(i => i.MenuPath)
            .Select(g => new
            {
                Header = g.Key,
                Order = g.Min(i => i.MenuGroupOrder),
                Items = g.OrderBy(i => i.Order).ToList()
            })
            .OrderBy(g => g.Order)
            .ToList();

        foreach (var group in grouped)
        {
            var groupVm = new MenuGroupViewModel
            {
                Header = group.Header,
                Order = group.Order
            };

            foreach (var item in group.Items)
            {
                if (item.IsSeparator)
                {
                    groupVm.Items.Add(new MenuItemViewModel { IsSeparator = true, Order = item.Order });
                }
                else
                {
                    groupVm.Items.Add(new MenuItemViewModel
                    {
                        Header = item.Header,
                        Command = item.Command,
                        CommandParameter = item.CommandParameter,
                        IsCheckable = item.IsCheckable,
                        Order = item.Order
                    });
                }
            }

            MenuGroups.Add(groupVm);
        }
    }

    private void OnThemeChanged(ThemeChangedEvent e)
    {
        if (_suppressSync) return;
        _suppressSync = true;
        IsDarkMode = e.Theme != "light";
        _suppressSync = false;
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        if (_suppressSync) return;
        _suppressSync = true;
        ApplyTheme(value ? ThemeVariant.Dark : ThemeVariant.Light);
        _eventBus.Publish(new ThemeChangedEvent(value ? "dark" : "light"));
        SaveTheme(value ? "dark" : "light");
        _suppressSync = false;
    }

    private void SaveTheme(string theme)
    {
        if (_settingsService == null) return;
        try
        {
            var settings = _settingsService.Load();
            settings.General.Theme = theme;
            _settingsService.Save(settings);
        }
        catch
        {
        }
    }

    [RelayCommand]
    private void Exit() => _eventBus.Publish(new ExitRequestedEvent());

    [RelayCommand]
    private void OpenSettings() => _eventBus.Publish(new SettingsRequestedEvent());

    [RelayCommand]
    private void OpenBrowserTab()
    {
        _eventBus.Publish(new WelcomeQuickAccessRequestedEvent());
        _eventBus.Publish(new OpenBrowserTabRequestedEvent(string.Empty, "新标签页"));
    }

    [RelayCommand]
    private void ToggleLeftPanel()
    {
        IsLeftPanelVisible = !IsLeftPanelVisible;
        _eventBus.Publish(new PanelVisibilityChangedEvent(DockPosition.Left, IsLeftPanelVisible));
    }

    [RelayCommand]
    private void ToggleTerminal()
    {
        IsTerminalVisible = !IsTerminalVisible;
        _eventBus.Publish(new TerminalVisibilityChangedEvent(IsTerminalVisible));
    }

    [RelayCommand]
    private void ToggleAiPanel()
    {
        IsAiPanelVisible = !IsAiPanelVisible;
        _eventBus.Publish(new AiPanelVisibilityChangedEvent(IsAiPanelVisible));
    }

    private void OnPanelVisibilityChanged(PanelVisibilityChangedEvent e)
    {
        switch (e.Position)
        {
            case DockPosition.Left:
                IsLeftPanelVisible = e.IsVisible;
                break;
            case DockPosition.Bottom:
                IsTerminalVisible = e.IsVisible;
                break;
            case DockPosition.Right:
                IsAiPanelVisible = e.IsVisible;
                break;
        }
    }

    private static void ApplyTheme(ThemeVariant theme)
    {
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = theme;
    }

    public void SetActiveContentToolPanel(Control? toolPanel)
    {
        if (ReferenceEquals(ActiveContentToolPanel, toolPanel))
        {
            HasActiveContentToolPanel = toolPanel is not null;
            return;
        }

        if (ActiveContentToolPanel is not null)
            DockControlHost.DetachFromVisualTree(ActiveContentToolPanel);

        if (toolPanel is not null)
            DockControlHost.DetachFromVisualTree(toolPanel);

        ActiveContentToolPanel = toolPanel;
        HasActiveContentToolPanel = toolPanel is not null;
    }
}

public partial class MenuGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private int _order;

    [ObservableProperty]
    private ObservableCollection<MenuItemViewModel> _items = new();
}

public partial class MenuItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private bool _isSeparator;

    [ObservableProperty]
    private bool _isCheckable;

    [ObservableProperty]
    private System.Windows.Input.ICommand? _command;

    [ObservableProperty]
    private object? _commandParameter;

    [ObservableProperty]
    private int _order;
}
