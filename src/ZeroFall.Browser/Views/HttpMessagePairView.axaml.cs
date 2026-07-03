using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaHex;
using AvaloniaHex.Document;
using ZeroFall.Browser.Services;

namespace ZeroFall.Browser.Views;

public partial class HttpMessagePairView : UserControl
{
    public static readonly StyledProperty<string> RequestTextProperty =
        AvaloniaProperty.Register<HttpMessagePairView, string>(nameof(RequestText), string.Empty);

    public static readonly StyledProperty<string> ResponseTextProperty =
        AvaloniaProperty.Register<HttpMessagePairView, string>(nameof(ResponseText), string.Empty);

    public static readonly StyledProperty<bool> RequestIsReadOnlyProperty =
        AvaloniaProperty.Register<HttpMessagePairView, bool>(nameof(RequestIsReadOnly), true);

    public static readonly StyledProperty<string> ReplayRealHostProperty =
        AvaloniaProperty.Register<HttpMessagePairView, string>(nameof(ReplayRealHost), string.Empty);

    public static readonly StyledProperty<bool> ReplayIsHttpsProperty =
        AvaloniaProperty.Register<HttpMessagePairView, bool>(nameof(ReplayIsHttps));

    public static readonly StyledProperty<bool> ReplayRealHostIsReadOnlyProperty =
        AvaloniaProperty.Register<HttpMessagePairView, bool>(nameof(ReplayRealHostIsReadOnly));

    public static readonly StyledProperty<bool> ReplayHeaderIsInteractiveProperty =
        AvaloniaProperty.Register<HttpMessagePairView, bool>(nameof(ReplayHeaderIsInteractive), true);

    public static readonly StyledProperty<string> ResponseHeaderRightTextProperty =
        AvaloniaProperty.Register<HttpMessagePairView, string>(nameof(ResponseHeaderRightText), "—");

    public static readonly StyledProperty<bool> ShowResponseHeaderRightTextProperty =
        AvaloniaProperty.Register<HttpMessagePairView, bool>(nameof(ShowResponseHeaderRightText));

    public static readonly StyledProperty<bool> ShowResponseProperty =
        AvaloniaProperty.Register<HttpMessagePairView, bool>(nameof(ShowResponse), true);

    public static readonly StyledProperty<bool> ShowSendToReplayMenuProperty =
        AvaloniaProperty.Register<HttpMessagePairView, bool>(nameof(ShowSendToReplayMenu));

    public static readonly StyledProperty<ICommand?> SendToReplayCommandProperty =
        AvaloniaProperty.Register<HttpMessagePairView, ICommand?>(nameof(SendToReplayCommand));

    public static readonly StyledProperty<byte[]?> ResponseBodyRawProperty =
        AvaloniaProperty.Register<HttpMessagePairView, byte[]?>(nameof(ResponseBodyRaw));

    public static readonly StyledProperty<HttpRequestEditorMenuScope> RequestEditorMenuScopeProperty =
        AvaloniaProperty.Register<HttpMessagePairView, HttpRequestEditorMenuScope>(
            nameof(RequestEditorMenuScope),
            HttpRequestEditorMenuScope.None);

    public static readonly RoutedEvent<RoutedEventArgs> RequestTextEditedEvent =
        RoutedEvent.Register<HttpMessagePairView, RoutedEventArgs>(nameof(RequestTextEdited), RoutingStrategies.Bubble);

    private readonly IHttpDocumentEditorRules _editorRules = HttpDocumentEditorRules.Instance;
    private TextEditor? _requestEditor;
    private TextEditor? _responseEditor;
    private HexEditor? _responseHexEditor;
    private TabControl? _responseTabControl;
    private TabItem? _responseHexTabItem;
    private IBinaryDocument? _responseHexDocument;
    private byte[]? _cachedResponseBodyRaw;
    private bool _syncingEditors;
    private bool _requestHighlightScheduled;

    public string RequestText
    {
        get => GetValue(RequestTextProperty);
        set => SetValue(RequestTextProperty, value);
    }

