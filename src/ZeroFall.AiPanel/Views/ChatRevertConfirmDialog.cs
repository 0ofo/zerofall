using System.Threading.Tasks;
using Avalonia.Controls;
using ZeroFall.Dock.Services;

namespace ZeroFall.AiPanel.Views;

internal static class ChatRevertConfirmDialog
{
    private const string Message =
        "确定撤销此条消息及之后的全部对话？\n此操作不可恢复；若 AI 正在输出将先停止。";

    public static Task<bool> ShowAsync(Window? owner) =>
        AppDialogService.ConfirmAsync(owner, Message, title: "撤销对话", warning: true);
}
