using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ZeroFall.DataTable.ViewModels;

namespace ZeroFall.DataTable.Views;

/// <summary>
/// 为 <see cref="DataTableViewModel"/> 注入 CSV 保存对话框；供 DataTableView / DataTablePagerView 等宿主共用。
/// </summary>
internal static class DataTableCsvExportHost
{
    public static void Attach(DataTableViewModel vm, Visual anchor)
    {
        vm.OpenCsvSaveStreamAsync = () => OpenSaveStreamAsync(anchor);
        vm.RefreshExportCommands();
    }

    public static void Detach(DataTableViewModel vm)
    {
        vm.OpenCsvSaveStreamAsync = null;
        vm.RefreshExportCommands();
    }

    private static async Task<Stream?> OpenSaveStreamAsync(Visual anchor)
    {
        var top = TopLevel.GetTopLevel(anchor);
        if (top?.StorageProvider == null)
            return null;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 CSV",
            SuggestedFileName = "export.csv",
            DefaultExtension = "csv",
            FileTypeChoices =
            [
                new FilePickerFileType("CSV (*.csv)") { Patterns = ["*.csv"] }
            ]
        }).ConfigureAwait(true);

        if (file == null)
            return null;

        return await file.OpenWriteAsync().ConfigureAwait(true);
    }
}
