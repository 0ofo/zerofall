using Avalonia.Controls;

namespace ZeroFall.Dock.Controls;

/// <summary>
/// Content 区 Tab 内容注册：可重载 / 不可重载 两种模式。
/// </summary>
public static class TabContent
{
    /// <summary>可重载（默认）：直接作为 <see cref="Platform.Registries.DockTabItemViewModel.Content"/>。</summary>
    public static Control Reloadable(Control view) => view;

    /// <summary>不可重载：占位壳 + 叠层保活，切 Tab 不卸载真实控件。</summary>
    public static NonReloadableTabPlaceholder NonReloadable(Control view) =>
        NonReloadableTabPlaceholder.Wrap(view);
}
