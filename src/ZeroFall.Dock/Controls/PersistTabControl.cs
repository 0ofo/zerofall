using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZeroFall.DataTable.Views;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.Dock.Controls;

/// <summary>
/// 双模式 Tab 内容宿主：
/// <list type="bullet">
/// <item><see cref="TabContentMode.Reloadable"/> — TabControl 原生 <c>PART_SelectedContentHost</c>，切 Tab 卸载/重挂。</item>
/// <item><see cref="TabContentMode.NonReloadable"/> — <see cref="INonReloadableTabShell"/> 占位 + <c>PART_NonReloadableOverlay</c> 叠层保活，切 Tab 仅显隐与回调。</item>
/// </list>
/// </summary>
public class PersistTabControl : TabControl
{
    public const string PartTabStrip = "PART_TabStrip";
    public const string PartTabStripHost = "PART_TabStripHost";
    public const string PartTabStripLayout = "PART_TabStripLayout";
    public const string PartScrollViewer = "PART_ScrollViewer";
    public const string PartTabStripActions = "PART_TabStripActions";
    public const string PartNonReloadableOverlay = "PART_NonReloadableOverlay";
    public const string PartSelectedContentHost = "PART_SelectedContentHost";

    public static readonly StyledProperty<IDataTemplate?> PersistContentTemplateProperty =
        AvaloniaProperty.Register<PersistTabControl, IDataTemplate?>(nameof(PersistContentTemplate));

    public static readonly StyledProperty<IDataTemplate?> TabHeaderTemplateProperty =
        AvaloniaProperty.Register<PersistTabControl, IDataTemplate?>(nameof(TabHeaderTemplate));

    public static readonly StyledProperty<Control?> TabStripRightContentProperty =
        AvaloniaProperty.Register<PersistTabControl, Control?>(nameof(TabStripRightContent));

    public static readonly StyledProperty<bool> IsTabStripVisibleProperty =
        AvaloniaProperty.Register<PersistTabControl, bool>(nameof(IsTabStripVisible), true);

    public IDataTemplate? PersistContentTemplate
    {
        get => GetValue(PersistContentTemplateProperty);
        set => SetValue(PersistContentTemplateProperty, value);
    }

    public IDataTemplate? TabHeaderTemplate
    {
        get => GetValue(TabHeaderTemplateProperty);
        set => SetValue(TabHeaderTemplateProperty, value);
    }

    public Control? TabStripRightContent
    {
        get => GetValue(TabStripRightContentProperty);
        set => SetValue(TabStripRightContentProperty, value);
    }

    public bool IsTabStripVisible
    {
        get => GetValue(IsTabStripVisibleProperty);
        set => SetValue(IsTabStripVisibleProperty, value);
    }

    private TabStrip? _tabStrip;
    private Panel? _tabStripHost;
    private Grid? _tabStripLayout;
    private ScrollViewer? _scrollViewer;
    private ContentPresenter? _tabStripActions;
    private Border? _borderSeparator;
    private Panel? _nonReloadableOverlay;
    private ContentPresenter? _selectedContentHost;
    private INotifyCollectionChanged? _itemsNotify;
    private DockTabItemViewModel? _watchedReloadableTab;
    private PropertyChangedEventHandler? _watchedReloadableTabHandler;
    private bool _syncingSelection;
    private bool _updatingNonReloadableLayer;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachParts();
        base.OnApplyTemplate(e);

        _tabStrip = e.NameScope.Find<TabStrip>(PartTabStrip);
        if (_tabStrip is null)
            _tabStrip = e.NameScope.Find<TabStrip>("PART_TabStrip");
        if (_tabStrip is not null)
            _tabStrip.SelectionChanged += OnTabStripSelectionChanged;

        _tabStripHost = e.NameScope.Find<Panel>(PartTabStripHost);
        _tabStripLayout = e.NameScope.Find<Grid>(PartTabStripLayout);
        _scrollViewer = e.NameScope.Find<ScrollViewer>(PartScrollViewer);
        _tabStripActions = e.NameScope.Find<ContentPresenter>(PartTabStripActions);
        _borderSeparator = e.NameScope.Find<Border>("PART_BorderSeparator");
        _selectedContentHost = e.NameScope.Find<ContentPresenter>(PartSelectedContentHost);
        _nonReloadableOverlay = e.NameScope.Find<Panel>(PartNonReloadableOverlay);

