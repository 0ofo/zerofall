using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using ZeroFall.Base.Events;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.Browser.Services;

public interface IBrowserTabManager
{
    void OnTabRegistered(string tabId, BrowserTabViewModel viewModel);

    void OnTabUnregistered(string tabId);

    /// <summary>在 Content 区新建浏览器标签并打开 URL，返回 tabId。</summary>
    Task<string> OpenTabAsync(string url, string? title = null, bool activate = true);

    string ListTabsJson();

    Task<string> SwitchTabAsync(string tabId);

    Task<string> CloseTabAsync(string tabId);

    /// <summary>解析目标 tabId；不存在时返回 null。</summary>
    string? ResolveTabId(string? tabId);

    /// <summary>获取当前活动标签页的 URL；无活动标签返回 null。</summary>
    string? GetActiveTabUrl();

    /// <summary>获取标签页地址栏 TopLevelUrl（文档 URL）；无则回退 Address。</summary>
    bool TryGetTopLevelUrl(string tabId, out string? topLevelUrl);

    /// <summary>获取标签页当前文档 URL 与会话 Id（用于与流量事件的 PageSessionId 对齐）。</summary>
    bool TryGetTabNavigationState(string tabId, out string? topLevelUrl, out int pageSessionId);
}

public sealed class BrowserTabManager : IBrowserTabManager
{
    private readonly ICdpBridge _cdpBridge;
    private readonly IEventBus _eventBus;
    private readonly ConcurrentDictionary<string, TabEntry> _entries = new(StringComparer.Ordinal);
    private string? _uiActiveTabId;

    public BrowserTabManager(ICdpBridge cdpBridge, IEventBus eventBus)
    {
        _cdpBridge = cdpBridge;
        _eventBus = eventBus;
        eventBus.Subscribe<ActiveContentTabChangedEvent>(OnActiveContentTabChanged);
    }

    private void OnActiveContentTabChanged(ActiveContentTabChangedEvent e)
    {
        if (!IsBrowserContentTab(e.TabId))
            return;

        _uiActiveTabId = e.TabId;
        if (_cdpBridge.HasSession(e.TabId))
            _cdpBridge.SetActiveTab(e.TabId);
    }

    public void OnTabRegistered(string tabId, BrowserTabViewModel viewModel)
    {
        var entry = _entries.GetOrAdd(tabId, _ => new TabEntry(tabId));
        entry.ViewModel = viewModel;
        entry.PropertyHandler ??= (_, args) => OnTabPropertyChanged(tabId, args);
        viewModel.PropertyChanged -= entry.PropertyHandler;
        viewModel.PropertyChanged += entry.PropertyHandler;
        entry.Title = viewModel.Title;
        entry.Url = viewModel.Address;
        entry.TopLevelUrl = viewModel.TopLevelUrl;
        entry.HasCdp = true;
    }

    public void OnTabUnregistered(string tabId)
    {
        if (_entries.TryRemove(tabId, out var entry) && entry.ViewModel != null && entry.PropertyHandler != null)
            entry.ViewModel.PropertyChanged -= entry.PropertyHandler;
    }

    public Task<string> OpenTabAsync(string url, string? title = null, bool activate = true) =>
        UiThreadBridge.InvokeAsync(() => OpenTabCore(url, title, activate));

    private string OpenTabCore(string url, string? title, bool activate)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var tabId = $"browser-{Guid.NewGuid():N}";
        var displayTitle = string.IsNullOrWhiteSpace(title) ? "新标签页" : title.Trim();
        _entries[tabId] = new TabEntry(tabId)
        {
            Title = displayTitle,
            Url = url.Trim(),
            HasCdp = false
        };

        _eventBus.Publish(new OpenBrowserTabRequestedEvent(url.Trim(), displayTitle, tabId));
        if (activate)
            SwitchTabCore(tabId);

