using System;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Base.Mvvm;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.SqlEditor.Services;

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
    private bool _hasMarkdownPreview;

    [ObservableProperty]
    private DataTableViewModel? _tableData;

    [ObservableProperty]
    private FilePreviewSurface _surface = FilePreviewSurface.Source;

    /// <summary>表格视图（与原文互斥）。</summary>
    public bool IsTableMode
    {
        get => Surface == FilePreviewSurface.Table;
        set
        {
            if (!value || !HasDataTable)
                return;
            Surface = FilePreviewSurface.Table;
        }
    }

    /// <summary>原文模式（AvaloniaEdit）。</summary>
    public bool IsTextMode
    {
        get => Surface == FilePreviewSurface.Source;
        set => Surface = value
            ? FilePreviewSurface.Source
            : HasMarkdownPreview
                ? FilePreviewSurface.Rendered
                : FilePreviewSurface.Table;
    }

    /// <summary>Markdown 渲染模式（LiveMarkdown）。</summary>
    public bool IsRenderedMode
    {
        get => Surface == FilePreviewSurface.Rendered;
        set
        {
            if (!value || !HasMarkdownPreview)
                return;
            Surface = FilePreviewSurface.Rendered;
        }
    }

    public bool ShowSecondaryMode => HasDataTable || HasMarkdownPreview;

    /// <summary>打开 Tab 时预读的文本（切换模式时无需再读盘）。</summary>
    [ObservableProperty]
    private string? _sourceText;

    partial void OnSurfaceChanged(FilePreviewSurface value)
    {
        OnPropertyChanged(nameof(IsTableMode));
        OnPropertyChanged(nameof(IsTextMode));
        OnPropertyChanged(nameof(IsRenderedMode));
    }

    partial void OnHasDataTableChanged(bool value)
    {
        if (!value && Surface == FilePreviewSurface.Table)
            Surface = FilePreviewSurface.Source;
        OnPropertyChanged(nameof(IsTableMode));
        OnPropertyChanged(nameof(ShowSecondaryMode));
    }

    partial void OnHasMarkdownPreviewChanged(bool value)
    {
        if (!value && Surface == FilePreviewSurface.Rendered)
            Surface = FilePreviewSurface.Source;
        OnPropertyChanged(nameof(IsRenderedMode));
        OnPropertyChanged(nameof(ShowSecondaryMode));
    }

    public void InvalidateCachedText() => SourceText = null;

    public string GetSourceText()
    {
        if (SourceText is not null)
            return SourceText;

        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            return string.IsNullOrEmpty(FilePath)
                ? "无文件路径"
                : $"找不到文件: {FilePath}";

        try
        {
            SourceText = ReadAllTextShared(FilePath);
            return SourceText;
        }
        catch (IOException ex)
        {
            return $"无法读取文件: {FileName}\n{ex.Message}";
        }
    }

    public void ApplySourceTextToEditor(AvaloniaEdit.TextEditor? editor)
    {
        if (editor == null || Surface != FilePreviewSurface.Source)
            return;

        var text = GetSourceText();
        if (!string.Equals(editor.Text, text, StringComparison.Ordinal))
            editor.Text = text;
    }

    internal static bool DetectMarkdownPreview(string filePath) =>
        MarkdownDocumentHtmlRenderer.IsMarkdownFile(filePath);

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
