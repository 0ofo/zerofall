using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Threading;
using ZeroFall.Base.AiTools;
using ZeroFall.Base.Events;
using ZeroFall.Dock.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Serialization;
using ZeroFall.Platform.Services;

namespace ZeroFall.Dock.Services;

public sealed class UiLayoutService : IUiLayoutService
{
    private static readonly (DockPosition Position, string RegionKey)[] TabSearchOrder =
    [
        (DockPosition.Content, "content"),
        (DockPosition.Left, "sidebar"),
        (DockPosition.Bottom, "bottom"),
        (DockPosition.Right, "right"),
    ];

    private readonly IMenuRegistry _menuRegistry;
    private readonly IUiLayoutTabExtraProvider[] _extraProviders;
    private readonly IEventBus _eventBus;
    private DockLayoutViewModel? _dock;

    public UiLayoutService(
        IMenuRegistry menuRegistry,
        IEnumerable<IUiLayoutTabExtraProvider> extraProviders,
        IEventBus eventBus)
    {
        _menuRegistry = menuRegistry;
        _extraProviders = extraProviders.ToArray();
        _eventBus = eventBus;
    }

    public void Attach(DockLayoutViewModel dock) => _dock = dock;

    public UiLayoutSnapshot GetSnapshot()
    {
        var menu = CaptureMenu();
        if (_dock == null)
        {
            return new UiLayoutSnapshot(menu, [], [], [], []);
        }

        if (Dispatcher.UIThread.CheckAccess())
            return _dock.CaptureLayoutSnapshot(menu, _extraProviders);

        return Dispatcher.UIThread.Invoke(() => _dock!.CaptureLayoutSnapshot(menu, _extraProviders));
    }

    public string GetLayoutJson(UiLayoutQuery query = default)
    {
        if (query.Scope == UiLayoutScope.Tab && string.IsNullOrWhiteSpace(query.TabId))
            return ToolResultJson.Error("Tab Id 不能为空");

        var snapshot = GetSnapshot();
        var tabId = query.TabId?.Trim() ?? string.Empty;

        return query.Scope switch
        {
            UiLayoutScope.All => JsonSerializer.Serialize(snapshot, PlatformJsonContext.Default.UiLayoutSnapshot),
            UiLayoutScope.Menu => JsonSerializer.Serialize(
                new UiLayoutMenuSection(snapshot.Menu),
                PlatformJsonContext.Default.UiLayoutMenuSection),
            UiLayoutScope.Sidebar => JsonSerializer.Serialize(
                new UiLayoutSidebarSection(snapshot.Sidebar),
                PlatformJsonContext.Default.UiLayoutSidebarSection),
            UiLayoutScope.Content => JsonSerializer.Serialize(
                new UiLayoutContentSection(snapshot.Content),
                PlatformJsonContext.Default.UiLayoutContentSection),
            UiLayoutScope.Bottom => JsonSerializer.Serialize(
                new UiLayoutBottomSection(snapshot.Bottom),
                PlatformJsonContext.Default.UiLayoutBottomSection),
            UiLayoutScope.Right => JsonSerializer.Serialize(
                new UiLayoutRightSection(snapshot.Right),
                PlatformJsonContext.Default.UiLayoutRightSection),
            UiLayoutScope.Active => JsonSerializer.Serialize(
                BuildActiveSection(snapshot),
                PlatformJsonContext.Default.UiLayoutActiveSection),
            UiLayoutScope.Tab => SerializeTabFocus(snapshot, tabId),
            _ => JsonSerializer.Serialize(snapshot, PlatformJsonContext.Default.UiLayoutSnapshot)
        };
    }

    public string SwitchTab(string tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
            return ToolResultJson.Error("tabId 不能为空");

        tabId = tabId.Trim();

        if (_dock == null)
            return ToolResultJson.Error("UI 布局尚未就绪");

        if (Dispatcher.UIThread.CheckAccess())
            return SwitchTabCore(tabId);

        return Dispatcher.UIThread.Invoke(() => SwitchTabCore(tabId));
    }

    private static UiLayoutActiveSection BuildActiveSection(UiLayoutSnapshot snapshot)
    {
        UiLayoutTabItem[]? Pick(UiLayoutTabItem[] tabs)
        {
            var selected = tabs.Where(t => t.Selected).ToArray();
            return selected.Length > 0 ? selected : null;
        }

        return new UiLayoutActiveSection
        {
            Sidebar = Pick(snapshot.Sidebar),
            Content = Pick(snapshot.Content),
            Bottom = Pick(snapshot.Bottom),
            Right = Pick(snapshot.Right)
        };
    }

    private static string SerializeTabFocus(UiLayoutSnapshot snapshot, string tabId)
    {
        var focus = FindTab(snapshot, tabId);
        if (focus == null)
        {
            return ToolResultJson.Error(
                $"未找到 Tab「{tabId}」。请先用 get_ui_layout 查看各区域 Tab 的 id，或确认 Tab 已打开。");
        }

        return JsonSerializer.Serialize(focus, PlatformJsonContext.Default.UiLayoutTabFocus);
    }

    private static UiLayoutTabFocus? FindTab(UiLayoutSnapshot snapshot, string tabId)
    {
        foreach (var (region, tabs) in new (string Region, UiLayoutTabItem[] Tabs)[]
                 {
                     ("sidebar", snapshot.Sidebar),
                     ("content", snapshot.Content),
                     ("bottom", snapshot.Bottom),
                     ("right", snapshot.Right)
                 })
        {
            var tab = tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
            if (tab != null)
                return new UiLayoutTabFocus(region, tab);
        }

        return null;
    }

    private string SwitchTabCore(string tabId)
    {
        DockPosition? foundPosition = null;
        string? title = null;

        foreach (var (position, _) in TabSearchOrder)
        {
            var panel = _dock!.GetPanelForRegion(position);
            if (panel == null)
                continue;

            var tab = panel.Tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
            if (tab == null)
                continue;

            foundPosition = position;
            title = tab.Title;
            break;
        }

        if (foundPosition == null)
        {
            return ToolResultJson.Error(
                $"未找到 Tab「{tabId}」。请先调用 get_ui_layout 查看 sidebar/content/bottom/right 各区域已打开 Tab 的 id。");
        }

        _eventBus.Publish(new SwitchDockTabRequestedEvent(foundPosition.Value, tabId));

        var region = RegionKey(foundPosition.Value);
        return ToolResultJson.Data(o =>
        {
            o["ok"] = true;
            o["tabId"] = tabId;
            o["title"] = title;
            o["region"] = region;
            o["message"] = $"已切换到 {region} 区域的「{title}」（{tabId}）";
        });
    }

    private static string RegionKey(DockPosition position) => position switch
    {
        DockPosition.Left => "sidebar",
        DockPosition.Content => "content",
        DockPosition.Bottom => "bottom",
        DockPosition.Right => "right",
        _ => position.ToString().ToLowerInvariant()
    };

    private UiLayoutMenuItem[] CaptureMenu()
    {
        return _menuRegistry.GetItems()
            .OrderBy(i => i.MenuGroupOrder)
            .ThenBy(i => i.Order)
            .Select(i => new UiLayoutMenuItem(i.MenuPath, i.Header, i.CommandId, i.Order, i.IsSeparator))
            .ToArray();
    }
}