        return tabId;
    }

    public string ListTabsJson()
    {
        var ids = new HashSet<string>(_cdpBridge.GetRegisteredTabIds(), StringComparer.Ordinal);
        foreach (var key in _entries.Keys)
            ids.Add(key);

        var active = _cdpBridge.ActiveTabId ?? _uiActiveTabId;
        var list = new List<Serialization.BrowserTabListItemDto>();
        foreach (var tabId in ids.OrderBy(id => id, StringComparer.Ordinal))
        {
            _entries.TryGetValue(tabId, out var entry);
            list.Add(new Serialization.BrowserTabListItemDto
            {
                TabId = tabId,
                Title = entry?.Title ?? tabId,
                Url = entry?.Url ?? string.Empty,
                HasCdpSession = _cdpBridge.HasSession(tabId),
                IsActive = string.Equals(tabId, active, StringComparison.Ordinal),
                IsEphemeral = tabId.StartsWith("fetch-", StringComparison.Ordinal)
            });
        }

        return Serialization.BrowserJson.Serialize(new Serialization.BrowserTabListResponseDto
        {
            ActiveTabId = active,
            Tabs = list
        });
    }

    public Task<string> SwitchTabAsync(string tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
            return Task.FromResult("tabId 不能为空");

        return UiThreadBridge.InvokeAsync(() => SwitchTabCore(tabId.Trim()));
    }

    private string SwitchTabCore(string tabId)
    {
        if (!TabExists(tabId))
            return $"未找到标签: {tabId}";

        _eventBus.Publish(new SwitchDockTabRequestedEvent(DockPosition.Content, tabId));
        if (_cdpBridge.HasSession(tabId))
            _cdpBridge.SetActiveTab(tabId);
        _uiActiveTabId = tabId;
        return $"已切换到标签 {tabId}";
    }

    public Task<string> CloseTabAsync(string tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
            return Task.FromResult("tabId 不能为空");

        return UiThreadBridge.InvokeAsync(() => CloseTabCore(tabId.Trim()));
    }

    private string CloseTabCore(string tabId)
    {
        if (!TabExists(tabId))
            return $"未找到标签: {tabId}";

        _eventBus.Publish(new CloseContentTabRequestedEvent(tabId));
        return $"已关闭标签 {tabId}";
    }

    public string? ResolveTabId(string? tabId)
    {
        if (!string.IsNullOrWhiteSpace(tabId))
        {
            tabId = tabId.Trim();
            return TabExists(tabId) ? tabId : null;
        }

        if (!string.IsNullOrEmpty(_cdpBridge.ActiveTabId) && _cdpBridge.HasSession(_cdpBridge.ActiveTabId))
            return _cdpBridge.ActiveTabId;

        if (!string.IsNullOrEmpty(_uiActiveTabId) && _cdpBridge.HasSession(_uiActiveTabId))
            return _uiActiveTabId;

        if (!string.IsNullOrEmpty(_uiActiveTabId) && TabExists(_uiActiveTabId))
            return _uiActiveTabId;

        var first = _cdpBridge.GetRegisteredTabIds().FirstOrDefault();
        return first;
    }

    private bool TabExists(string tabId) =>
        _entries.ContainsKey(tabId) || _cdpBridge.HasSession(tabId);

    public string? GetActiveTabUrl()
    {
        var activeId = ResolveTabId(null);
        if (string.IsNullOrEmpty(activeId) || !_entries.TryGetValue(activeId, out var entry))
            return null;
        return entry.Url;
    }

    public bool TryGetTopLevelUrl(string tabId, out string? topLevelUrl)
    {
        if (TryGetTabNavigationState(tabId, out topLevelUrl, out _))
            return !string.IsNullOrWhiteSpace(topLevelUrl);
        topLevelUrl = null;
        return false;
    }

    public bool TryGetTabNavigationState(string tabId, out string? topLevelUrl, out int pageSessionId)
    {
        topLevelUrl = null;
        pageSessionId = 0;
        if (string.IsNullOrWhiteSpace(tabId) || !_entries.TryGetValue(tabId.Trim(), out var entry) || entry.ViewModel is null)
            return false;

        pageSessionId = entry.ViewModel.PageSessionId;
        if (!string.IsNullOrWhiteSpace(entry.ViewModel.TopLevelUrl))
            topLevelUrl = entry.ViewModel.TopLevelUrl;
        else if (!string.IsNullOrWhiteSpace(entry.ViewModel.Address))
            topLevelUrl = entry.ViewModel.Address;
        else if (!string.IsNullOrWhiteSpace(entry.TopLevelUrl))
            topLevelUrl = entry.TopLevelUrl;
        else
            topLevelUrl = entry.Url;

        return !string.IsNullOrWhiteSpace(topLevelUrl);
    }

    private void OnTabPropertyChanged(string tabId, PropertyChangedEventArgs e)
    {
        if (!_entries.TryGetValue(tabId, out var entry) || entry.ViewModel == null)
            return;

        if (e.PropertyName is nameof(BrowserTabViewModel.Title))
            entry.Title = entry.ViewModel.Title;
        else if (e.PropertyName is nameof(BrowserTabViewModel.Address))
            entry.Url = entry.ViewModel.Address;
        else if (e.PropertyName is nameof(BrowserTabViewModel.TopLevelUrl))
            entry.TopLevelUrl = entry.ViewModel.TopLevelUrl;
    }

    private static bool IsBrowserContentTab(string tabId) =>
        tabId.StartsWith("browser", StringComparison.Ordinal)
        || tabId.StartsWith("fetch-", StringComparison.Ordinal);

    private sealed class TabEntry
    {
        public TabEntry(string tabId) => TabId = tabId;

        public string TabId { get; }
        public BrowserTabViewModel? ViewModel;
        public PropertyChangedEventHandler? PropertyHandler;
        public string Title = string.Empty;
        public string Url = string.Empty;
        public string TopLevelUrl = string.Empty;
        public bool HasCdp;
    }
}
