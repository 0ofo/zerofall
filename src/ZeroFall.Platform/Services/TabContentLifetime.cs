using System;
using Avalonia.Controls;

namespace ZeroFall.Platform.Services;

/// <summary>
/// Content 区 Tab 关闭时释放大对象（文件缓冲、表格 Provider、Hex 映射等）。
/// 实现方勿在 <c>DetachedFromVisualTree</c> 中调用：PersistTab 切 Tab 会暂时离树，只有 Tab 关闭才应释放。
/// </summary>
public interface ITabContentReleasable
{
    void ReleaseTabResources();
}

public static class TabContentLifetime
{
    public static void Release(object? content)
    {
        if (content is null)
            return;

        if (content is ITabContentReleasable releasable)
        {
            releasable.ReleaseTabResources();
            return;
        }

        if (content is INonReloadableTabShell shell)
        {
            Release(shell.PersistedContent);
            if (shell is ITabContentReleasable shellReleasable)
                shellReleasable.ReleaseTabResources();
            return;
        }

        if (content is Control control)
            ReleaseFromControl(control);
    }

    private static void ReleaseFromControl(Control control)
    {
        if (control is ITabContentReleasable releasable)
        {
            releasable.ReleaseTabResources();
            return;
        }

        if (control.DataContext is ITabContentReleasable dataReleasable)
            dataReleasable.ReleaseTabResources();
    }
}