        SyncContentTemplate();
        ApplyTabStripLayout();
        ApplyTabStripVisibility();
        ApplyItemsSourceToTemplateParts();
        HookItemsCollectionChanges();
        SyncTabStripFromSelectedItem();
        PostUpdateNonReloadableLayer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachParts();
        base.OnDetachedFromVisualTree(e);
    }

    private void DetachParts()
    {
        if (_tabStrip is not null)
        {
            _tabStrip.SelectionChanged -= OnTabStripSelectionChanged;
            _tabStrip = null;
        }

        HookItemsCollectionChanges(null);
        UnhookReloadableTabWatcher();
        _tabStripHost = null;
        _tabStripLayout = null;
        _scrollViewer = null;
        _tabStripActions = null;
        _borderSeparator = null;
        _nonReloadableOverlay = null;
        _selectedContentHost = null;
    }

    private void ApplyTabStripLayout()
    {
        if (_tabStripLayout is null || _scrollViewer is null || _tabStripActions is null)
            return;

        var verticalStrip = TabStripPlacement is Avalonia.Controls.Dock.Left or Avalonia.Controls.Dock.Right;

        _tabStripLayout.ColumnDefinitions.Clear();
        _tabStripLayout.RowDefinitions.Clear();

        if (verticalStrip)
        {
            _tabStripLayout.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            _tabStripLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _tabStripLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            Grid.SetRow(_scrollViewer, 0);
            Grid.SetColumn(_scrollViewer, 0);
            Grid.SetRowSpan(_scrollViewer, 1);
            Grid.SetColumnSpan(_scrollViewer, 1);
            Grid.SetRow(_tabStripActions, 1);
            Grid.SetColumn(_tabStripActions, 0);
            Grid.SetColumnSpan(_tabStripActions, 1);

            _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scrollViewer.VerticalContentAlignment = VerticalAlignment.Stretch;

            if (_tabStripHost is not null)
            {
                _tabStripHost.ClearValue(MinWidthProperty);
                _tabStripHost.ClearValue(MaxWidthProperty);
                _tabStripHost.ClearValue(WidthProperty);
                _tabStripHost.MinHeight = 0;
                _tabStripHost.ClearValue(MaxHeightProperty);
            }

            if (_tabStrip is not null)
            {
                _tabStrip.ItemsPanel = VerticalTabItemsPanel;
                _tabStrip.Margin = new Thickness(0, 0, 4, 0);
                _tabStrip.HorizontalAlignment = HorizontalAlignment.Left;
            }

            if (_borderSeparator is not null)
            {
                _borderSeparator.Width = 1;
                _borderSeparator.ClearValue(HeightProperty);
                _borderSeparator.HorizontalAlignment = TabStripPlacement == Avalonia.Controls.Dock.Left
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left;
                _borderSeparator.VerticalAlignment = VerticalAlignment.Stretch;
            }
        }
        else
        {
            _tabStripLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _tabStripLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            Grid.SetRow(_scrollViewer, 0);
            Grid.SetColumn(_scrollViewer, 0);
            Grid.SetRowSpan(_scrollViewer, 1);
            Grid.SetColumnSpan(_scrollViewer, 1);
            Grid.SetRow(_tabStripActions, 0);
            Grid.SetColumn(_tabStripActions, 1);
            Grid.SetColumnSpan(_tabStripActions, 1);

            _scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scrollViewer.VerticalContentAlignment = VerticalAlignment.Center;

            if (_tabStripHost is not null)
            {
                _tabStripHost.ClearValue(MinWidthProperty);
                _tabStripHost.ClearValue(MaxWidthProperty);
                _tabStripHost.ClearValue(WidthProperty);
                _tabStripHost.ClearValue(MinHeightProperty);
                _tabStripHost.ClearValue(MaxHeightProperty);
            }

            if (_tabStrip is not null)
            {
                _tabStrip.ItemsPanel = HorizontalTabItemsPanel;
                _tabStrip.Margin = new Thickness(0, 0, 4, 0);
            }

            if (_borderSeparator is not null)
            {
                _borderSeparator.ClearValue(WidthProperty);
                _borderSeparator.Height = 1;
                _borderSeparator.HorizontalAlignment = HorizontalAlignment.Stretch;
                _borderSeparator.VerticalAlignment = VerticalAlignment.Bottom;
            }
        }
    }

    private void ApplyTabStripVisibility()
    {
        if (_tabStripHost is not null)
            _tabStripHost.IsVisible = IsTabStripVisible;
    }

    private static readonly FuncTemplate<Panel?> HorizontalTabItemsPanel =
        new(() => new StackPanel { Orientation = Orientation.Horizontal });

    private static readonly FuncTemplate<Panel?> VerticalTabItemsPanel =
        new(() => new StackPanel { Orientation = Orientation.Vertical });

    private void OnTabStripSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _tabStrip is null)
            return;

        try
        {
            _syncingSelection = true;
            SelectedItem = _tabStrip.SelectedItem;
        }
        finally
        {
            _syncingSelection = false;
        }

        PostUpdateNonReloadableLayer();
    }

    private void SyncTabStripFromSelectedItem()
    {
        if (_tabStrip is null)
            return;

        try
        {
            _syncingSelection = true;
            if (!ReferenceEquals(_tabStrip.SelectedItem, SelectedItem))
                _tabStrip.SelectedItem = SelectedItem;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void SyncContentTemplate()
    {
        if (PersistContentTemplate is not null)
            ContentTemplate = PersistContentTemplate;
    }

    private void ApplyItemsSourceToTemplateParts()
    {
        var source = ResolveItemsEnumerable();

        if (_tabStrip is not null && !ReferenceEquals(_tabStrip.ItemsSource, source))
            _tabStrip.ItemsSource = source;
    }

    private IEnumerable ResolveItemsEnumerable() => ItemsSource ?? Items;

    private INotifyCollectionChanged? ResolveItemsCollectionNotifier() =>
        ResolveItemsEnumerable() as INotifyCollectionChanged;

    private void HookItemsCollectionChanges(INotifyCollectionChanged? ncc = null)
    {
        ncc ??= ResolveItemsCollectionNotifier();

        if (_itemsNotify is not null)
        {
            _itemsNotify.CollectionChanged -= OnItemsCollectionChanged;
            _itemsNotify = null;
        }

        _itemsNotify = ncc;
        if (_itemsNotify is not null)
            _itemsNotify.CollectionChanged += OnItemsCollectionChanged;
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        PostUpdateNonReloadableLayer();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PersistContentTemplateProperty)
        {
            SyncContentTemplate();
            PostUpdateNonReloadableLayer();
        }

        if (change.Property == SelectedItemProperty)
        {
            SyncTabStripFromSelectedItem();
            PostUpdateNonReloadableLayer();
        }

        if (change.Property == ItemsSourceProperty)
        {
            ApplyItemsSourceToTemplateParts();
            HookItemsCollectionChanges();
            SyncTabStripFromSelectedItem();
            PostUpdateNonReloadableLayer();
        }

        if (change.Property == TabStripPlacementProperty)
            ApplyTabStripLayout();

        if (change.Property == IsTabStripVisibleProperty)
            ApplyTabStripVisibility();
    }

    private void PostUpdateNonReloadableLayer() =>
        Dispatcher.UIThread.Post(UpdateNonReloadableLayer, DispatcherPriority.Loaded);

    private void UpdateNonReloadableLayer()
    {
        if (_updatingNonReloadableLayer)
            return;

        try
        {
            _updatingNonReloadableLayer = true;

            ApplyTabStripVisibility();
            SyncNonReloadableOverlay();
            SyncSelectedContentHost();
            NotifyReloadableTabActivated();
        }
        finally
        {
            _updatingNonReloadableLayer = false;
        }
    }

    /// <summary>
    /// 自定义模板未包含 <c>PART_ItemsPresenter</c>，TabControl 基类无法通过 TabItem 填充选中内容，需手动同步可重载 Tab。
    /// 可重载 Tab 直接将 <see cref="DockTabItemViewModel.Content"/> 挂到宿主，避免模板内嵌 ContentPresenter 在切 Tab 时反复卸挂同一控件导致空白。
    /// </summary>
    private void SyncSelectedContentHost()
    {
        if (_selectedContentHost is null)
            return;

        if (SelectedItem is not DockTabItemViewModel tabVm)
        {
            UnhookReloadableTabWatcher();
            ClearSelectedContentHost();
            return;
        }

        if (tabVm.Content is INonReloadableTabShell)
        {
            UnhookReloadableTabWatcher();
            ClearSelectedContentHost();
            return;
        }

        HookReloadableTabWatcher(tabVm);
        PresentReloadableTabContent(tabVm);
    }

    private void ClearSelectedContentHost()
    {
        if (_selectedContentHost is null)
            return;

        _selectedContentHost.Content = null;
        _selectedContentHost.ContentTemplate = null;
    }

    private void HookReloadableTabWatcher(DockTabItemViewModel tabVm)
    {
        if (ReferenceEquals(_watchedReloadableTab, tabVm))
            return;

        UnhookReloadableTabWatcher();
        _watchedReloadableTab = tabVm;
        _watchedReloadableTabHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(DockTabItemViewModel.Content))
                PostUpdateNonReloadableLayer();
        };
        _watchedReloadableTab.PropertyChanged += _watchedReloadableTabHandler;
    }

    private void UnhookReloadableTabWatcher()
    {
        if (_watchedReloadableTab is not null && _watchedReloadableTabHandler is not null)
            _watchedReloadableTab.PropertyChanged -= _watchedReloadableTabHandler;
        _watchedReloadableTab = null;
        _watchedReloadableTabHandler = null;
    }

    private void PresentReloadableTabContent(DockTabItemViewModel tabVm)
    {
        if (_selectedContentHost is null)
            return;

        if (tabVm.Content is Control control)
        {
            if (ReferenceEquals(_selectedContentHost.Content, control))
                return;

            _selectedContentHost.ContentTemplate = null;
            _selectedContentHost.Content = null;
            DockControlHost.DetachFromVisualTree(control);
            _selectedContentHost.Content = control;
            return;
        }

        if (!ReferenceEquals(_selectedContentHost.Content, tabVm))
        {
            _selectedContentHost.ContentTemplate = PersistContentTemplate ?? ContentTemplate;
            _selectedContentHost.Content = tabVm;
        }
    }

    private void SyncNonReloadableOverlay()
    {
        if (_nonReloadableOverlay is null)
            return;

        var selected = SelectedItem;
        var alive = new HashSet<Control>();
        Control? selectedContent = null;
        INonReloadableTabShell? selectedShell = null;

        if (selected is DockTabItemViewModel selectedTab
            && selectedTab.Content is INonReloadableTabShell selectedTabShell)
        {
            selectedShell = selectedTabShell;
            selectedContent = selectedTabShell.PersistedContent;
        }

        foreach (var item in ResolveItemsEnumerable())
        {
            if (item is not DockTabItemViewModel tabVm)
                continue;
            if (tabVm.Content is not INonReloadableTabShell shell)
                continue;

            var content = shell.PersistedContent;
            if (content is null)
                continue;

            alive.Add(content);
            EnsureChildOfOverlay(content);

            if (ReferenceEquals(content, selectedContent))
            {
                continue;
            }

            content.IsVisible = false;
            content.IsHitTestVisible = false;
            shell.OnTabBecameHidden();
        }

        if (selectedContent is not null)
        {
            EnsureChildOfOverlay(selectedContent);
            selectedContent.IsVisible = true;
            selectedContent.IsHitTestVisible = true;
            if (selectedShell is not null)
                selectedShell.OnTabBecameVisible();
        }

        foreach (var item in ResolveItemsEnumerable())
        {
            if (item is not DockTabItemViewModel tabVm)
                continue;
            if (tabVm.Content is not INonReloadableTabShell shell)
                continue;
            if (ReferenceEquals(shell, selectedShell))
                continue;
            if (ReferenceEquals(shell.PersistedContent, selectedContent))
                shell.OnTabBecameHidden();
        }

        for (var i = _nonReloadableOverlay.Children.Count - 1; i >= 0; i--)
        {
            if (_nonReloadableOverlay.Children[i] is Control child && !alive.Contains(child))
            {
                TabContentLifetime.Release(child);
                _nonReloadableOverlay.Children.RemoveAt(i);
            }
        }
    }

    private void EnsureChildOfOverlay(Control content)
    {
        if (_nonReloadableOverlay is null)
            return;

        if (ReferenceEquals(content.Parent, _nonReloadableOverlay))
            return;

        if (content.Parent is Panel parentPanel)
            parentPanel.Children.Remove(content);
        else if (content.Parent is ContentControl contentControl)
            contentControl.Content = null;
        else if (content.Parent is Decorator decorator)
            decorator.Child = null;

        _nonReloadableOverlay.Children.Add(content);
    }

    private bool _reloadableTabNotifyPosted;

    private void NotifyReloadableTabActivated()
    {
        if (_reloadableTabNotifyPosted)
            return;

        _reloadableTabNotifyPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _reloadableTabNotifyPosted = false;
            NotifyReloadableTabActivatedCore();
        }, DispatcherPriority.Background);
    }

    private void NotifyReloadableTabActivatedCore()
    {
        if (SelectedItem is not DockTabItemViewModel tabVm)
            return;

        if (tabVm.Id.StartsWith("browser-", StringComparison.Ordinal))
            return;

        if (tabVm.Content is INonReloadableTabShell)
            return;

        if (TryNotifyEditorTabActivated(tabVm.Content))
            return;

        if (_selectedContentHost?.Child is Control host)
            NotifyDataTableShown(host);
        else if (tabVm.Content is Control content)
            NotifyDataTableShown(content);
    }

    private static bool TryNotifyEditorTabActivated(object? content)
    {
        if (content is ITableGridHost filePreview
            && content.GetType().Name == "FilePreviewView")
        {
            filePreview.NotifyTabActivated();
            return true;
        }

        return content is not null && content.GetType().Name == "SqlEditorView";
    }

    private static void NotifyDataTableShown(Control host)
    {
        if (host is ITableGridHost gridHost)
        {
            gridHost.NotifyTabActivated();
            return;
        }

        if (host is DataTableView table)
        {
            table.NotifyTabActivated();
            return;
        }

        foreach (var child in host.GetVisualChildren().OfType<Control>())
        {
            if (child is ITableGridHost nestedHost)
            {
                nestedHost.NotifyTabActivated();
                return;
            }

            if (child is DataTableView nestedTable)
            {
                nestedTable.NotifyTabActivated();
                return;
            }
        }
    }

    public Control? FindContentHostForItem(object? item)
    {
        if (item is null)
            return null;

        if (item is DockTabItemViewModel tabVm)
        {
            var resolved = NonReloadableTabContent.Resolve(tabVm.Content);
            if (resolved is not null)
                return resolved;

            if (ReferenceEquals(item, SelectedItem) && _selectedContentHost?.Child is Control live)
                return live;

            return tabVm.Content as Control;
        }

        if (ReferenceEquals(item, SelectedItem) && _selectedContentHost?.Child is Control selectedHost)
            return selectedHost;

        return null;
    }

    public TControl? FindContentForItem<TControl>(object? item) where TControl : Control
    {
        var host = FindContentHostForItem(item);
        if (host is null)
            return null;
        if (host is TControl exact)
            return exact;
        return host.GetVisualDescendants().OfType<TControl>().FirstOrDefault();
    }

    public TControl? FindSelectedContent<TControl>() where TControl : Control =>
        FindContentForItem<TControl>(SelectedItem);
}
