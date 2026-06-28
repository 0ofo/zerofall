namespace ZeroFall.Platform.Services;

/// <summary>
/// 不可重载 Tab 内真实控件：在 Tab 选中/隐藏/关闭时接收生命周期回调（显隐、挂载、销毁等）。
/// </summary>
public interface INonReloadableTabHost
{
    void OnTabBecameVisible();

    void OnTabBecameHidden();
}
