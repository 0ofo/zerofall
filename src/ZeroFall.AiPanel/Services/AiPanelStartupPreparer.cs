using System;
using System.Linq;
using ZeroFall.AiPanel.ViewModels;
using ZeroFall.AiPanel.Views;
using ZeroFall.Dock.Controls;
using ZeroFall.Dock.ViewModels;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Services;

/// <summary>启动后激活 AI 会话 Tab 并标记 WebView2 浏览器可初始化（AI 聊天已为原生 UI）。</summary>
public static class AiPanelStartupPreparer
{
    public static void TryAttachAiChatWebView(DockLayoutViewModel dock)
    {
        WebView2CreationCoordinator.MarkAiAdapterReady();

        if (dock?.RightPanel is null)
            return;

        var tab = dock.RightPanel.SelectedTab
                  ?? dock.RightPanel.Tabs.FirstOrDefault(t =>
                      t.Id.StartsWith("ai-session:", StringComparison.Ordinal));

        if (tab is null)
            return;

        if (tab.Content is AiSessionTabShell shell)
            shell.OnTabBecameVisible();

        var view = ResolveAiPanelView(tab.Content);
        view?.AttachWebViewWhenReady();
        if (view?.DataContext is AiPanelViewModel viewModel)
            viewModel.RequestChatSurfaceResync();
    }

    private static AiPanelView? ResolveAiPanelView(object? content) => content switch
    {
        AiPanelView direct => direct,
        NonReloadableTabPlaceholder shell => shell.PersistedContent as AiPanelView,
        AiSessionTabShell shell => shell.PersistedContent as AiPanelView,
        _ => null
    };
}
