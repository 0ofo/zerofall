using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Browser.Views;

public partial class WebsiteTreeView : UserControl, IDockTabToolPanelProvider
{
    private TreeView? _treeView;
    private TrafficHttpDetailFlyoutHost? _httpDetailFlyoutHost;
    private WebsiteTreeNodeViewModel? _contextMenuNode;
    private StackPanel? _dockToolPanel;

    public WebsiteTreeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public TrafficMonitorTabViewModel? TrafficMonitor { get; set; }

    public Control? GetDockTabToolPanel()
    {
        _dockToolPanel ??= CreateDockToolPanel();
        _dockToolPanel.DataContext = DataContext;
        return _dockToolPanel;
    }

    private static StackPanel CreateDockToolPanel()
    {
        var panel = DockTabToolPanelHelper.CreateHorizontalPanel();
        DockTabToolPanelHelper.AddIconCommandButton(
            panel,
            "SemiIconDelete",
            nameof(WebsiteTreeViewModel.ClearCommand),
            tooltip: "清空网站树");
        return panel;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _treeView = this.FindControl<TreeView>("WebsiteTree");
        if (_treeView is null || TrafficMonitor is null)
            return;

        _httpDetailFlyoutHost = new TrafficHttpDetailFlyoutHost(TrafficMonitor);
        _treeView.AddHandler(TreeViewItem.ExpandedEvent, OnTreeItemExpanded, RoutingStrategies.Bubble);
        _treeView.AddHandler(InputElement.PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_treeView is not null)
        {
            _treeView.RemoveHandler(TreeViewItem.ExpandedEvent, OnTreeItemExpanded);
            _treeView.RemoveHandler(InputElement.PointerPressedEvent, OnTreePointerPressed);
        }

        _httpDetailFlyoutHost?.Hide();
        _httpDetailFlyoutHost = null;
        _treeView = null;
        _contextMenuNode = null;
    }

