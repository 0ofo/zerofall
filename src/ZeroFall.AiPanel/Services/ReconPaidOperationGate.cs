using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Ursa.Controls;
using ZeroFall.AiPanel.ViewModels;
using ZeroFall.AiPanel.Views;
using ZeroFall.Dock.Services;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Services;

/// <summary>资产测绘付费操作：StandardDialog 确认（说明 + 查询语句 + 底栏确定/取消）。</summary>
public sealed class ReconPaidOperationGate : IReconPaidOperationGate
{
    public Task<bool> ConfirmAsync(
        string summary,
        string query,
        CancellationToken cancellationToken = default) =>
        UiThreadBridge.InvokeAsync(() => ShowConfirmAsync(summary, query));

    private static async Task<bool> ShowConfirmAsync(string summary, string query)
    {
        var owner = AppDialogService.ResolveOwner(null);
        if (owner is null)
            return false;

        var vm = new ReconPaidConfirmDialogViewModel
        {
            Summary = summary,
            Query = query.Trim(),
        };

        var result = await Dialog.ShowStandardAsync<ReconPaidConfirmDialogView, ReconPaidConfirmDialogViewModel>(
            vm,
            owner,
            new DialogOptions
            {
                Title = "资产测绘",
                Mode = DialogMode.None,
                Button = DialogButton.OKCancel,
                CanResize = false,
                StartupLocation = WindowStartupLocation.CenterOwner,
            });

        return result == DialogResult.OK;
    }
}
