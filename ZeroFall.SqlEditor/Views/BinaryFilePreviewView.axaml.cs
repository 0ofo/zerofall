using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaHex.Document;
using ZeroFall.DataTable.Views;

namespace ZeroFall.SqlEditor.Views;

public partial class BinaryFilePreviewView : UserControl, ITableGridHost
{
    private readonly string _filePath;
    private IBinaryDocument? _document;

    public BinaryFilePreviewView() : this(string.Empty)
    {
    }

    public BinaryFilePreviewView(string filePath)
    {
        _filePath = filePath ?? string.Empty;
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public void NotifyTabActivated() => InvalidateHexEditorLayout();

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_document != null || string.IsNullOrEmpty(_filePath))
        {
            InvalidateHexEditorLayout();
            return;
        }

        try
        {
            _document = OpenDocument(_filePath);
            HexEditor.Document = _document;
            HexEditor.HexView.BytesPerLine = null;
            InvalidateHexEditorLayout();
            Dispatcher.UIThread.Post(InvalidateHexEditorLayout, DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Content = new TextBlock { Text = $"无法加载二进制预览: {ex.Message}" };
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) <= 0.5
            && e.NewSize.Height <= e.PreviousSize.Height)
            return;

        InvalidateHexEditorLayout();
    }

    private void InvalidateHexEditorLayout()
    {
        if (_document is null)
            return;

        HexEditor.HexView.BytesPerLine = null;
        HexEditor.InvalidateMeasure();
        HexEditor.InvalidateArrange();
        HexEditor.HexView.InvalidateMeasure();
        HexEditor.HexView.InvalidateArrange();
    }

    private static IBinaryDocument OpenDocument(string filePath)
    {
        try
        {
            // AvaloniaHex 内部 CreateViewAccessor() 需要 ReadWrite 映射；勿用 MemoryMappedFileAccess.Read。
            var mappedFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open);
            return new MemoryMappedBinaryDocument(mappedFile, leaveOpen: false, isReadOnly: true);
        }
        catch (IOException)
        {
            return new MemoryBinaryDocument(ReadAllBytesShared(filePath), isReadOnly: true);
        }
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void OnDetachedFromVisualTree(object? sender, EventArgs e)
    {
        HexEditor.Document = null;
        if (_document is IDisposable disposable)
            disposable.Dispose();
        _document = null;
    }
}
