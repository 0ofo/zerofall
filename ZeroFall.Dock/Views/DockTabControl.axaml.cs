using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.DataTable.Views;
using ZeroFall.Platform.Registries;
using ZeroFall.Dock.Controls;
using DockPanelViewModel = ZeroFall.Dock.ViewModels.DockPanelViewModel;

namespace ZeroFall.Dock.Views;

public partial class DockTabControl : UserControl
{
    public static readonly StyledProperty<DockPanelViewModel?> PanelProperty =
        AvaloniaProperty.Register<DockTabControl, DockPanelViewModel?>(nameof(Panel));

    public static readonly StyledProperty<Avalonia.Controls.Dock> TabStripPlacementProperty =
        AvaloniaProperty.Register<DockTabControl, Avalonia.Controls.Dock>(nameof(TabStripPlacement), Avalonia.Controls.Dock.Top);

    public static readonly StyledProperty<bool> ShowCloseButtonProperty =
        AvaloniaProperty.Register<DockTabControl, bool>(nameof(ShowCloseButton), false);

    public static readonly StyledProperty<bool> SidebarModeProperty =
        AvaloniaProperty.Register<DockTabControl, bool>(nameof(SidebarMode), false);

    public static readonly StyledProperty<bool> IsSidebarContentCollapsedProperty =
        AvaloniaProperty.Register<DockTabControl, bool>(nameof(IsSidebarContentCollapsed), false);

    public static readonly StyledProperty<System.Windows.Input.ICommand?> OpenSettingsCommandProperty =
        AvaloniaProperty.Register<DockTabControl, System.Windows.Input.ICommand?>(nameof(OpenSettingsCommand));

    public static readonly StyledProperty<bool> KeepTabContentAliveProperty =
        AvaloniaProperty.Register<DockTabControl, bool>(nameof(KeepTabContentAlive), false);

    public static readonly StyledProperty<bool> ShowNewBrowserButtonProperty =
        AvaloniaProperty.Register<DockTabControl, bool>(nameof(ShowNewBrowserButton), false);

    public static readonly StyledProperty<System.Windows.Input.ICommand?> NewBrowserTabCommandProperty =
        AvaloniaProperty.Register<DockTabControl, System.Windows.Input.ICommand?>(nameof(NewBrowserTabCommand));

    public bool ShowNewBrowserButton
    {
        get => GetValue(ShowNewBrowserButtonProperty);
        set => SetValue(ShowNewBrowserButtonProperty, value);
    }

    public System.Windows.Input.ICommand? NewBrowserTabCommand
    {
        get => GetValue(NewBrowserTabCommandProperty);
        set => SetValue(NewBrowserTabCommandProperty, value);
    }

    public bool KeepTabContentAlive
    {
        get => GetValue(KeepTabContentAliveProperty);
        set => SetValue(KeepTabContentAliveProperty, value);
    }

    public DockPanelViewModel? Panel
    {
        get => GetValue(PanelProperty);
        set => SetValue(PanelProperty, value);
    }

    public Avalonia.Controls.Dock TabStripPlacement
    {
        get => GetValue(TabStripPlacementProperty);
        set => SetValue(TabStripPlacementProperty, value);
    }

