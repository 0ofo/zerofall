using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ZeroFall.Settings.ViewModels;

namespace ZeroFall.Settings.Helpers;

internal static class SettingsBindingHelper
{
    /// <summary>提交仍聚焦在输入框上的绑定（默认 LostFocus 更新源）。</summary>
    public static void CommitPendingEdits(Control? root)
    {
        if (root == null)
            return;

        if (TopLevel.GetTopLevel(root) is InputElement topLevel)
            topLevel.Focus(NavigationMethod.Unspecified);
    }

}
