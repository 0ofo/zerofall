using Avalonia.Controls;

namespace ZeroFall.Platform.Services;

/// <summary>
/// 不可重载 Tab 的占位壳：Tab 内容区只渲染此壳（空面板占布局），
/// 真实 <see cref="PersistedContent"/> 由 <see cref="PersistTabControl"/> 挂到叠层并管理显隐。
/// </summary>
public interface INonReloadableTabShell
{
    Control? PersistedContent { get; }

    void OnTabBecameVisible();

    void OnTabBecameHidden();
}
