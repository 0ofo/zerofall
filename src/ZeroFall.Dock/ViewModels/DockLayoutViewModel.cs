using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Dock.Services;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.Dock.ViewModels;

public partial class DockLayoutViewModel : ViewModelBase
{
    [ObservableProperty]
    private TopBarViewModel _topBar;

    [ObservableProperty]
    private DockPanelViewModel _leftPanel;

    [ObservableProperty]
    private ContentPanelViewModel _contentPanel;

    [ObservableProperty]
    private DockPanelViewModel _rightPanel;

    [ObservableProperty]
    private DockPanelViewModel _bottomPanel;

    [ObservableProperty]
    private StatusBarViewModel _statusBar;

    private readonly IDockLayoutRegistry _registry;
    private readonly IEventBus _eventBus;
    private readonly ISettingsService? _settingsService;
    private readonly Dictionary<string, DockTabRegistration> _lazyTabRegistrations = new(StringComparer.Ordinal);
    private bool _layoutRestored;
    private bool _isNoProjectMode;
    private CancellationTokenSource? _saveLayoutCts;

    public DockLayoutViewModel(IDockLayoutRegistry registry, IEventBus eventBus,
        IMenuRegistry menuRegistry, ContentCreationService contentCreation,
        ISettingsService? settingsService = null)
    {
        _registry = registry;
        _eventBus = eventBus;
        _settingsService = settingsService;
        _topBar = new TopBarViewModel(menuRegistry, eventBus, settingsService);
        _leftPanel = new DockPanelViewModel(DockPosition.Left, eventBus);
        _contentPanel = new ContentPanelViewModel(eventBus, contentCreation);
        _rightPanel = new DockPanelViewModel(DockPosition.Right, eventBus);
        _bottomPanel = new DockPanelViewModel(DockPosition.Bottom, eventBus);
        _statusBar = new StatusBarViewModel(eventBus);

        _contentPanel.PropertyChanged += OnContentPanelPropertyChanged;
        _leftPanel.PropertyChanged += OnPanelPropertyChanged;
        _rightPanel.PropertyChanged += OnPanelPropertyChanged;
        _bottomPanel.PropertyChanged += OnPanelPropertyChanged;

        SubscribeEvent(eventBus, (PanelVisibilityChangedEvent e) => OnPanelVisibilityChanged(e));
        SubscribeEvent(eventBus, (AiPanelVisibilityChangedEvent e) => OnAiPanelVisibilityChanged(e));
        SubscribeEvent(eventBus, (TerminalVisibilityChangedEvent e) => OnTerminalVisibilityChanged(e));
        SubscribeEvent(eventBus, (AddContentTabEvent e) => OnAddContentTab(e));
        SubscribeEvent(eventBus, (AddDockTabEvent e) => OnAddDockTab(e));
        SubscribeEvent(eventBus, (RemoveDockTabEvent e) => OnRemoveDockTab(e));
        SubscribeEvent(eventBus, (UpdateDockTabTitleEvent e) => OnUpdateDockTabTitle(e));
        SubscribeEvent(eventBus, (CloseContentTabRequestedEvent e) => OnCloseContentTabRequested(e));
        SubscribeEvent(eventBus, (SwitchDockTabRequestedEvent e) => OnSwitchDockTabRequested(e));
    }

    public void ApplyRegistrations()
    {
        ApplyRegistrationsCore();
    }

    /// <summary>分帧注册 Tab 头；内容按需 <see cref="EnsureTabMaterialized"/>，避免启动时构造全部 View。</summary>
    public async Task ApplyRegistrationsAsync(bool restoreSavedLayout = true, bool materializeSelections = true)
    {
        RegisterAllTabShells();

        for (var i = 0; i < _lazyTabRegistrations.Count; i++)
        {
            if (i % 3 == 2)
                await StartupPerformance.YieldUiFrameAsync();
        }

        if (restoreSavedLayout)
            RestoreLayout();
        else
            SelectFirstTabPerPanel();

        if (materializeSelections && !_isNoProjectMode)
            await MaterializeStartupSelectionsAsync();
    }

    private void RegisterAllTabShells()
    {
        _lazyTabRegistrations.Clear();
        LeftPanel.Tabs.Clear();
        RightPanel.Tabs.Clear();
        BottomPanel.Tabs.Clear();
        ContentPanel.Tabs.Clear();

        foreach (var reg in _registry.GetRegistrations().OrderBy(r => r.Region))
        {
            var panel = GetPanelForRegion(reg.Region);
            if (panel == null)
                continue;

            _lazyTabRegistrations[reg.TabId] = reg;
            if (!reg.IsDefaultVisible)
                continue;

            panel.Tabs.Add(CreateTabShell(reg));
        }

        SelectFirstTabPerPanel();
    }

