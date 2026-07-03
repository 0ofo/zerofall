using System.Threading.Tasks;
using Avalonia.Controls;
using ZeroFall.Dock.Services;

namespace ZeroFall.AiPanel.Views;

internal static class AiSessionCloseConfirmDialog
{
    public static Task<bool> ShowAsync(Window? owner, string message) =>
        AppDialogService.ConfirmAsync(owner, message, title: "关闭会话", warning: true);
}