    public string ResponseText
    {
        get => GetValue(ResponseTextProperty);
        set => SetValue(ResponseTextProperty, value);
    }

    public bool RequestIsReadOnly
    {
        get => GetValue(RequestIsReadOnlyProperty);
        set => SetValue(RequestIsReadOnlyProperty, value);
    }

    public string ReplayRealHost
    {
        get => GetValue(ReplayRealHostProperty);
        set => SetValue(ReplayRealHostProperty, value);
    }

    public bool ReplayIsHttps
    {
        get => GetValue(ReplayIsHttpsProperty);
        set => SetValue(ReplayIsHttpsProperty, value);
    }

    public bool ReplayRealHostIsReadOnly
    {
        get => GetValue(ReplayRealHostIsReadOnlyProperty);
        set => SetValue(ReplayRealHostIsReadOnlyProperty, value);
    }

    public bool ReplayHeaderIsInteractive
    {
        get => GetValue(ReplayHeaderIsInteractiveProperty);
        set => SetValue(ReplayHeaderIsInteractiveProperty, value);
    }

    public string ResponseHeaderRightText
    {
        get => GetValue(ResponseHeaderRightTextProperty);
        set => SetValue(ResponseHeaderRightTextProperty, value);
    }

    public bool ShowResponseHeaderRightText
    {
        get => GetValue(ShowResponseHeaderRightTextProperty);
        set => SetValue(ShowResponseHeaderRightTextProperty, value);
    }

    public bool ShowResponse
    {
        get => GetValue(ShowResponseProperty);
        set => SetValue(ShowResponseProperty, value);
    }

    public bool ShowSendToReplayMenu
    {
        get => GetValue(ShowSendToReplayMenuProperty);
        set => SetValue(ShowSendToReplayMenuProperty, value);
    }

    public ICommand? SendToReplayCommand
    {
        get => GetValue(SendToReplayCommandProperty);
        set => SetValue(SendToReplayCommandProperty, value);
    }

    public byte[]? ResponseBodyRaw
    {
        get => GetValue(ResponseBodyRawProperty);
        set => SetValue(ResponseBodyRawProperty, value);
    }

    public HttpRequestEditorMenuScope RequestEditorMenuScope
    {
        get => GetValue(RequestEditorMenuScopeProperty);
        set => SetValue(RequestEditorMenuScopeProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? RequestTextEdited
    {
        add => AddHandler(RequestTextEditedEvent, value);
        remove => RemoveHandler(RequestTextEditedEvent, value);
    }

    public HttpMessagePairView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        Unloaded += OnUnloaded;
    }

    /// <summary>将已写入的 StyledProperty 同步到 AvaloniaEdit / Hex 控件（控件尚未 Loaded 时会跳过，Loaded 后再调一次）。</summary>
    public void SyncEditorsFromProperties()
    {
        ApplyRequestTextToEditor();
        ApplyResponseTextToEditor();
        CacheResponseBodyRaw();
        UpdateResponseHexTabVisibility();
        ConfigureResponseTabStrip();
        if (IsHexTabSelected())
            ScheduleRefreshHexEditorLayout();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RequestTextProperty)
            ApplyRequestTextToEditor();
        else if (change.Property == ResponseTextProperty)
            ApplyResponseTextToEditor();
        else if (change.Property == ResponseBodyRawProperty)
        {
            CacheResponseBodyRaw();
            UpdateResponseHexTabVisibility();
            if (IsHexTabSelected())
                ScheduleRefreshHexEditorLayout();
            ApplyResponseTextToEditor();
        }
        else if (change.Property == ShowResponseProperty)
            UpdateResponsePanelLayout();
        else if (change.Property == ShowResponseHeaderRightTextProperty
                 || change.Property == ResponseHeaderRightTextProperty)
            ConfigureResponseTabStrip();
    }

