using System;
using System.Text.Json;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Browser.Views;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Serialization;
using ZeroFall.Platform.Services;

namespace ZeroFall.Browser.Services;

public sealed class BrowserUiLayoutTabExtraProvider : IUiLayoutTabExtraProvider
{
    private readonly ICdpBridge _cdpBridge;
    private readonly IBrowserTabManager _tabManager;

    public BrowserUiLayoutTabExtraProvider(ICdpBridge cdpBridge, IBrowserTabManager tabManager)
    {
        _cdpBridge = cdpBridge;
        _tabManager = tabManager;
    }

    public bool TryGetExtra(DockTabItemViewModel tab, DockPosition region, out JsonElement extra)
    {
        extra = default;
        if (region != DockPosition.Content || !IsBrowserContentTab(tab.Id))
            return false;

        if (!TryResolveBrowserTabViewModel(tab, out var vm))
            return false;

        var tabId = string.IsNullOrWhiteSpace(vm.TabId) ? tab.Id : vm.TabId;
        var activeTabId = _tabManager.ResolveTabId(null);
        var dto = new BrowserContentTabUiLayoutExtra(
            tabId,
            vm.PageSessionId,
            vm.Address,
            vm.TopLevelUrl,
            _cdpBridge.HasSession(tabId),
            !string.IsNullOrEmpty(activeTabId)
            && string.Equals(activeTabId, tabId, StringComparison.Ordinal));

        extra = JsonSerializer.SerializeToElement(dto, PlatformJsonContext.Default.BrowserContentTabUiLayoutExtra);
        return true;
    }

    private static bool TryResolveBrowserTabViewModel(DockTabItemViewModel tab, out BrowserTabViewModel vm)
    {
        vm = null!;
        if (NonReloadableTabContent.Resolve<BrowserTabView>(tab.Content) is not { } view)
            return false;

        if (view.Tag is BrowserTabViewModel viewTagVm)
        {
            vm = viewTagVm;
            return true;
        }

        if (view.DataContext is BrowserTabViewModel dcVm)
        {
            vm = dcVm;
            return true;
        }

        return false;
    }

    private static bool IsBrowserContentTab(string tabId) =>
        tabId.StartsWith("browser", StringComparison.Ordinal)
        || tabId.StartsWith("fetch-", StringComparison.Ordinal);
}