    private void SelectFirstTabPerPanel()
    {
        if (LeftPanel.Tabs.Count > 0 && LeftPanel.SelectedTab is null)
            LeftPanel.SelectedTab = LeftPanel.Tabs[0];
        if (ContentPanel.Tabs.Count > 0 && ContentPanel.SelectedTab is null)
            ContentPanel.SelectedTab = ContentPanel.Tabs[0];
        if (BottomPanel.Tabs.Count > 0 && BottomPanel.SelectedTab is null)
            BottomPanel.SelectedTab = BottomPanel.Tabs[0];
        if (RightPanel.Tabs.Count > 0 && RightPanel.SelectedTab is null)
            RightPanel.SelectedTab = RightPanel.Tabs[0];
    }

    private static DockTabItemViewModel CreateTabShell(DockTabRegistration reg) =>
        new()
        {
            Id = reg.TabId,
            Title = reg.Title,
            Icon = IconHelper.GetIcon(reg.IconKey ?? "SemiIconFile"),
            IsClosable = reg.IsClosable,
            Content = null
        };

    /// <summary>仅物化启动时可见区域所需的 Tab 内容（Bottom 折叠时跳过终端/流量表）。</summary>
    private async Task MaterializeStartupSelectionsAsync()
    {
        if (LeftPanel.IsVisible)
            EnsureTabMaterialized(LeftPanel.SelectedTab);
        await StartupPerformance.YieldUiFrameAsync();

        if (TopBar.IsTerminalVisible)
            EnsureTabMaterialized(BottomPanel.SelectedTab);

        if (ContentPanel.SelectedTab is { } contentTab)
        {
            EnsureTabMaterialized(contentTab);
            ApplyTabLinkage(contentTab);
            if (LeftPanel.IsVisible)
                EnsureTabMaterialized(LeftPanel.SelectedTab);
        }
        await StartupPerformance.YieldUiFrameAsync();

        if (RightPanel.IsVisible)
            EnsureTabMaterialized(RightPanel.SelectedTab);
    }

    private void ApplyRegistrationsCore()
    {
        RegisterAllTabShells();
        RestoreLayout();
        if (!_isNoProjectMode)
            MaterializeStartupSelections();
    }

    /// <summary>未打开项目：隐藏各 Dock 面板，主区由欢迎页占据。</summary>
    public void EnterNoProjectMode()
    {
        _isNoProjectMode = true;
        LeftPanel.IsVisible = false;
        TopBar.IsLeftPanelVisible = false;
        RightPanel.IsVisible = false;
        TopBar.IsAiPanelVisible = false;
        TopBar.IsTerminalVisible = false;
        ContentPanel.SelectedTab = null;
        SyncShellPanelVisibility();
    }

    /// <summary>打开项目后恢复布局与各面板可见性。</summary>
    public void ExitNoProjectMode()
    {
        _isNoProjectMode = false;
        RestoreLayout();
        MaterializeStartupSelections();
        SyncShellPanelVisibility();
        PublishContentSelectionAndLinkage();
    }

    private void MaterializeStartupSelections()
    {
        if (LeftPanel.IsVisible)
            EnsureTabMaterialized(LeftPanel.SelectedTab);
        if (TopBar.IsTerminalVisible)
            EnsureTabMaterialized(BottomPanel.SelectedTab);
        if (ContentPanel.SelectedTab is { } contentTab)
        {
            EnsureTabMaterialized(contentTab);
            ApplyTabLinkage(contentTab);
            if (LeftPanel.IsVisible)
                EnsureTabMaterialized(LeftPanel.SelectedTab);
        }
        if (RightPanel.IsVisible)
            EnsureTabMaterialized(RightPanel.SelectedTab);
    }

    /// <summary>Dock Tab 全部创建完成后同步 Shell 布局并发布选中与联动事件。</summary>
    public void CompleteStartupLayout()
    {
        SyncShellPanelVisibility();
        PublishContentSelectionAndLinkage();
    }

    /// <summary>将 Left/Right/Bottom 可见性同步到 MainWindow 行列（须在 MainContent 注入后调用）。</summary>
    public void SyncShellPanelVisibility()
    {
        _eventBus.Publish(new PanelVisibilityChangedEvent(DockPosition.Left, LeftPanel.IsVisible));
        _eventBus.Publish(new PanelVisibilityChangedEvent(DockPosition.Right, RightPanel.IsVisible));
        _eventBus.Publish(new PanelVisibilityChangedEvent(DockPosition.Bottom, TopBar.IsTerminalVisible));
    }