    private void UpdateResponsePanelLayout()
    {
        Grid.SetColumnSpan(RequestPanel, ShowResponse ? 1 : 3);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _requestEditor = RequestEditor;
        _responseEditor = ResponseEditor;
        _responseHexEditor = ResponseHexEditor;
        _responseTabControl = ResponseTabControl;
        _responseHexTabItem = ResponseHexTabItem;
        if (_responseTabControl is not null)
            _responseTabControl.SelectionChanged += OnResponseTabSelectionChanged;
        if (_requestEditor is not null)
            _requestEditor.TextChanged += OnRequestEditorTextChanged;

        if (Application.Current is not null)
            Application.Current.ActualThemeVariantChanged += OnThemeChanged;

        ResponsePanel.SizeChanged += OnResponsePanelSizeChanged;
        SyncEditorsFromProperties();
        UpdateResponsePanelLayout();
        ApplyHighlighting();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
        Dispatcher.UIThread.Post(SyncEditorsFromProperties, DispatcherPriority.Loaded);

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
        ClearResponseHexDocument();

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        ResponsePanel.SizeChanged -= OnResponsePanelSizeChanged;

        if (_requestEditor is not null)
            _requestEditor.TextChanged -= OnRequestEditorTextChanged;

        if (_responseTabControl is not null)
            _responseTabControl.SelectionChanged -= OnResponseTabSelectionChanged;

        if (Application.Current is not null)
            Application.Current.ActualThemeVariantChanged -= OnThemeChanged;

        ClearResponseHexDocument();
    }

