namespace Datafinder.Platform.Services;

/// <summary>
/// Content 区 Tab 内容生命周期模式（由 <see cref="PersistTabControl"/> 实现）。
/// </summary>
public enum TabContentMode
{
    /// <summary>
    /// 可重载（默认）：<c>DockTabItemViewModel.Content</c> 直接挂控件；
    /// 选中时由 TabControl <c>PART_SelectedContentHost</c> 原生挂载，切走即卸载，再次选中可重新 Loaded。
    /// 适用于编辑器、Hex 预览、表格等。
    /// </summary>
    Reloadable,

    /// <summary>
    /// 不可重载：<c>Content</c> 为 <see cref="INonReloadableTabShell"/> 占位壳，
    /// 真实控件在叠层保活；切 Tab 仅显隐并回调 <see cref="INonReloadableTabHost"/>，关闭 Tab 时再销毁。
    /// 适用于 WebView2 等卸载代价高的控件。
    /// </summary>
    NonReloadable
}
