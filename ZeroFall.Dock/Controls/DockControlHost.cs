using Avalonia.Controls;

namespace ZeroFall.Dock.Controls;

/// <summary>
/// Tab 的 <c>Content</c> 在注册时创建<strong>唯一</strong>的 <see cref="Control"/> 实例以保留状态；
/// 任意时刻只能有一个可视父级。在换宿主前必须经本类解除挂载。
/// </summary>
public static class DockControlHost
{
    public static void DetachFromVisualTree(Control? control)
    {
        if (control is null)
            return;

        if (control.Parent is ContentControl contentHost)
            contentHost.Content = null;
        else if (control.Parent is Panel panelHost)
            panelHost.Children.Remove(control);
    }

    public static void SetContent(ContentControl host, object? content)
    {
        if (ReferenceEquals(host.Content, content))
            return;

        host.Content = null;

        if (content is Control control)
            DetachFromVisualTree(control);

        host.Content = content;
    }
}