    private void OnResponsePanelSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!IsHexTabSelected())
            return;

        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5
            || e.NewSize.Height > e.PreviousSize.Height)
            InvalidateHexEditorLayout();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        HttpHighlighting.InvalidateCache();
        ApplyHighlighting();
    }

    private void OnResponseTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsHexTabSelected())
            ScheduleRefreshHexEditorLayout();
        else
        {
            ClearResponseHexDocument();
            ApplyResponseTextToEditor();
        }
    }

    private bool IsHexTabSelected() =>
        _responseHexTabItem is not null
        && ReferenceEquals(_responseTabControl?.SelectedItem, _responseHexTabItem);

    private bool HasResponseBody() => _cachedResponseBodyRaw is { Length: > 0 };

    private void UpdateResponseHexTabVisibility()
    {
        if (_responseHexTabItem is null)
            return;

        var visible = HasResponseBody();
        _responseHexTabItem.IsVisible = visible;

        if (!visible && IsHexTabSelected())
        {
            ClearResponseHexDocument();
            if (_responseTabControl is not null)
                _responseTabControl.SelectedIndex = 0;
        }
    }

    private void ConfigureResponseTabStrip()
    {
        if (_responseTabControl is null)
            return;

        var tabStrip = _responseTabControl.GetVisualDescendants().OfType<TabStrip>().FirstOrDefault();
        if (tabStrip is null)
            return;

        tabStrip.Margin = new Thickness(0, 0, ShowResponseHeaderRightText ? 72 : 0, 0);
    }

    private void CacheResponseBodyRaw()
    {
        _cachedResponseBodyRaw = ResponseBodyRaw is { Length: > 0 } raw
            ? (byte[])raw.Clone()
            : null;
    }

    private void ScheduleRefreshHexEditorLayout()
    {
        Dispatcher.UIThread.Post(RefreshHexEditorLayout, DispatcherPriority.Loaded);
    }

    private void RefreshHexEditorLayout()
    {
        if (_responseHexEditor is null || !IsHexTabSelected())
            return;

        ClearResponseHexDocument();
        if (_cachedResponseBodyRaw is not { Length: > 0 } raw)
        {
            _responseHexEditor.Document = null;
            return;
        }

        _responseHexDocument = new MemoryBinaryDocument(raw, isReadOnly: true);
        _responseHexEditor.Document = _responseHexDocument;
        // 窄面板下固定 16 字节/行会导致右侧 Hex/ASCII 列被裁切；交给 HexView 按宽度自适应。
        _responseHexEditor.HexView.BytesPerLine = null;
        InvalidateHexEditorLayout();
    }

    private void InvalidateHexEditorLayout()
    {
        if (_responseHexEditor is null)
            return;

        _responseHexEditor.HexView.BytesPerLine = null;
        _responseHexEditor.InvalidateMeasure();
        _responseHexEditor.InvalidateArrange();
        _responseHexEditor.HexView.InvalidateMeasure();
        _responseHexEditor.HexView.InvalidateArrange();
    }

    private void ApplyRequestTextToEditor()
    {
        if (_requestEditor is null)
            return;

        var text = RequestText ?? string.Empty;
        if (string.Equals(_requestEditor.Text, text, StringComparison.Ordinal))
            return;

        _syncingEditors = true;
        try
        {
            _requestEditor.Text = text;
        }
        finally
        {
            _syncingEditors = false;
        }

        ApplyRequestHighlighting();
    }

    private void ApplyResponseTextToEditor()
    {
        if (_responseEditor is null || IsHexTabSelected())
            return;

        var text = ResponseText ?? string.Empty;
        if (string.Equals(_responseEditor.Text, text, StringComparison.Ordinal))
            return;

        _syncingEditors = true;
        try
        {
            _responseEditor.Text = text;
        }
        finally
        {
            _syncingEditors = false;
        }

        ApplyResponseHighlighting();
    }

    private void ClearResponseHexDocument()
    {
        if (_responseHexEditor is not null)
            _responseHexEditor.Document = null;

        if (_responseHexDocument is IDisposable disposable)
            disposable.Dispose();

        _responseHexDocument = null;
    }

    private void OnRequestEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncingEditors || RequestIsReadOnly || _requestEditor is null)
            return;

        var text = _requestEditor.Text ?? string.Empty;
        if (string.Equals(RequestText, text, StringComparison.Ordinal))
            return;

        _syncingEditors = true;
        try
        {
            SetValue(RequestTextProperty, text);
        }
        finally
        {
            _syncingEditors = false;
        }

        RaiseEvent(new RoutedEventArgs(RequestTextEditedEvent));
        ScheduleRequestHighlighting();
    }

    private void ScheduleRequestHighlighting()
    {
        if (_requestHighlightScheduled)
            return;
        _requestHighlightScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _requestHighlightScheduled = false;
            ApplyRequestHighlighting();
        }, DispatcherPriority.Background);
    }

    private void ApplyHighlighting()
    {
        ApplyRequestHighlighting();
        ApplyResponseHighlighting();
        AvaloniaEditEditorHelper.ApplyTheme(_requestEditor);
        AvaloniaEditEditorHelper.ApplyTheme(_responseEditor);
    }

    private void ApplyRequestHighlighting() =>
        HttpEditorRules.ApplyHighlighting(_requestEditor, _requestEditor?.Text ?? RequestText ?? string.Empty, _editorRules);

    private void ApplyResponseHighlighting() =>
        HttpEditorRules.ApplyHighlighting(
            _responseEditor,
            _responseEditor?.Text ?? ResponseText ?? string.Empty,
            _editorRules);

    private void RequestCopy_Click(object? sender, RoutedEventArgs e) =>
        CopyEditorText(_requestEditor);

    private void RequestSelectAll_Click(object? sender, RoutedEventArgs e) =>
        _requestEditor?.SelectAll();

    private void RequestContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        RemoveExtensionMenuItems(menu);
        if (RequestEditorMenuScope == HttpRequestEditorMenuScope.None
            || RequestIsReadOnly
            || _requestEditor is null)
            return;

        var context = CreateRequestEditorMenuContext();
        var descriptors = HttpRequestEditorMenuRegistry.Instance.GetItems(context);
        if (descriptors.Count == 0)
            return;

        var insertIndex = FindRequestMenuInsertIndex(menu);
        foreach (var descriptor in descriptors)
            insertIndex = InsertMenuDescriptor(menu, descriptor, insertIndex);
    }

    private HttpRequestEditorMenuContext CreateRequestEditorMenuContext() =>
        new()
        {
            Editor = _requestEditor!,
            Scope = RequestEditorMenuScope,
            IsReadOnly = RequestIsReadOnly,
            SetRequestText = SetRequestTextFromEditorAction
        };

    private void SetRequestTextFromEditorAction(string text)
    {
        _syncingEditors = true;
        try
        {
            SetValue(RequestTextProperty, text);
            if (_requestEditor is not null && !string.Equals(_requestEditor.Text, text, StringComparison.Ordinal))
                _requestEditor.Text = text;
        }
        finally
        {
            _syncingEditors = false;
        }

        RaiseEvent(new RoutedEventArgs(RequestTextEditedEvent));
        ScheduleRequestHighlighting();
    }

    private static void RemoveExtensionMenuItems(ContextMenu menu)
    {
        for (var i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is MenuItem { Tag: HttpRequestEditorMenuTags.Extension })
                menu.Items.RemoveAt(i);
        }
    }

    private static int FindRequestMenuInsertIndex(ContextMenu menu)
    {
        for (var i = 0; i < menu.Items.Count; i++)
        {
            if (menu.Items[i] is MenuItem { Header: string header }
                && string.Equals(header, "复制", StringComparison.Ordinal))
                return i;
        }

        return menu.Items.Count;
    }

    private int InsertMenuDescriptor(
        ContextMenu menu,
        HttpRequestEditorMenuDescriptor descriptor,
        int insertIndex)
    {
        if (descriptor.IsSeparator)
        {
            menu.Items.Insert(insertIndex, new Separator { Tag = HttpRequestEditorMenuTags.Extension });
            return insertIndex + 1;
        }

        var item = new MenuItem
        {
            Header = descriptor.Header,
            IsEnabled = descriptor.IsEnabled,
            Tag = HttpRequestEditorMenuTags.Extension,
            Classes = { "Small" }
        };

        if (descriptor.Children is { Count: > 0 })
        {
            foreach (var child in descriptor.Children)
                InsertMenuDescriptor(item, child, item.Items.Count);
        }
        else if (descriptor.Execute is not null)
        {
            var execute = descriptor.Execute;
            item.Click += (_, _) =>
            {
                if (_requestEditor is null)
                    return;

                execute(CreateRequestEditorMenuContext());
            };
        }

        menu.Items.Insert(insertIndex, item);
        return insertIndex + 1;
    }

    private void InsertMenuDescriptor(
        MenuItem parent,
        HttpRequestEditorMenuDescriptor descriptor,
        int insertIndex)
    {
        if (descriptor.IsSeparator)
        {
            parent.Items.Insert(insertIndex, new Separator { Tag = HttpRequestEditorMenuTags.Extension });
            return;
        }

        var item = new MenuItem
        {
            Header = descriptor.Header,
            IsEnabled = descriptor.IsEnabled,
            Tag = HttpRequestEditorMenuTags.Extension,
            Classes = { "Small" }
        };

        if (descriptor.Children is { Count: > 0 })
        {
            foreach (var child in descriptor.Children)
                InsertMenuDescriptor(item, child, item.Items.Count);
        }
        else if (descriptor.Execute is not null)
        {
            var execute = descriptor.Execute;
            item.Click += (_, _) =>
            {
                if (_requestEditor is null)
                    return;

                execute(CreateRequestEditorMenuContext());
            };
        }

        parent.Items.Insert(insertIndex, item);
    }

    private void ResponseCopy_Click(object? sender, RoutedEventArgs e) =>
        CopyEditorText(_responseEditor);

    private void ResponseSelectAll_Click(object? sender, RoutedEventArgs e) =>
        _responseEditor?.SelectAll();

    private static void CopyEditorText(TextEditor? editor)
    {
        if (editor?.TextArea is not { } ta)
            return;

        var text = ta.Selection.GetText();
        if (string.IsNullOrEmpty(text))
            text = editor.Text;
        TopLevel.GetTopLevel(editor)?.Clipboard?.SetTextAsync(text);
    }
}
