using System;
using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using ZeroFall.Base.Events;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Views;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using ZeroFall.SqlEditor.Services;
using ZeroFall.SqlEditor.ViewModels;

namespace ZeroFall.SqlEditor.Views;

public partial class FilePreviewView : UserControl, IDockTabToolPanelProvider, ITableGridHost, ITabContentReleasable
{
    private readonly string _filePath;
    private readonly IEventBus? _eventBus;
    private FilePreviewViewModel? _viewModel;
    private DataTableView? _tableView;
    private StackPanel? _dockToolPanel;
    private RadioButton? _secondaryModeRadio;
    private RadioButton? _sourceModeRadio;
    private readonly string _previewModeGroupName;
    private bool _syncingModeRadios;
    private bool _textContentReady;
    private string? _highlightedFilePath;
    private string? _renderedSourceFingerprint;
    private bool _resourcesReleased;
    private bool _applyingEditorText;

    public FilePreviewView() : this(string.Empty, null, null, null)
    {
    }

    public FilePreviewView(
        string filePath,
        string? sourceText = null,
        DataTableViewModel? dataTable = null,
        IEventBus? eventBus = null)
    {
        _filePath = filePath ?? string.Empty;
        _eventBus = eventBus;
        _previewModeGroupName = $"FilePreview_{_filePath.GetHashCode(StringComparison.Ordinal):X8}";
        InitializeComponent();
        MarkdownPreview.SetEventBus(_eventBus);

        var vm = new FilePreviewViewModel
        {
            FilePath = _filePath,
            FileName = string.IsNullOrEmpty(_filePath) ? string.Empty : Path.GetFileName(_filePath),
            HasDataTable = dataTable != null,
            HasMarkdownPreview = FilePreviewViewModel.DetectMarkdownPreview(_filePath),
            TableData = dataTable,
            Surface = FilePreviewViewModel.DetectMarkdownPreview(_filePath)
                ? FilePreviewSurface.Rendered
                : FilePreviewSurface.Source,
            SourceText = sourceText
        };

        DataContext = vm;
        Tag = vm;

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        AttachedToVisualTree += OnAttachedToVisualTree;
        TextEditor.TextChanged += OnTextEditorTextChanged;
        TextEditor.TextArea.KeyDown += OnTextEditorKeyDown;
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        CodeSyntaxHighlighting.InvalidateCache();
        _renderedSourceFingerprint = null;
        ApplyContent();
    }

    /// <summary>仅由 <see cref="TabContentLifetime.Release"/> 在 Tab 关闭时调用；勿绑 DetachedFromVisualTree（PersistTab 切 Tab 会暂时离树）。</summary>
    public void ReleaseTabResources()
    {
        if (_resourcesReleased)
            return;

        _resourcesReleased = true;

        if (_eventBus != null)
            _eventBus.Unsubscribe<WorkspaceFileChangedEvent>(OnWorkspaceFileChanged);

        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged -= OnThemeChanged;

        DataContextChanged -= OnDataContextChanged;
        Loaded -= OnLoaded;
        AttachedToVisualTree -= OnAttachedToVisualTree;
        TextEditor.TextChanged -= OnTextEditorTextChanged;
        TextEditor.TextArea.KeyDown -= OnTextEditorKeyDown;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (_viewModel.TableData is IDisposable tableDisposable)
                tableDisposable.Dispose();
            _viewModel.TableData = null;
            _viewModel.InvalidateCachedText();
            _viewModel = null;
        }

        TextEditor.Text = string.Empty;
        TextEditor.SyntaxHighlighting = null;
        TableContent.Content = null;
        _tableView = null;
        MarkdownPreview.ReleaseTabResources();

