using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Browser.Services;
using ZeroFall.Traffic.Capture;
using ZeroFall.Traffic.Ingest;

namespace ZeroFall.Browser.ViewModels;

public partial class BrowserTabViewModel : BrowserTabViewModelBase
{
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private readonly IEventBus? _eventBus;

    public ICdpBridge? CdpBridge { get; set; }

    public IBrowserTabManager? TabManager { get; set; }

    public ITrafficCaptureSink? CaptureSink { get; set; }

    public static List<string> ZoomLevelDisplay { get; } = new() { "50%", "75%", "90%", "100%", "110%", "125%", "150%", "200%" };

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private string _tabId = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
    private bool _canGoBack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
    private bool _canGoForward;

    [ObservableProperty]
    private double _zoomFactor = 1.0;

    [ObservableProperty]
    private int _pageSessionId;

    [ObservableProperty]
    private string _topLevelUrl = string.Empty;

    public BrowserTabViewModel(string? initialAddress = null, IEventBus? eventBus = null)
    {
        _eventBus = eventBus;
        PropertyChanged += OnBrowserTabPropertyChanged;
        if (!string.IsNullOrWhiteSpace(initialAddress))
        {
            Address = initialAddress.Trim();
            PushHistory(Address);
        }
    }

    private void OnBrowserTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Title)) return;
        if (string.IsNullOrEmpty(TabId)) return;
        _eventBus?.Publish(new BrowserContentTabTitleChangedEvent(TabId, Title));
    }

    public Uri GetNavigateUri()
    {
        var text = Address.Trim();
        if (string.IsNullOrEmpty(text))
            return new Uri("about:blank");

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute))
            return absolute;

        if (Uri.TryCreate("https://" + text, UriKind.Absolute, out var withScheme))
            return withScheme;

        return new Uri("about:blank");
    }

    /// <summary>仅更新地址栏显示，不写入历史栈（导航进行中实时同步）。</summary>
    public void SyncAddress(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (IsBlankUrl(url))
            return;
        if (string.Equals(Address, url, StringComparison.OrdinalIgnoreCase))
            return;
        Address = url;
    }

    public void ApplyNavigated(Uri uri, string? documentTitle)
    {
        if (IsBlankUri(uri))
            return;

        var url = uri.ToString();
        // 同 URL 刷新/重载不递增：网站树按 PageSessionId 分会话，否则 F5 会出现「新 tab (2)」。
        if (!IsSameDocumentUrl(uri, TopLevelUrl))
            PageSessionId++;
        TopLevelUrl = url;
        Address = url;
        PushHistory(url);

        if (!string.IsNullOrEmpty(TabId))
            _eventBus?.Publish(new BrowserTabDocumentNavigatedEvent(TabId, PageSessionId, url));

        if (!string.IsNullOrWhiteSpace(documentTitle))
            Title = documentTitle!;
        else if (!string.IsNullOrWhiteSpace(uri.Host))
            Title = uri.Host;
        else
            Title = "新标签页";
    }

    public void ApplyDocumentTitle(string? documentTitle)
    {
        if (string.IsNullOrWhiteSpace(documentTitle)) return;
        var t = documentTitle.Trim();
        if (string.Equals(Title, t, StringComparison.Ordinal))
            return;
        Title = t;
    }

    public void ApplyFavicon(byte[]? imageBytes)
    {
        if (string.IsNullOrEmpty(TabId))
            return;
        _eventBus?.Publish(new BrowserContentTabFaviconChangedEvent(TabId, imageBytes));
    }

    public void ClearFavicon() => ApplyFavicon(null);

    private static bool IsBlankUri(Uri uri) =>
        uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals("blank", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlankUrl(string url) =>
        url.Equals("about:blank", StringComparison.OrdinalIgnoreCase);

    /// <summary>比较 scheme/host/path/query，忽略 fragment；用于识别刷新而非新文档导航。</summary>
    private static bool IsSameDocumentUrl(Uri uri, string? previousUrl)
    {
        if (string.IsNullOrWhiteSpace(previousUrl))
            return false;
        if (!Uri.TryCreate(previousUrl, UriKind.Absolute, out var prev))
            return false;

        const UriComponents parts = UriComponents.SchemeAndServer
                                    | UriComponents.UserInfo
                                    | UriComponents.Path
                                    | UriComponents.Query;
        return Uri.Compare(uri, prev, parts, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private void PushHistory(string url)
    {
        if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(url);
        _historyIndex = _history.Count - 1;
        UpdateNavigationState();
    }

    private void UpdateNavigationState()
    {
        CanGoBack = _historyIndex > 0;
        CanGoForward = _historyIndex < _history.Count - 1;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (_historyIndex <= 0) return;
        _historyIndex--;
        Address = _history[_historyIndex];
        UpdateNavigationState();
        NavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (_historyIndex >= _history.Count - 1) return;
        _historyIndex++;
        Address = _history[_historyIndex];
        UpdateNavigationState();
        NavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Refresh()
    {
        NavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Go()
    {
        NavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void NewTab()
    {
        _eventBus?.Publish(new OpenBrowserTabRequestedEvent(string.Empty, "新标签页"));
    }

    [RelayCommand]
    private void CloseTab()
    {
        if (!string.IsNullOrEmpty(TabId))
            _eventBus?.Publish(new CloseContentTabRequestedEvent(TabId));
    }

    [RelayCommand]
    private void SetZoom(int percentage)
    {
        ZoomFactor = percentage / 100.0;
    }

    public void SubmitCapture(TrafficCaptureRecord capture) =>
        CaptureSink?.Submit(capture);

    public void UpdateTrafficBody(
        string entryId,
        string requestBody,
        string responseBody,
        byte[]? requestBodyRaw = null,
        byte[]? responseBodyRaw = null)
    {
        CaptureSink?.SubmitBodyUpdate(new TrafficBodyCaptureUpdate(
            entryId,
            requestBody,
            responseBody,
            requestBodyRaw,
            responseBodyRaw));
    }

    public void OpenInNewTab(Uri uri)
    {
        _eventBus?.Publish(new OpenBrowserTabRequestedEvent(uri.ToString()));
    }

    public event EventHandler? NavigationRequested;

    /// <summary>供视图订阅全局事件（如代理变更重建 WebView）。</summary>
    public IEventBus? EventBus => _eventBus;
}