    private void PublishContentSelectionAndLinkage()
    {
        if (ContentPanel.SelectedTab is not { } selected)
            return;

        _eventBus.Publish(new ActiveContentTabChangedEvent(selected.Id, selected.Title));
        ApplyTabLinkage(selected);
    }

    private void EnsureTabMaterialized(DockTabItemViewModel? tab)
    {
        if (tab is null || tab.Content is not null)
            return;
        if (!_lazyTabRegistrations.TryGetValue(tab.Id, out var reg))
            return;

        var created = reg.CreateTab();
        tab.Content = created.Content;
        tab.LinkedTabIds = created.LinkedTabIds;
        tab.IsClosable = created.IsClosable;
        if (!string.IsNullOrWhiteSpace(created.Title))
            tab.Title = created.Title;
        if (created.Icon is not null)
            tab.Icon = created.Icon;
    }

    private void RestoreLayout()
    {
        if (_settingsService == null) return;

        var layout = _settingsService.Load().Layout;

        RestorePanelSelection(LeftPanel, layout.LeftSelectedTabId);
        RestorePanelSelection(RightPanel, layout.RightSelectedTabId);
        RestorePanelSelection(BottomPanel, layout.BottomSelectedTabId);
        RestorePanelSelection(ContentPanel, layout.ContentSelectedTabId);

        if (!layout.LeftPanelVisible)
        {
            LeftPanel.IsVisible = false;
            TopBar.IsLeftPanelVisible = false;
            _eventBus.Publish(new PanelVisibilityChangedEvent(DockPosition.Left, false));
        }
        else
        {
            LeftPanel.IsVisible = true;
            TopBar.IsLeftPanelVisible = true;
            _eventBus.Publish(new PanelVisibilityChangedEvent(DockPosition.Left, true));
        }

        if (!layout.RightPanelVisible)
        {
            RightPanel.IsVisible = false;
            TopBar.IsAiPanelVisible = false;
            _eventBus.Publish(new PanelVisibilityChangedEvent(DockPosition.Right, false));
        }
        else
        {
            RightPanel.IsVisible = true;
            TopBar.IsAiPanelVisible = true;
            _eventBus.Publish(new PanelVisibilityChangedEvent(DockPosition.Right, true));
        }

        if (!layout.BottomPanelVisible)
        {
            TopBar.IsTerminalVisible = false;
            _eventBus.Publish(new TerminalVisibilityChangedEvent(false));
        }
        else
        {
            TopBar.IsTerminalVisible = true;
            _eventBus.Publish(new TerminalVisibilityChangedEvent(true));
        }

        _layoutRestored = true;
    }

    private void RestorePanelSelection(DockPanelViewModel panel, string tabId)
    {
        if (panel.Position == DockPosition.Left && tabId == "asset-recon-left")
            tabId = "sidebar";

        if (!string.IsNullOrEmpty(tabId)
            && _lazyTabRegistrations.TryGetValue(tabId, out var reg)
            && !reg.IsDefaultVisible)
        {
            tabId = string.Empty;
        }

        if (!string.IsNullOrEmpty(tabId))
        {
            var tab = panel.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab != null)
            {
                panel.SelectedTab = tab;
                return;
            }
        }

        if (panel.Position == DockPosition.Left)
        {
            var sidebar = panel.Tabs.FirstOrDefault(t => t.Id == "sidebar");
            if (sidebar != null)
                panel.SelectedTab = sidebar;
            return;
        }