    private void OnTreeItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem { DataContext: WebsiteTreeNodeViewModel node })
            return;

        if (DataContext is not WebsiteTreeViewModel vm)
            return;

        if (node.NodeType == WebsiteTreeNodeType.Site && node.Children.Count == 0)
            vm.NotifyDisplayNodeExpanding(node);
    }

    private async void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TrafficMonitor is null)
            return;

        var point = e.GetCurrentPoint(this);
        if (e.Source is not Visual visual)
            return;

        if (!TryGetTreeNode(visual, out var node))
            return;

        if (point.Properties.IsRightButtonPressed)
        {
            _contextMenuNode = node;
            if (DataContext is WebsiteTreeViewModel treeVm)
                treeVm.SelectedNode = node;

            if (node.NodeType == WebsiteTreeNodeType.Request && !string.IsNullOrWhiteSpace(node.EntryId))
                await TrafficMonitor.SelectEntryByIdAsync(node.EntryId);

            _httpDetailFlyoutHost?.Hide();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || _httpDetailFlyoutHost is null)
            return;

        if (node.NodeType != WebsiteTreeNodeType.Request || string.IsNullOrWhiteSpace(node.EntryId))
        {
            _httpDetailFlyoutHost.Hide();
            return;
        }

        if (DataContext is WebsiteTreeViewModel treeVm2)
            treeVm2.SelectedNode = node;

        await TrafficMonitor.SelectEntryByIdAsync(node.EntryId);
        _httpDetailFlyoutHost.Sync(this, showAtPointer: true);
    }

    private void WebsiteTree_ContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu cm || TrafficMonitor is null)
            return;

        var copyUrl = FindMenuItem(cm, "复制 URL");
        var replay = FindMenuItem(cm, "发送到 HTTP 重放");
        var probe = FindMenuItem(cm, "主动探测技术栈");
        var toggleProbe = FindMenuItem(cm, "开启主动探测辅助");
        var addScope = FindMenuItem(cm, "添加到目标空间");
        var removeScope = FindMenuItem(cm, "从目标空间移除");
        if (copyUrl is null || replay is null || probe is null || toggleProbe is null || addScope is null || removeScope is null)
            return;

        var isSite = _contextMenuNode?.NodeType == WebsiteTreeNodeType.Site;
        probe.IsEnabled = isSite;
        toggleProbe.IsEnabled = isSite;
        if (DataContext is WebsiteTreeViewModel treeVmProbe && isSite)
            toggleProbe.Header = treeVmProbe.IsActiveProbeEnabledForNode(_contextMenuNode)
                ? "关闭主动探测辅助"
                : "开启主动探测辅助";

        var entryId = _contextMenuNode?.EntryId;
        var hasRequest = _contextMenuNode?.NodeType == WebsiteTreeNodeType.Request
                         && !string.IsNullOrWhiteSpace(entryId);
        var url = hasRequest ? TrafficMonitor.GetEntryUrl(entryId!) : null;
        var hasUrl = !string.IsNullOrWhiteSpace(url);

        copyUrl.IsEnabled = hasUrl;
        replay.IsEnabled = hasRequest && TrafficMonitor.FindEntryById(entryId!) is not null;

        var canAddScope = DataContext is WebsiteTreeViewModel treeVm
            && _contextMenuNode?.NodeType is WebsiteTreeNodeType.Site or WebsiteTreeNodeType.Request;
        addScope.IsEnabled = canAddScope;
        removeScope.IsEnabled = _contextMenuNode?.NodeType == WebsiteTreeNodeType.ScopeHost;
    }

    private async void WebsiteTreeMenu_CopyUrl(object? sender, RoutedEventArgs e)
    {
        if (TrafficMonitor is null || _contextMenuNode is null)
            return;

        var url = TrafficMonitor.GetEntryUrl(_contextMenuNode.EntryId);
        if (string.IsNullOrWhiteSpace(url))
            return;

        var clipboard = ResolveClipboard(sender);
        if (clipboard is null)
            return;

        await clipboard.SetTextAsync(url);
        await clipboard.FlushAsync();
    }

    private void WebsiteTreeMenu_SendReplay(object? sender, RoutedEventArgs e)
    {
        if (TrafficMonitor is null || _contextMenuNode is null)
            return;

        if (string.IsNullOrWhiteSpace(_contextMenuNode.EntryId))
            return;

        TrafficMonitor.SendEntryToReplayById(_contextMenuNode.EntryId);
    }

    private void WebsiteTreeMenu_AddToTargetScope(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WebsiteTreeViewModel treeVm || _contextMenuNode is null)
            return;

        treeVm.TryAddNodeToTargetScope(_contextMenuNode);
    }

    private void WebsiteTreeMenu_RemoveFromTargetScope(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WebsiteTreeViewModel treeVm || _contextMenuNode is null)
            return;

        treeVm.TryRemoveScopeHost(_contextMenuNode);
    }

    private async void WebsiteTreeMenu_ProbeTechnologies(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WebsiteTreeViewModel treeVm || _contextMenuNode is null)
            return;

        if (treeVm.ProbeSiteTechnologiesCommand.CanExecute(_contextMenuNode))
            await treeVm.ProbeSiteTechnologiesCommand.ExecuteAsync(_contextMenuNode);
    }

    private void WebsiteTreeMenu_ToggleActiveProbe(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WebsiteTreeViewModel treeVm || _contextMenuNode is null)
            return;

        if (treeVm.ToggleActiveProbeAssistCommand.CanExecute(_contextMenuNode))
            treeVm.ToggleActiveProbeAssistCommand.Execute(_contextMenuNode);
    }

    private static bool TryGetTreeNode(Visual visual, out WebsiteTreeNodeViewModel node)
    {
        node = null!;
        foreach (var item in visual.GetVisualAncestors())
        {
            if (item is TreeViewItem { DataContext: WebsiteTreeNodeViewModel n })
            {
                node = n;
                return true;
            }
        }

        return false;
    }

    private static MenuItem? FindMenuItem(ContextMenu cm, string header)
    {
        foreach (var item in cm.Items)
        {
            if (item is MenuItem mi && string.Equals(mi.Header?.ToString(), header, StringComparison.Ordinal))
                return mi;
        }

        return null;
    }

    private IClipboard? ResolveClipboard(object? eventSource)
    {
        if (TopLevel.GetTopLevel(this) is { Clipboard: { } rootClip })
            return rootClip;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { Clipboard: { } mwClip })
            return mwClip;

        if (eventSource is MenuItem mi)
        {
            foreach (var v in mi.GetVisualAncestors())
            {
                if (v is Visual vis && TopLevel.GetTopLevel(vis) is { Clipboard: { } c })
                    return c;
            }
        }

        return null;
    }
}
