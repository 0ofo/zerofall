using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;

namespace ZeroFall.Terminal;

/// <summary>
/// 官方 Iciclecreek 2.x 未公开 SendTextAsync；通过剪贴板 + <see cref="TerminalView.PasteAsync"/> 注入 PTY 输入。
/// </summary>
internal static class TerminalControlExtensions
{
    public static async Task SendTextAsync(this TerminalControl control, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        control.ApplyTemplate();
        var view = control.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
        if (view == null)
            return;

        var topLevel = TopLevel.GetTopLevel(control);
        var clipboard = topLevel?.Clipboard;
        if (clipboard == null)
            return;

        var previous = await clipboard.TryGetTextAsync();
        await clipboard.SetTextAsync(text);
        try
        {
            await view.PasteAsync();
        }
        finally
        {
            if (previous != null)
                await clipboard.SetTextAsync(previous);
        }
    }
}
