using Avalonia.Controls;

namespace Datafinder.Platform.Services;

public static class NonReloadableTabContent
{
    public static TabContentMode GetMode(object? tabContent) =>
        tabContent is INonReloadableTabShell ? TabContentMode.NonReloadable : TabContentMode.Reloadable;

    /// <summary>穿透不可重载占位壳，取得真实控件；可重载 Tab 则直接返回 Content。</summary>
    public static Control? Resolve(object? tabContent) =>
        tabContent switch
        {
            INonReloadableTabShell { PersistedContent: Control persisted } => persisted,
            Control direct => direct,
            _ => null
        };

    public static T? Resolve<T>(object? tabContent) where T : Control =>
        Resolve(tabContent) as T;
}