        if (panel.Tabs.Count > 0)
            panel.SelectedTab = panel.Tabs[0];
    }

    private DockTabItemViewModel? TryAddOnDemandTabShell(DockPanelViewModel panel, string tabId)
    {
        if (!_lazyTabRegistrations.TryGetValue(tabId, out var reg))
            return null;

        var existing = panel.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (existing != null)
            return existing;

        var shell = CreateTabShell(reg);
        panel.Tabs.Add(shell);
        return shell;
    }

    private void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is DockPanelViewModel panel)
        {
            if (e.PropertyName == nameof(DockPanelViewModel.SelectedTab))
                EnsureTabMaterialized(panel.SelectedTab);
            else if (e.PropertyName == nameof(DockPanelViewModel.IsVisible) && panel.IsVisible)
                EnsureTabMaterialized(panel.SelectedTab);
        }

        if (!_layoutRestored) return;

        if (e.PropertyName == nameof(DockPanelViewModel.SelectedTab) ||
            e.PropertyName == nameof(DockPanelViewModel.IsVisible))
        {
            ScheduleSaveLayout();
        }
    }

    private void ScheduleSaveLayout()
    {
        if (!_layoutRestored || _isNoProjectMode || _settingsService == null)
            return;

        _saveLayoutCts?.Cancel();
        _saveLayoutCts?.Dispose();
        _saveLayoutCts = new CancellationTokenSource();
        var token = _saveLayoutCts.Token;
        _ = DebouncedSaveLayoutAsync(token);
    }

    private async Task DebouncedSaveLayoutAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(450, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Dispatcher.UIThread.Post(SaveLayout, DispatcherPriority.Background);
    }

    private void SaveLayout()
    {
        if (_settingsService == null || _isNoProjectMode) return;
        try
        {
            var settings = _settingsService.Load();
            settings.Layout.LeftPanelVisible = LeftPanel.IsVisible;
            settings.Layout.RightPanelVisible = RightPanel.IsVisible;
            settings.Layout.BottomPanelVisible = TopBar.IsTerminalVisible;
            settings.Layout.LeftSelectedTabId = LeftPanel.SelectedTab?.Id ?? string.Empty;
            settings.Layout.RightSelectedTabId = RightPanel.SelectedTab?.Id ?? string.Empty;
            settings.Layout.BottomSelectedTabId = BottomPanel.SelectedTab?.Id ?? string.Empty;
            settings.Layout.ContentSelectedTabId = ContentPanel.SelectedTab?.Id ?? string.Empty;
            _settingsService.Save(settings);
        }
        catch
        {
        }
    }

    public DockPanelViewModel? GetPanelForRegion(DockPosition region)
    {
        return region switch
        {
            DockPosition.Left => LeftPanel,
            DockPosition.Right => RightPanel,
            DockPosition.Bottom => BottomPanel,
            DockPosition.Content => ContentPanel,
            _ => null
        };
    }

    public UiLayoutSnapshot CaptureLayoutSnapshot(
        UiLayoutMenuItem[] menu,
        IReadOnlyList<IUiLayoutTabExtraProvider>? extraProviders = null)
    {
        extraProviders ??= Array.Empty<IUiLayoutTabExtraProvider>();
        return new UiLayoutSnapshot(
            menu,
            CapturePanelTabs(LeftPanel, DockPosition.Left, extraProviders),
            CapturePanelTabs(ContentPanel, DockPosition.Content, extraProviders),
            CapturePanelTabs(BottomPanel, DockPosition.Bottom, extraProviders),
            CapturePanelTabs(RightPanel, DockPosition.Right, extraProviders));
    }

    private static UiLayoutTabItem[] CapturePanelTabs(
        DockPanelViewModel panel,
        DockPosition region,
        IReadOnlyList<IUiLayoutTabExtraProvider> extraProviders)
    {
        var selectedId = panel.SelectedTab?.Id;
        return panel.Tabs
            .Select(t =>
            {
                JsonElement? extra = null;
                foreach (var provider in extraProviders)
                {
                    if (provider.TryGetExtra(t, region, out var element))
                    {
                        extra = element;
                        break;
                    }
                }

                return new UiLayoutTabItem(t.Id, t.Title, t.Id == selectedId, extra);
            })
            .ToArray();
    }

    private void OnContentPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContentPanel.SelectedTab))
        {
            EnsureTabMaterialized(ContentPanel.SelectedTab);
            if (ContentPanel.SelectedTab is { } selected)
                _eventBus.Publish(new ActiveContentTabChangedEvent(selected.Id, selected.Title));
            ApplyTabLinkage(ContentPanel.SelectedTab);
            if (_layoutRestored)
                ScheduleSaveLayout();
        }
    }

    private void ApplyTabLinkage(DockTabItemViewModel? selectedTab)
    {
        // 不做跨 Dock 区域自动切换 Tab（浏览器/流量/网站树等由用户自行选择）。
        _ = selectedTab;
    }

    private void OnAddContentTab(AddContentTabEvent e)
    {
        var tab = e.Tab;
        var existing = ContentPanel.Tabs.FirstOrDefault(t => t.Id == tab.Id);
        if (existing is not null)
        {
            ContentPanel.SelectedTab = existing;
            return;
        }

        ContentPanel.Tabs.Add(tab);
        ContentPanel.SelectedTab = tab;
    }

    private void OnAddDockTab(AddDockTabEvent e)
    {
        var panel = GetPanelForRegion(e.Region);
        if (panel == null) return;

        var existing = panel.Tabs.FirstOrDefault(t => t.Id == e.Tab.Id);
        if (existing != null)
        {
            if (existing.Content is Control oldContent && oldContent.DataContext is IDisposable dOld)
                dOld.Dispose();
            existing.Title = e.Tab.Title;
            existing.Content = e.Tab.Content;
            existing.Icon = e.Tab.Icon;
            existing.LinkedTabIds = e.Tab.LinkedTabIds;
            existing.IsClosable = e.Tab.IsClosable;
            if (e.Select)
                panel.SelectedTab = existing;
        }
        else
        {
            panel.Tabs.Add(e.Tab);
            if (e.Select)
                panel.SelectedTab = e.Tab;
        }

        panel.IsVisible = true;
        if (_layoutRestored)
            ScheduleSaveLayout();
    }

    private void OnRemoveDockTab(RemoveDockTabEvent e)
    {
        var panel = GetPanelForRegion(e.Region);
        if (panel == null) return;

        var tab = panel.Tabs.FirstOrDefault(t => t.Id == e.TabId);
        if (tab == null) return;

        TabContentLifetime.Release(tab.Content);
        tab.Content = null;
        panel.Tabs.Remove(tab);
        if (ReferenceEquals(panel.SelectedTab, tab))
            panel.SelectedTab = panel.Tabs.FirstOrDefault();

        if (_layoutRestored)
            ScheduleSaveLayout();
    }

    private void OnUpdateDockTabTitle(UpdateDockTabTitleEvent e)
    {
        var panel = GetPanelForRegion(e.Region);
        var tab = panel?.Tabs.FirstOrDefault(t => t.Id == e.TabId);
        if (tab != null)
            tab.Title = e.Title;
    }

    private void OnPanelVisibilityChanged(PanelVisibilityChangedEvent e)
    {
        // Bottom 仅折叠行高，勿改 DockPanel.IsVisible（终端 PTY 需保持可视树挂载）
        if (e.Position == DockPosition.Bottom)
            return;

        var panel = GetPanelForRegion(e.Position);
        if (panel != null)
            panel.IsVisible = e.IsVisible;
    }

    private void OnCloseContentTabRequested(CloseContentTabRequestedEvent e)
    {
        var tab = ContentPanel.Tabs.FirstOrDefault(t => t.Id == e.TabId);
        if (tab == null) return;
        _eventBus.Publish(new TabClosedEvent(tab));
    }

    private void OnSwitchDockTabRequested(SwitchDockTabRequestedEvent e)
    {
        var panel = GetPanelForRegion(e.Position);
        if (panel == null) return;
        var tab = panel.Tabs.FirstOrDefault(t => t.Id == e.TabId)
                  ?? TryAddOnDemandTabShell(panel, e.TabId);
        if (tab == null) return;

        if (tab.Content is null)
            EnsureTabMaterialized(tab);

        if (e.Position == DockPosition.Left)
        {
            panel.IsVisible = true;
            // Sidebar 折叠时 LeftPanel.IsVisible 可能已为 true，须强制展开内容列
            _eventBus.Publish(new PanelVisibilityChangedEvent(DockPosition.Left, true));
        }

        var selectionChanged = !ReferenceEquals(panel.SelectedTab, tab);
        panel.SelectedTab = tab;

        if (e.Position != DockPosition.Left)
            panel.IsVisible = true;

        if (e.Position == DockPosition.Content && !selectionChanged)
        {
            _eventBus.Publish(new ActiveContentTabChangedEvent(tab.Id, tab.Title));
            ApplyTabLinkage(tab);
        }
    }

    private void OnAiPanelVisibilityChanged(AiPanelVisibilityChangedEvent e)
    {
        if (e.IsVisible)
            EnsureTabMaterialized(RightPanel.SelectedTab);

        _eventBus.Publish(new PanelVisibilityChangedEvent(DockPosition.Right, e.IsVisible));
    }

    private void OnTerminalVisibilityChanged(TerminalVisibilityChangedEvent e)
    {
        if (e.IsVisible)
        {
            var terminalTab = BottomPanel.Tabs.FirstOrDefault(t => t.Id == "terminal");
            if (terminalTab is not null)
            {
                BottomPanel.SelectedTab = terminalTab;
                EnsureTabMaterialized(terminalTab);
            }
        }

        _eventBus.Publish(new PanelVisibilityChangedEvent(DockPosition.Bottom, e.IsVisible));
    }
}
