using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Base.Mvvm;
using ZeroFall.DataTable.ViewModels;

namespace ZeroFall.SqlEditor.ViewModels;

public partial class FilePreviewViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private bool _hasDataTable;

    [ObservableProperty]
    private DataTableViewModel? _tableData;

    [ObservableProperty]
    private bool _isTextMode = true;

    /// <summary>表格视图（与 <see cref="IsTextMode"/> 互斥，供 Semi 按钮式 RadioButton 双向绑定）。</summary>
    public bool IsTableMode
    {
        get => !IsTextMode;
        set
        {
            if (!value || !HasDataTable)
                return;
            IsTextMode = false;
        }
    }

    /// <summary>打开 Tab 时预读的文本（CSV 切到文本模式时无需再读盘）。</summary>
    [ObservableProperty]
    private string? _sourceText;

    partial void OnIsTextModeChanged(bool value) => OnPropertyChanged(nameof(IsTableMode));

    partial void OnHasDataTableChanged(bool value)
    {
        if (!value && !IsTextMode)
            IsTextMode = true;
        OnPropertyChanged(nameof(IsTableMode));
    }

    public void ApplySourceTextToEditor(AvaloniaEdit.TextEditor? editor)
    {
        if (editor == null || !IsTextMode)
            return;

        if (!string.IsNullOrEmpty(SourceText))
        {
            editor.Text = SourceText;
            return;
        }

        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
        {
            editor.Text = string.IsNullOrEmpty(FilePath)
                ? "无文件路径"
                : $"找不到文件: {FilePath}";
            return;
        }

        try
        {
            SourceText = ReadAllTextShared(FilePath);
            editor.Text = SourceText;
        }
        catch (IOException ex)
        {
            editor.Text = $"无法读取文件: {FileName}\n{ex.Message}";
        }
    }

    internal static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