        _textContentReady = false;
        _highlightedFilePath = null;
        _renderedSourceFingerprint = null;
        Tag = null;
        DataContext = null;
    }

    private void OnWorkspaceFileChanged(WorkspaceFileChangedEvent e)
    {
        var vm = ResolveViewModel();
        if (vm == null || string.IsNullOrEmpty(vm.FilePath))
            return;

        if (!string.Equals(
                Path.GetFullPath(vm.FilePath),
                Path.GetFullPath(e.FilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (e.Deleted)
            {
                vm.InvalidateCachedText();
                _textContentReady = false;
                _highlightedFilePath = null;
                _renderedSourceFingerprint = null;
                TextEditor.Text = $"文件已删除: {vm.FileName}";
                TextEditor.IsReadOnly = true;
                return;
            }

            vm.InvalidateCachedText();
            _textContentReady = false;
            _highlightedFilePath = null;
            _renderedSourceFingerprint = null;
            TextEditor.IsReadOnly = false;

            if (vm.HasDataTable && !string.IsNullOrEmpty(vm.FilePath))
            {
                try
                {
                    vm.TableData = DataTableViewModel.FromCsv(vm.FilePath);
                }
                catch
                {
                    // 保留旧表格，文本模式仍可从磁盘重读
                }
            }

            ApplyContent();
        }, DispatcherPriority.Normal);
    }

    private FilePreviewViewModel? ResolveViewModel()
    {
        if (Tag is FilePreviewViewModel tagVm)
            return tagVm;
        if (DataContext is FilePreviewViewModel dcVm)
            return dcVm;
        return null;
    }

    public void NotifyTabActivated()
    {
        var vm = ResolveViewModel();
        if (vm == null)
            return;

        switch (vm.Surface)
        {
            case FilePreviewSurface.Source:
                if (!_textContentReady)
                    ApplyContent();
                break;
            case FilePreviewSurface.Table:
                if (vm.TableData == null)
                    return;
                EnsureTableView(vm)?.NotifyTabActivated();
                if (!TableContent.IsVisible)
                    ApplyContent();
                break;
            case FilePreviewSurface.Rendered:
                ApplyRenderedPreview(vm, force: false);
                break;
        }
    }

    public Control? GetDockTabToolPanel()
    {
        var vm = ResolveViewModel();
        if (vm == null)
            return null;

        AttachViewModelIfNeeded();
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

        _sourceModeRadio = CreateModeRadio("原文");
        _secondaryModeRadio = CreateModeRadio("渲染");
        modeGroup.Children.Add(_sourceModeRadio);
        modeGroup.Children.Add(_secondaryModeRadio);
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
        if (_sourceModeRadio != null)
        {
            _sourceModeRadio.Content = vm.HasMarkdownPreview ? "原文" : "文本";
            _sourceModeRadio.IsCheckedChanged -= OnSourceModeRadioCheckedChanged;
            _sourceModeRadio.IsCheckedChanged += OnSourceModeRadioCheckedChanged;
        }

        if (_secondaryModeRadio != null)
        {
            _secondaryModeRadio.Content = vm.HasMarkdownPreview ? "渲染" : "表格";
            _secondaryModeRadio.IsCheckedChanged -= OnSecondaryModeRadioCheckedChanged;
            _secondaryModeRadio.IsCheckedChanged += OnSecondaryModeRadioCheckedChanged;
            _secondaryModeRadio.Bind(
                Visual.IsVisibleProperty,
                new Binding(nameof(FilePreviewViewModel.ShowSecondaryMode)) { Source = vm });
        }

        SyncModeRadiosFromViewModel();
    }

    private void OnSourceModeRadioCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncingModeRadios || _sourceModeRadio?.IsChecked != true)
            return;

        var vm = ResolveViewModel();
        if (vm == null)
            return;

        if (vm.Surface != FilePreviewSurface.Source)
        {
            vm.Surface = FilePreviewSurface.Source;
            ApplyContent();
        }
    }

    private void OnSecondaryModeRadioCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncingModeRadios || _secondaryModeRadio?.IsChecked != true)
            return;

        var vm = ResolveViewModel();
        if (vm == null)
            return;

        if (vm.HasMarkdownPreview)
        {
            if (vm.Surface != FilePreviewSurface.Rendered)
            {
                vm.Surface = FilePreviewSurface.Rendered;
                ApplyContent();
            }

            return;
        }

        if (!vm.HasDataTable)
            return;

        if (vm.Surface != FilePreviewSurface.Table)
        {
            vm.Surface = FilePreviewSurface.Table;
            ApplyContent();
        }
    }

    private void SyncModeRadiosFromViewModel()
    {
        var vm = ResolveViewModel();
        if (vm == null)
            return;

        try
        {
            _syncingModeRadios = true;
            if (_sourceModeRadio != null)
                _sourceModeRadio.IsChecked = vm.Surface == FilePreviewSurface.Source;
            if (_secondaryModeRadio != null)
                _secondaryModeRadio.IsChecked = vm.Surface != FilePreviewSurface.Source;
        }
        finally
        {
            _syncingModeRadios = false;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => AttachViewModelIfNeeded();

    private void OnLoaded(object? sender, EventArgs e) => ApplyContent();

    private void OnTextEditorTextChanged(object? sender, EventArgs e)
    {
        if (_applyingEditorText)
            return;

        var vm = ResolveViewModel();
        if (vm == null || vm.Surface != FilePreviewSurface.Source)
            return;

        vm.SourceText = TextEditor.Text;
        _renderedSourceFingerprint = null;
    }

    private async void OnTextEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || (e.KeyModifiers & KeyModifiers.Control) == 0)
            return;

        e.Handled = true;
        await SaveCurrentTextAsync();
    }

    private async System.Threading.Tasks.Task SaveCurrentTextAsync()
    {
        var vm = ResolveViewModel();
        if (vm == null || string.IsNullOrWhiteSpace(vm.FilePath))
            return;

        var filePath = vm.FilePath;
        var fileName = vm.FileName;
        var text = TextEditor.Text;

        try
        {
            await File.WriteAllTextAsync(filePath, text, System.Text.Encoding.UTF8)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_resourcesReleased)
                    return;

                var currentVm = ResolveViewModel();
                if (currentVm != null && string.Equals(currentVm.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    currentVm.SourceText = text;

                _renderedSourceFingerprint = null;
                _eventBus?.Publish(new StatusMessageEvent($"保存成功: {fileName}"));
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                _eventBus?.Publish(new StatusMessageEvent($"保存失败: {ex.Message}")));
        }
    }

    private void OnAttachedToVisualTree(object? sender, EventArgs e)
    {
        if (_eventBus != null)
        {
            _eventBus.Unsubscribe<WorkspaceFileChangedEvent>(OnWorkspaceFileChanged);
            _eventBus.Subscribe<WorkspaceFileChangedEvent>(OnWorkspaceFileChanged);
        }

        if (!_textContentReady)
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

        if (e.PropertyName is nameof(FilePreviewViewModel.Surface)
            or nameof(FilePreviewViewModel.TableData)
            or nameof(FilePreviewViewModel.HasMarkdownPreview)
            or nameof(FilePreviewViewModel.HasDataTable))
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

        var showSource = vm.Surface == FilePreviewSurface.Source;
        var showTable = vm.Surface == FilePreviewSurface.Table;
        var showRendered = vm.Surface == FilePreviewSurface.Rendered;

        TextEditor.IsVisible = showSource;
        TextEditor.IsHitTestVisible = showSource;
        TextEditor.ZIndex = showSource ? 1 : 0;

        TableContent.IsVisible = showTable;
        TableContent.IsHitTestVisible = showTable;
        TableContent.ZIndex = showTable ? 1 : 0;

        MarkdownPreview.IsVisible = showRendered;
        MarkdownPreview.IsHitTestVisible = showRendered;
        MarkdownPreview.ZIndex = showRendered ? 1 : 0;

        if (showSource)
        {
            _applyingEditorText = true;
            try
            {
                vm.ApplySourceTextToEditor(TextEditor);
            }
            finally
            {
                _applyingEditorText = false;
            }

            ApplySyntaxHighlighting(TextEditor, vm.FilePath);
            _textContentReady = true;
            return;
        }

        _textContentReady = false;

        if (showTable)
        {
            var tableView = EnsureTableView(vm);
            if (tableView == null)
                return;

            if (!ReferenceEquals(TableContent.Content, tableView))
                TableContent.Content = tableView;

            tableView.NotifyTabActivated();
            return;
        }

        if (showRendered)
            ApplyRenderedPreview(vm, force: true);
    }

    private void ApplyRenderedPreview(FilePreviewViewModel vm, bool force)
    {
        var markdown = vm.GetSourceText();
        var fingerprint = $"{vm.FilePath}\0{markdown.Length}\0{markdown.GetHashCode(StringComparison.Ordinal)}";
        if (!force && string.Equals(_renderedSourceFingerprint, fingerprint, StringComparison.Ordinal))
            return;

        _renderedSourceFingerprint = fingerprint;
        MarkdownPreview.ShowDocument(markdown, vm.FileName, vm.FilePath);
    }

    private void ApplySyntaxHighlighting(AvaloniaEdit.TextEditor editor, string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || editor.Text.Length > 16 * 1024)
        {
            if (_highlightedFilePath != filePath)
            {
                editor.SyntaxHighlighting = null;
                AvaloniaEditEditorHelper.ApplyTheme(editor);
                _highlightedFilePath = filePath;
            }

            return;
        }

        if (string.Equals(_highlightedFilePath, filePath, StringComparison.OrdinalIgnoreCase)
            && editor.SyntaxHighlighting != null)
        {
            return;
        }

        editor.SyntaxHighlighting = CodeSyntaxHighlighting.GetForFile(filePath);
        AvaloniaEditEditorHelper.ApplyTheme(editor);
        _highlightedFilePath = filePath;
    }
}