    public bool ShowCloseButton
    {
        get => GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    public bool SidebarMode
    {
        get => GetValue(SidebarModeProperty);
        set => SetValue(SidebarModeProperty, value);
    }

    public bool IsSidebarContentCollapsed
    {
        get => GetValue(IsSidebarContentCollapsedProperty);
        set => SetValue(IsSidebarContentCollapsedProperty, value);
    }

    public System.Windows.Input.ICommand? OpenSettingsCommand
    {
        get => GetValue(OpenSettingsCommandProperty);
        set => SetValue(OpenSettingsCommandProperty, value);
    }

    private PersistTabControl? _persistTabControl;
    private bool _subscribedToPanel;
    private bool _syncingTabControls;
    private IDataTemplate? _tabContentTemplate;

    private TabControl? ActiveTabControl => SidebarMode ? null : _persistTabControl;

    private Border? _standardLayout;
    private Border? _sidebarLayout;
    private ListBox? _iconStrip;
    private TextBlock? _sidebarTitle;
    private Button? _sidebarCloseButton;
    private ContentControl? _sidebarContent;
    private Grid? _sidebarContentArea;
    private StackPanel? _sidebarToolPanelHost;
    private Control? _activeToolPanel;

    public DockTabControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TabStripPlacementProperty)
        {
            if (_persistTabControl != null)
                _persistTabControl.TabStripPlacement = TabStripPlacement;
        }
        else if (change.Property == ShowCloseButtonProperty)
        {
            if (_sidebarCloseButton != null)
                _sidebarCloseButton.IsVisible = ShowCloseButton;
        }
        else if (change.Property == PanelProperty)
        {
            UnsubscribeFromPanel();
            SyncTabControlItemsSource();
            SubscribeToPanel();
            if (SidebarMode && Panel != null)
                IsSidebarContentCollapsed = !Panel.IsVisible;
            UpdateAll();
        }
        else if (change.Property == SidebarModeProperty)
        {
            UpdateLayoutMode();
        }
        else if (change.Property == ShowNewBrowserButtonProperty
                 || change.Property == NewBrowserTabCommandProperty)
        {
            UpdateTabStripRightContent();
        }
        else if (change.Property == IsSidebarContentCollapsedProperty)
        {
            UpdateSidebarCollapsedState();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _persistTabControl = this.FindControl<PersistTabControl>("PART_PersistTabControl");
        _standardLayout = this.FindControl<Border>("StandardLayout");
        _sidebarLayout = this.FindControl<Border>("SidebarLayout");
        _iconStrip = this.FindControl<ListBox>("PART_IconStrip");
        _sidebarTitle = this.FindControl<TextBlock>("SidebarTitle");
        _sidebarCloseButton = this.FindControl<Button>("SidebarCloseButton");
        _sidebarToolPanelHost = this.FindControl<StackPanel>("SidebarToolPanelHost");
        _sidebarContent = this.FindControl<ContentControl>("PART_SidebarContent");
        _sidebarContentArea = this.FindControl<Grid>("SidebarContentArea");

        if (_persistTabControl != null)
        {
            _tabContentTemplate = _persistTabControl.PersistContentTemplate;
            _persistTabControl.SelectionChanged += OnTabSelectionChanged;
            _persistTabControl.AddHandler(Button.ClickEvent, OnCloseTabClick, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        if (_iconStrip != null)
        {
            _iconStrip.SelectionChanged += OnIconStripSelectionChanged;
            _iconStrip.AddHandler(PointerPressedEvent, OnIconStripPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        SetupSidebarCloseButton();
        UpdateLayoutMode();
        UpdateTabStripRightContent();
        SubscribeToPanel();
        UpdateAll();
    }

    private void UpdateTabStripRightContent()
    {
        DetachActiveToolPanel();

        var toolPanel = ResolveSelectedTabToolPanel();
        _activeToolPanel = toolPanel;

        if (SidebarMode)
        {
            ApplySidebarHeaderTools(toolPanel);
            return;
        }

        if (_persistTabControl is null)
            return;

        var staticAction = CreateStaticTabStripAction();

        if (staticAction is null && toolPanel is null)
        {
            _persistTabControl.TabStripRightContent = null;
            return;
        }

        if (toolPanel is not null)
            DockControlHost.DetachFromVisualTree(toolPanel);

        if (staticAction is null)
        {
            _persistTabControl.TabStripRightContent = toolPanel;
            return;
        }

        if (toolPanel is null)
        {
            _persistTabControl.TabStripRightContent = staticAction;
            return;
        }

        _persistTabControl.TabStripRightContent = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                staticAction,
                toolPanel
            }
        };
    }

    private void ApplySidebarHeaderTools(Control? toolPanel)
    {
        if (_sidebarToolPanelHost is null)
            return;

        _sidebarToolPanelHost.Children.Clear();
        if (toolPanel is null)
            return;

        DockControlHost.DetachFromVisualTree(toolPanel);
        _sidebarToolPanelHost.Children.Add(toolPanel);
    }

    private Control? CreateStaticTabStripAction()
    {
        if (!ShowNewBrowserButton)
            return null;

        var button = new Button
        {
            Classes = { "Small" },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Command = NewBrowserTabCommand
        };
        ToolTip.SetTip(button, "新建浏览器标签页");
        button.Content = new PathIcon
        {
            Data = Application.Current?.FindResource("SemiIconGlobe") as Avalonia.Media.StreamGeometry,
            Width = 16,
            Height = 16
        };
        return button;
    }

    private Control? ResolveSelectedTabToolPanel()
    {
        var content = Panel?.SelectedTab?.Content;
        if (content is IDockTabToolPanelProvider provider)
            return provider.GetDockTabToolPanel();

        if (content is ContentControl { Content: IDockTabToolPanelProvider nestedProvider })
            return nestedProvider.GetDockTabToolPanel();

        return null;
    }

    private void DetachActiveToolPanel()
    {
        if (!SidebarMode && _persistTabControl is not null)
            _persistTabControl.TabStripRightContent = null;

        _sidebarToolPanelHost?.Children.Clear();

        if (_activeToolPanel is not null)
        {
            DockControlHost.DetachFromVisualTree(_activeToolPanel);
            _activeToolPanel = null;
        }
    }

    private void SetupSidebarCloseButton()
    {
        if (_sidebarCloseButton == null) return;

        _sidebarCloseButton.Command = new RelayCommand(() =>
        {
            if (SidebarMode && Panel != null)
                Panel.IsVisible = false;
            else
                Panel?.ClosePanelCommand.Execute(null);
        });

        ToolTip.SetTip(_sidebarCloseButton, "关闭面板");
    }

    private void UpdateSidebarCollapsedState()
    {
        if (_sidebarContentArea == null) return;

        if (IsSidebarContentCollapsed)
            _sidebarContentArea.IsVisible = false;
        else
        {
            _sidebarContentArea.IsVisible = true;
            UpdateSidebarContentVisibility();
        }
    }

    private void OnIconStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!SidebarMode || _iconStrip == null || Panel == null) return;
        if (!IsSidebarContentCollapsed) return;

        var point = e.GetCurrentPoint(_iconStrip);
        if (point.Properties.IsLeftButtonPressed)
        {
            var hitTest = _iconStrip.InputHitTest(e.GetPosition(_iconStrip));
            var listItem = (hitTest as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();

            if (listItem != null && listItem.DataContext is DockTabItemViewModel clickedTab)
            {
                Panel.SelectedTab = clickedTab;
                Panel.IsVisible = true;
                e.Handled = true;
            }
        }
    }

    private void UpdateLayoutMode()
    {
        if (_standardLayout == null || _sidebarLayout == null) return;

        if (SidebarMode)
        {
            _standardLayout.IsVisible = false;
            _sidebarLayout.IsVisible = true;
            if (ActiveTabControl is PersistTabControl persistOff)
            {
                persistOff.PersistContentTemplate = null;
                persistOff.ContentTemplate = null;
            }
            UpdateTabStripRightContent();
        }
        else
        {
            _standardLayout.IsVisible = true;
            _sidebarLayout.IsVisible = false;
            if (ActiveTabControl is PersistTabControl persistOn)
            {
                persistOn.PersistContentTemplate = _tabContentTemplate;
                if (_tabContentTemplate is not null)
                    persistOn.ContentTemplate = _tabContentTemplate;
            }
            UpdateTabStripRightContent();
        }

        SyncTabControlItemsSource();
        UpdateAll();
    }

    private void SubscribeToPanel()
    {
        if (_subscribedToPanel || Panel == null) return;
        Panel.PropertyChanged += OnPanelPropertyChanged;
        Panel.Tabs.CollectionChanged += OnTabsCollectionChanged;
        _subscribedToPanel = true;
    }

    private void UnsubscribeFromPanel()
    {
        if (!_subscribedToPanel || Panel == null) return;
        Panel.PropertyChanged -= OnPanelPropertyChanged;
        Panel.Tabs.CollectionChanged -= OnTabsCollectionChanged;
        _subscribedToPanel = false;
    }

    private void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DockPanelViewModel.SelectedTab))
        {
            if (SidebarMode && Panel?.IsVisible == true)
                IsSidebarContentCollapsed = false;
            SyncTabControlSelectedItem();
            UpdateTabStripRightContent();
            UpdateAll();
            NotifyDataTableInSelectedTab();
        }
        else if (e.PropertyName == nameof(DockPanelViewModel.IsVisible))
        {
            if (SidebarMode && Panel != null)
                IsSidebarContentCollapsed = !Panel.IsVisible;
        }
    }

    private void OnTabsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (SidebarMode)
            UpdateSidebarContentVisibility();
        else
            UpdateTabStripRightContent();
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTabControls)
            return;

        if (sender is TabControl tc && Panel != null && tc.SelectedItem is DockTabItemViewModel tab
            && !ReferenceEquals(Panel.SelectedTab, tab))
        {
            Panel.SelectedTab = tab;
        }

        UpdateTabStripRightContent();
        NotifyDataTableInSelectedTab();
    }

    private void SyncTabControlItemsSource()
    {
        try
        {
            _syncingTabControls = true;

            if (_persistTabControl != null)
            {
                _persistTabControl.ItemsSource = null;
                _persistTabControl.SelectedItem = null;
            }

            if (Panel == null)
                return;

            if (SidebarMode)
                return;

            var active = ActiveTabControl;
            if (active == null)
                return;

            active.ItemsSource = Panel.Tabs;
            active.SelectedItem = Panel.SelectedTab;
        }
        finally
        {
            _syncingTabControls = false;
        }
    }

    private void SyncTabControlSelectedItem()
    {
        if (Panel == null || SidebarMode)
            return;
        var active = ActiveTabControl;
        if (active == null)
            return;
        if (ReferenceEquals(active.SelectedItem, Panel.SelectedTab))
            return;

        try
        {
            _syncingTabControls = true;
            active.SelectedItem = Panel.SelectedTab;
        }
        finally
        {
            _syncingTabControls = false;
        }
    }

    private void NotifyDataTableInSelectedTab()
    {
        // PersistTabControl 切换可见性时已调用 NotifyTabActivated；此处仅合并一次后台刷新，避免 Loaded+Render 双次整表重建列。
        Dispatcher.UIThread.Post(NotifyDataTableInSelectedTabCore, DispatcherPriority.Background);
    }

    private void NotifyDataTableInSelectedTabCore()
    {
        var tab = Panel?.SelectedTab;
        if (tab == null)
            return;

        foreach (var host in FindTableHostsForTab(tab))
            host.NotifyTabActivated();
    }

    private IEnumerable<ITableGridHost> FindTableHostsForTab(DockTabItemViewModel tab)
    {
        var seen = new HashSet<ITableGridHost>();

        void Collect(Control? root)
        {
            if (root is null)
                return;

            while (root is ContentControl { Content: Control inner })
                root = inner;

            if (root is ITableGridHost host)
                seen.Add(host);

            foreach (var nested in root.GetVisualDescendants().OfType<ITableGridHost>())
                seen.Add(nested);
        }

        if (tab.Content is Control contentRoot)
            Collect(contentRoot);

        if (ActiveTabControl is PersistTabControl persist)
        {
            var host = persist.FindContentForItem<Control>(tab);
            Collect(host);
        }
        else if (ActiveTabControl != null)
        {
            foreach (var presenter in ActiveTabControl.GetVisualDescendants().OfType<ContentPresenter>())
            {
                if (!IsPresenterForTab(presenter, tab))
                    continue;
                if (presenter.Child is Control child)
                    Collect(child);
            }
        }

        foreach (var h in seen)
            yield return h;
    }

    private static bool IsPresenterForTab(ContentPresenter presenter, DockTabItemViewModel tab)
    {
        if (presenter.DataContext is DockTabItemViewModel vm && ReferenceEquals(vm, tab))
            return true;
        if (ReferenceEquals(presenter.Content, tab) || ReferenceEquals(presenter.Content, tab.Content))
            return true;
        if (presenter.Child is ContentControl cc && ReferenceEquals(cc.Content, tab.Content))
            return true;
        return false;
    }

    private void OnIconStripSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_iconStrip == null || Panel == null) return;
        if (_iconStrip.SelectedItem is DockTabItemViewModel tab && Panel.SelectedTab != tab)
            Panel.SelectedTab = tab;

        if (SidebarMode && IsSidebarContentCollapsed && !Panel.IsVisible)
            Panel.IsVisible = true;
    }

    private void UpdateAll()
    {
        UpdateSidebarTitle();
        UpdateSidebarContentVisibility();
    }

    private void UpdateSidebarTitle()
    {
        if (_sidebarTitle == null) return;
        _sidebarTitle.Text = Panel?.SelectedTab?.Title ?? string.Empty;
    }

    private void UpdateSidebarContentVisibility()
    {
        if (_sidebarContent == null || Panel == null) return;

        var selectedTab = Panel.SelectedTab;
        if (selectedTab?.Content is Control content)
            content.IsVisible = true;

        DockControlHost.SetContent(_sidebarContent, selectedTab?.Content);
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        var tab = sender switch
        {
            Button { CommandParameter: DockTabItemViewModel vm } => vm,
            Button { Tag: DockTabItemViewModel vm } => vm,
            _ => null
        };
        if (tab is null)
            return;

        e.Handled = true;
        Panel?.CloseTabCommand.Execute(tab);
    }
}
