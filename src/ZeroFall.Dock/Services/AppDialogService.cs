using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Ursa.Controls;
using ZeroFall.Dock.ViewModels;
using ZeroFall.Dock.Views;

namespace ZeroFall.Dock.Services;

/// <summary>
/// 应用内标准对话框（Ursa StandardDialog），设置窗口除外。
/// </summary>
public static class AppDialogService
{
    public static Window? ResolveOwner(Visual? context)
    {
        if (context != null && TopLevel.GetTopLevel(context) is Window window)
            return window;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;

        return null;
    }

    public static async Task<bool> ConfirmAsync(
        Window? owner,
        string message,
        string title = "确认",
        bool warning = false)
    {
        owner ??= ResolveOwner(null);
        if (owner is null)
            return false;

        var vm = new ConfirmDialogViewModel { Message = message };
        var result = await Dialog.ShowStandardAsync<ConfirmDialogView, ConfirmDialogViewModel>(
            vm,
            owner,
            CreateOptions(title, warning ? DialogMode.Warning : DialogMode.None));

        return result == DialogResult.OK;
    }

    public static async Task<string?> PromptAsync(
        Window? owner,
        string title,
        string? initialValue = null,
        bool multiline = false,
        int selectionEnd = 0)
    {
        owner ??= ResolveOwner(null);
        if (owner is null)
            return null;

        var vm = new TextInputDialogViewModel
        {
            Text = initialValue ?? string.Empty,
            Multiline = multiline,
            SelectionEnd = selectionEnd,
        };

        var result = await Dialog.ShowStandardAsync<TextInputDialogView, TextInputDialogViewModel>(
            vm,
            owner,
            CreateOptions(title, DialogMode.None, canResize: multiline));

        return result == DialogResult.OK ? vm.Text : null;
    }

    private static DialogOptions CreateOptions(
        string title,
        DialogMode mode,
        bool canResize = false) =>
        new()
        {
            Title = title,
            Mode = mode,
            Button = DialogButton.OKCancel,
            CanResize = canResize,
            StartupLocation = WindowStartupLocation.CenterOwner,
        };
}
