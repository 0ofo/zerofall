using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Dock.ViewModels;

public partial class DockPanelViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<DockTabItemViewModel> _tabs = new();

    [ObservableProperty]
    private DockTabItemViewModel? _selectedTab;

    [ObservableProperty]
    private bool _isVisible = true;

    partial void OnIsVisibleChanged(bool value)
    {
        // 底部终端区只用 MainWindow 行高折叠，禁止 IsVisible=false（见 doc/pitfalls.md）
        if (Position == DockPosition.Bottom)
            return;

        _eventBus?.Publish(new PanelVisibilityChangedEvent(Position, value));
    }

    public DockPosition Position { get; }

    private readonly IEventBus? _eventBus;

    public IRelayCommand<DockTabItemViewModel?> CloseTabCommand { get; }

    public DockPanelViewModel(DockPosition position, IEventBus? eventBus = null)
    {
        Position = position;
        _eventBus = eventBus;
        CloseTabCommand = new RelayCommand<DockTabItemViewModel?>(CloseTab);
    }

    public void AddTab(string id, string title, object? content = null, StreamGeometry? icon = null)
    {
        var existing = Tabs.FirstOrDefault(t => t.Id == id);
        if (existing != null)
        {
            SelectedTab = existing;
            return;
        }

        var tab = new DockTabItemViewModel
        {
            Id = id,
            Title = title,
            Content = content,
            Icon = icon
        };

        Tabs.Add(tab);
        SelectedTab = tab;
    }

    public void RemoveTab(string id)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == id);
        if (tab != null)
        {
            Tabs.Remove(tab);
            if (SelectedTab == tab)
                SelectedTab = Tabs.Count > 0 ? Tabs[0] : null;
        }
    }

    private void CloseTab(DockTabItemViewModel? tab)
    {
        if (tab == null) return;
        _eventBus?.Publish(new TabClosedEvent(tab));
        if (Position == DockPosition.Content)
            return;

        Tabs.Remove(tab);
        if (SelectedTab == tab)
            SelectedTab = Tabs.Count > 0 ? Tabs[0] : null;
    }

    [RelayCommand]
    private void ClosePanel()
    {
        if (Position == DockPosition.Bottom)
        {
            _eventBus?.Publish(new TerminalVisibilityChangedEvent(false));
            return;
        }

        IsVisible = false;
    }
}
