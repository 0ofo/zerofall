using System;
using System.Linq;
using ZeroFall.Browser.Views;
using ZeroFall.Dock.ViewModels;
using ZeroFall.Platform.Services;

namespace ZeroFall.Browser.Services;

/// <summary>
/// Content 区浏览器 WebView2：仅在用户打开浏览器 Tab 且 Tab 可见时挂载（见 App <see cref="ZeroFall.App.Services.WebViewStartupCoordinator"/>）。
/// </summary>
public static class BrowserStartupPreparer
{
    public static void TryAttachAllBrowserTabs(DockLayoutViewModel dock)
    {
        if (!StartupPerformance.IsLayoutReady)
            return;

        StartupPerformance.RunOnUiIdle(() =>
        {
            foreach (var tab in dock.ContentPanel.Tabs)
            {
                if (!IsBrowserContentTabId(tab.Id))
                    continue;

                if (NonReloadableTabContent.Resolve<BrowserTabView>(tab.Content) is { } view)
                    view.AttachWebViewWhenReady();
            }
        });
    }

    public static void TryAttachBrowserTab(DockLayoutViewModel dock, string? tabId)
    {
        if (string.IsNullOrEmpty(tabId) || !IsBrowserContentTabId(tabId))
            return;

        if (!StartupPerformance.IsLayoutReady)
            return;

        StartupPerformance.RunOnUiIdle(() =>
        {
            var tab = dock.ContentPanel.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (NonReloadableTabContent.Resolve<BrowserTabView>(tab?.Content) is { } view)
                view.AttachWebViewWhenReady();
        });
    }

    private static bool IsBrowserContentTabId(string tabId) =>
        tabId.StartsWith("browser", StringComparison.Ordinal)
        || tabId.StartsWith("fetch-", StringComparison.Ordinal);
}
