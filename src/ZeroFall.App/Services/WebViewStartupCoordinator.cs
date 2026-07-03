using ZeroFall.AiPanel.Services;
using ZeroFall.Browser.Services;
using ZeroFall.Dock.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Services;

namespace ZeroFall.App.Services;

/// <summary>
/// 启动后激活 AI 聊天面板；Content 浏览器 Tab 由用户打开后再延迟挂载。
/// </summary>
public static class WebViewStartupCoordinator
{
    public static void Schedule(DockLayoutViewModel dock, IEventBus eventBus)
    {
        eventBus.Subscribe<ActiveContentTabChangedEvent>(e =>
            BrowserStartupPreparer.TryAttachBrowserTab(dock, e.TabId));

        StartupPerformance.RunAfterDelay(() =>
        {
            if (!StartupPerformance.IsLayoutReady)
                return;

            StartupPerformance.RunOnUiIdle(() => AiPanelStartupPreparer.TryAttachAiChatWebView(dock));
        }, delayMs: 500);
    }
}
