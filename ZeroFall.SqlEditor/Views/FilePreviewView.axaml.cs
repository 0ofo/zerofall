using System;
using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Views;
using ZeroFall.Platform.Registries;
using ZeroFall.SqlEditor.ViewModels;

namespace ZeroFall.SqlEditor.Views;

public partial class FilePreviewView : UserControl, IDockTabToolPanelProvider, ITableGridHost
{
    private readonly string _filePath;
    private FilePreviewViewModel? _viewModel;
    private DataTableView? _tableView;
    private StackPanel? _dockToolPanel;
    private RadioButton? _tableModeRadio;
    private RadioButton? _textModeRadio;
    private readonly string _previewModeGroupName;

    public FilePreviewView() : this(string.Empty, null, null)
    {
    }

    public FilePreviewView(string filePath, string? sourceText = null, DataTableViewModel? dataTable = null)
    {
        _filePath = filePath ?? string.Empty;
        _previewModeGroupName = $"FilePreview_{_filePath.GetHashCode(StringComparison.Ordinal):X8}";
        InitializeComponent();

        var vm = new FilePreviewViewModel
        {
            FilePath = _filePath,
            FileName = string.IsNullOrEmpty(_filePath) ? string.Empty : Path.GetFileName(_filePath),
            HasDataTable = dataTable != null,
            TableData = dataTable,
            IsTextMode = dataTable == null,
            SourceText = sourceText
        };

        DataContext = vm;
        Tag = vm;

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private FilePreviewViewModel? ResolveViewModel()
    {
        if (Tag is FilePreviewViewModel tagVm)
            return tagVm;
        if (DataContext is FilePreviewViewModel dcVm)
            return dcVm;
        return null;
    }

    public void NotifyTabActivated() => ApplyContent();

    public Control? GetDockTabToolPanel()
    {
        var vm = ResolveViewModel();
        if (vm == null)
            return null;

        _dockToolPanel ??= CreateDockToolPanel();
        WireToolPanelCommands(vm);
        return _dockToolPanel;
    }

    private StackPanel CreateDockToolPanel()
    {
        var panel = DockTabToolPanelHelper.CreateHorizontalPanel();
        var modeGroup = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        _tableModeRadio = CreateModeRadio("表格");
        _textModeRadio = CreateModeRadio("文本");
        modeGroup.Children.Add(_tableModeRadio);
        modeGroup.Children.Add(_textModeRadio);
        panel.Children.Add(modeGroup);
        return panel;
    }

    private RadioButton CreateModeRadio(string content)
    {
        var radio = new RadioButton
        {
            Content = content,
            GroupName = _previewModeGroupName,
            Classes = { "Small" }
        };

        if (Application.Current?.TryFindResource("ButtonRadioButton", out var theme) == true
            && theme is ControlTheme buttonTheme)
            radio.Theme = buttonTheme;

        return radio;
    }

    private void WireToolPanelCommands(FilePreviewViewModel vm)
    {
        if (_tableModeRadio != null)
        {
            _tableModeRadio.IsCheckedChanged -= OnTableModeRadioCheckedChanged;
            _tableModeRadio.IsCheckedChanged += OnTableModeRadioCheckedChanged;
            _tableModeRadio.Bind(
                Visual.IsVisibleProperty,
                new Binding(nameof(FilePreviewViewModel.HasDataTable)) { Source = vm });
        }

        if (_textModeRadio != null)
        {
            _textModeRadio.IsCheckedChanged -= OnTextModeRadioCheckedChanged;
            _textModeRadio.IsCheckedChanged += OnTextModeRadioCheckedChanged;
        }

        SyncModeRadiosFromViewModel();
    }

    private void OnTableModeRadioCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_tableModeRadio?.IsChecked != true || _viewModel == null)
            return;

        if (_viewModel.IsTextMode)
            _viewModel.IsTextMode = false;
    }

    private void OnTextModeRadioCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_textModeRadio?.IsChecked != true || _viewModel == null)
            return;

        if (!_viewModel.IsTextMode)
            _viewModel.IsTextMode = true;
    }

    private void SyncModeRadiosFromViewModel()
    {
        if (_viewModel == null)
            return;

        if (_tableModeRadio != null)
            _tableModeRadio.IsChecked = !_viewModel.IsTextMode;
        if (_textModeRadio != null)
            _textModeRadio.IsChecked = _viewModel.IsTextMode;
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => AttachViewModelIfNeeded();

    private void OnLoaded(object? sender, EventArgs e) => ApplyContent();

    private void OnAttachedToVisualTree(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ApplyContent, DispatcherPriority.Loaded);
    }

    private void AttachViewModelIfNeeded()
    {
        var vm = ResolveViewModel();
        if (vm == null)
            return;

        if (!ReferenceEquals(_viewModel, vm))
        {
            if (_viewModel != null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

            _viewModel = vm;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (string.IsNullOrEmpty(vm.FilePath) && !string.IsNullOrEmpty(_filePath))
            vm.FilePath = _filePath;

        WireToolPanelCommands(vm);
        ApplyContent();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel == null)
            return;

        if (e.PropertyName is nameof(FilePreviewViewModel.IsTextMode)
            or nameof(FilePreviewViewModel.TableData))
        {
            SyncModeRadiosFromViewModel();
            ApplyContent();
        }
    }

    private DataTableView? EnsureTableView(FilePreviewViewModel vm)
    {
        if (vm.TableData == null)
            return null;

        _tableView ??= new DataTableView();
        if (!ReferenceEquals(_tableView.DataContext, vm.TableData))
            _tableView.DataContext = vm.TableData;

        return _tableView;
    }

    private void ApplyContent()
    {
        var vm = ResolveViewModel();
        if (vm == null)
            return;

        var textMode = vm.IsTextMode;

        TextEditor.IsVisible = textMode;
        TextEditor.IsHitTestVisible = textMode;
        TextEditor.ZIndex = textMode ? 1 : 0;

        TableContent.IsVisible = !textMode;
        TableContent.IsHitTestVisible = !textMode;
        TableContent.ZIndex = textMode ? 0 : 1;

        if (textMode)
        {
            vm.ApplySourceTextToEditor(TextEditor);
            ApplySyntaxHighlighting(TextEditor, vm.FilePath);
            return;
        }

        var tableView = EnsureTableView(vm);
        if (tableView == null)
            return;

        if (!ReferenceEquals(TableContent.Content, tableView))
            TableContent.Content = tableView;

        tableView.NotifyTabActivated();
    }

    /// <summary>根据文件扩展名设置 AvaloniaEdit 语法高亮。未知扩展名或大文件（>16KB）置 null。</summary>
    private static void ApplySyntaxHighlighting(AvaloniaEdit.TextEditor editor, string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || editor.Text.Length > 16 * 1024)
        {
            editor.SyntaxHighlighting = null;
            return;
        }

        var ext = System.IO.Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        var definitionName = ext switch
        {
            "sql" => "SQL",
            "json" => "JSON",
            "xml" => "XML",
            "html" or "htm" => "HTML",
            "cs" => "C#",
            "js" => "JavaScript",
            "ts" => "TypeScript",
            "java" => "Java",
            "py" => "Python",
            "rb" => "Ruby",
            "php" => "PHP",
            "cpp" or "c" or "h" or "hpp" => "C++",
            "css" => "CSS",
            "markdown" or "md" => "Markdown",
            _ => null
        };

        editor.SyntaxHighlighting = definitionName != null
            ? AvaloniaEdit.Highlighting.HighlightingManager.Instance.GetDefinition(definitionName)
            : null;
    }
}
