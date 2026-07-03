using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZeroFall.Base.Events;
using ZeroFall.Browser.ComInterop;
using ZeroFall.Browser.Services;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;
using ZeroFall.Traffic;
using ZeroFall.Traffic.Capture;

namespace ZeroFall.Browser.Views;

public partial class BrowserTabView : UserControl, INonReloadableTabHost, ITabContentReleasable
{
    private static readonly SemaphoreSlim GlobalRecreateLock = new(1, 1);

    private bool _webEventsAttached;
    private bool _nativeNetworkAttached;
    private bool _recreatingWebView;
    private WebView2NativeWrapper? _webView2Wrapper;
    private BrowserTabViewModel? _tabVm;
    private NativeWebView? _webView;
    private bool _webViewCreateScheduled;
    private bool _webViewCreating;
    private bool _disposed;
    private bool _adapterReady;
    private bool _syncingAddressBar;
    private EventHandler? _webViewHostLayoutHandler;
    private int _attachRetryAttempts;

    public BrowserTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    public void OnTabBecameVisible()
    {
        RefreshWebViewLayout();
        if (_webView is not null)
        {
            if (_adapterReady)
                TryNavigateToViewModel(force: false);
            else
                ScheduleAdapterProbe();
            return;
        }

        AttachWebViewWhenReady();
    }

    public void OnTabBecameHidden()
    {
        StopWaitingForHostLayout();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsEffectivelyVisible && StartupPerformance.IsLayoutReady)
            AttachWebViewWhenReady();
    }

    /// <summary>由 <see cref="BrowserStartupPreparer"/> 在启动最后调用。</summary>
    public void AttachWebViewWhenReady()
    {
        if (_disposed)
            return;

        _webViewCreateScheduled = false;
        ScheduleWebViewCreation();
    }

    private void ScheduleWebViewCreation()
    {
        if (_webView is not null || _disposed || _webViewCreateScheduled)
            return;

        if (!StartupPerformance.IsLayoutReady)
            return;

        _webViewCreateScheduled = true;
        TryCreateWebViewWhenHostReady();
        if (_webView is null)
            StartupPerformance.RunOnUiIdle(TryCreateWebViewWhenHostReady);
    }

    private bool IsWebViewHostReady()
    {
        if (!IsLoaded || !IsEffectivelyVisible)
            return false;
        if (TopLevel.GetTopLevel(this) is null)
            return false;

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = WebViewHost.Bounds;
        return bounds.Width > 8 && bounds.Height > 8;
    }

    private void TryCreateWebViewWhenHostReady()
    {
        if (_disposed || _webView is not null || _webViewCreating)
            return;

        if (!IsLoaded)
        {
            _webViewCreateScheduled = false;
            return;
        }

        if (!IsWebViewHostReady())
        {
            if (_webViewHostLayoutHandler is null)
            {
                _webViewHostLayoutHandler = OnWebViewHostLayoutUpdated;
                WebViewHost.LayoutUpdated += _webViewHostLayoutHandler;
                LayoutUpdated += _webViewHostLayoutHandler;
            }

            _webViewCreateScheduled = false;
            return;
        }

        StopWaitingForHostLayout();
        EnsureWebViewCreated();
    }

    private void OnWebViewHostLayoutUpdated(object? sender, EventArgs e) =>
        TryCreateWebViewWhenHostReady();

    private void StopWaitingForHostLayout()
    {
        if (_webViewHostLayoutHandler is null)
            return;

        WebViewHost.LayoutUpdated -= _webViewHostLayoutHandler;
        LayoutUpdated -= _webViewHostLayoutHandler;
        _webViewHostLayoutHandler = null;
    }

    private void EnsureWebViewCreated()
    {
        if (_webView is not null || _disposed || _webViewCreating)
            return;

        if (!IsWebViewHostReady())
        {
            _webViewCreateScheduled = false;
            TryCreateWebViewWhenHostReady();
            return;
        }

        _webViewCreating = true;
        _ = EnsureWebViewCreatedAsync();
    }

    private async Task EnsureWebViewCreatedAsync()
    {
        try
        {
            SetWebViewStatus("正在初始化浏览器...");
            await WebView2CreationCoordinator.WaitForInitAsync().ConfigureAwait(true);

            if (_disposed || _webView is not null || !IsWebViewHostReady())
            {
                WebView2CreationCoordinator.ReleaseInit();
                return;
            }

            WebView2CreationCoordinator.ArmInitRelease(20_000, () =>
                Console.Error.WriteLine("[BrowserTabView] WebView2 init gate timeout"));

            _webView = CreateBrowserWebView();
            EnsureWebShellEvents();
            WebViewHost.Children.Add(_webView);
            _webView.ZIndex = 0;
            WebViewStatusText.ZIndex = 1;

            SetWebViewStatus("正在初始化 WebView...");
            BeginNavigationPipeline();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BrowserTabView] WebView create failed: {ex}");
            WebView2CreationCoordinator.ReleaseInit();
            TeardownFailedWebView();
            StartupPerformance.RunAfterDelay(TryCreateWebViewWhenHostReady, delayMs: 2000);
        }
        finally
        {
            _webViewCreating = false;
            _webViewCreateScheduled = false;
        }
    }

    private void TeardownFailedWebView()
    {
        _webViewCreateScheduled = false;
        _adapterReady = false;
        if (_webView is null)
            return;

        DetachWebShellEvents();
        _webView.EnvironmentRequested -= OnWebViewEnvironmentRequested;
        try { WebViewHost.Children.Remove(_webView); } catch { }
        DisposeWebViewControl(_webView);
        _webView = null;
    }

    private static NativeWebView CreateBrowserWebView()
    {
        var w = new NativeWebView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        w.EnvironmentRequested += OnWebViewEnvironmentRequested;
        return w;
    }

    /// <summary>
    /// WebView2 仅在创建环境时读取代理；运行时切换通过重建 <see cref="NativeWebView"/>（见 <see cref="OnProxySettingsChanged"/>）。
    /// 代理来源统一由 <see cref="WebView2ProxyOptions"/> 提供。
    /// </summary>
    private static void OnWebViewEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (e is not WindowsWebView2EnvironmentRequestedEventArgs win)
            return;

        var profileDir = Path.Combine(Path.GetTempPath(), "ZeroFall", "BrowserWebView2");
        Directory.CreateDirectory(profileDir);
        win.UserDataFolder = profileDir;

        var browserArgs = "--disable-features=msSmartScreenProtection";
        var proxy = WebView2ProxyOptions.Resolve();
        var existing = win.AdditionalBrowserArguments?.Trim();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            proxy = proxy.Trim();
            if (proxy.Length > 0)
                browserArgs = $"{browserArgs} --proxy-server={proxy}";
        }

        win.AdditionalBrowserArguments = string.IsNullOrEmpty(existing)
            ? browserArgs
            : $"{existing} {browserArgs}";
    }

    /// <summary>
    /// TabControl 的 Content 模板会把选中项 DataContext 设为 Dock 标签项 VM，
    /// 覆盖构造时设置的浏览器 VM；<see cref="Tag"/> 保留页面 VM（BrowserModule 创建时写入）。
    /// </summary>
    private BrowserTabViewModel? ResolveTabViewModel()
    {
        if (DataContext is BrowserTabViewModel vm)
            return vm;
        if (Tag is BrowserTabViewModel tagVm)
            return tagVm;
        return null;
    }

    private void OnDataContextChanged(object? sender, EventArgs e) =>
        AttachTabViewModelIfNeeded();

    private void AttachTabViewModelIfNeeded()
    {
        var next = ResolveTabViewModel();
        if (next is null)
            return;

        if (!ReferenceEquals(_tabVm, next))
        {
            if (_tabVm is not null)
            {
                _tabVm.NavigationRequested -= OnNavigationRequested;
                _tabVm.PropertyChanged -= OnTabVmPropertyChanged;
                UnsubscribeProxyChanged();
            }

            _tabVm = next;
            _tabVm.NavigationRequested += OnNavigationRequested;
            _tabVm.PropertyChanged += OnTabVmPropertyChanged;
            SubscribeProxyChanged();
            SyncZoomComboFromViewModel(_tabVm);
        }

        PushAddressToBar();
    }

    private void PushAddressToBar()
    {
        if (_tabVm is null || _syncingAddressBar)
            return;

        var text = _tabVm.Address ?? string.Empty;
        if (string.Equals(AddressBar.Text, text, StringComparison.Ordinal))
            return;

        _syncingAddressBar = true;
        try
        {
            AddressBar.Text = text;
        }
        finally
        {
            _syncingAddressBar = false;
        }
    }

    private void SubscribeProxyChanged()
    {
        _tabVm?.EventBus?.Subscribe<ProxyRuntimeStateChangedEvent>(OnProxyRuntimeStateChanged);
    }

    private void UnsubscribeProxyChanged()
    {
        _tabVm?.EventBus?.Unsubscribe<ProxyRuntimeStateChangedEvent>(OnProxyRuntimeStateChanged);
    }

    private void OnProxyRuntimeStateChanged(ProxyRuntimeStateChangedEvent e)
    {
        WebView2ProxyOptions.SetFromGatewayState(e.State);
        if (!StartupPerformance.IsLayoutReady || _webView is null)
            return;

        BrowserWebViewRecreateQueue.Enqueue(() =>
        {
            _ = RecreateWebViewForProxyChangeAsync();
        });
    }

    private async Task RecreateWebViewForProxyChangeAsync()
    {
        if (_recreatingWebView || !IsLoaded || _webView is null)
            return;

        await GlobalRecreateLock.WaitAsync();
        _recreatingWebView = true;
        try
        {
            var oldWebView = _webView;
            DetachNativeNetworkCapture();
            DetachWebShellEvents();
            _webEventsAttached = false;

            oldWebView.EnvironmentRequested -= OnWebViewEnvironmentRequested;
            WebViewHost.Children.Remove(oldWebView);

            _adapterReady = false;
            // 等待 WebView2 适配器销毁后再创建新环境，避免同进程双实例导致卡死/闪退。
            await Task.Delay(200);
            if (!IsLoaded)
                return;

            DisposeWebViewControl(oldWebView);

            _webView = CreateBrowserWebView();
            EnsureWebShellEvents();
            WebViewHost.Children.Add(_webView);
            _webView.ZIndex = 0;
            WebViewStatusText.ZIndex = 1;

            BeginNavigationPipeline();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BrowserTabView] RecreateWebViewForProxyChange: {ex}");
        }
        finally
        {
            _recreatingWebView = false;
            GlobalRecreateLock.Release();
        }
    }

    private static void DisposeWebViewControl(NativeWebView webView)
    {
        try
        {
            if (webView is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BrowserTabView] DisposeWebViewControl: {ex}");
        }
    }

    private void EnsureWebShellEvents()
    {
        if (_webEventsAttached || _webView is null)
            return;
        _webEventsAttached = true;
        _webView.AdapterCreated += OnWebViewAdapterCreated;
        _webView.AdapterDestroyed += OnWebViewAdapterDestroyed;
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;
        _webView.WebResourceRequested += OnWebResourceRequested;
    }

    private void DetachWebShellEvents()
    {
        if (!_webEventsAttached || _webView is null)
            return;
        _webView.AdapterCreated -= OnWebViewAdapterCreated;
        _webView.AdapterDestroyed -= OnWebViewAdapterDestroyed;
        _webView.NavigationStarted -= OnNavigationStarted;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.NewWindowRequested -= OnNewWindowRequested;
        _webView.WebResourceRequested -= OnWebResourceRequested;
        _webEventsAttached = false;
    }

    private void OnTabVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTabViewModel.Address))
            PushAddressToBar();

        if (e.PropertyName == nameof(BrowserTabViewModel.ZoomFactor))
        {
            ApplyZoom();
            if (_tabVm != null)
                SyncZoomComboFromViewModel(_tabVm);
        }
    }

    private void OnNavigationRequested(object? sender, EventArgs e)
    {
        NavigateFromViewModel();
    }

    public void NavigateFromViewModel() => TryNavigateToViewModel(force: true);

    private void TryNavigateToViewModel(bool force)
    {
        if (_webView is null)
        {
            ScheduleWebViewCreation();
            return;
        }

        if (!_adapterReady)
        {
            ScheduleNavigateAfterAdapter();
            return;
        }

        var target = ResolveTabViewModel()?.GetNavigateUri()
                     ?? new Uri("about:blank");

        if (!force
            && _webView.Source is { } current
            && UriEquals(current, target))
        {
            HideWebViewStatus();
            return;
        }

        try
        {
            Console.WriteLine($"[BrowserTabView] Navigate -> {target}");
            if (IsBlankUri(target))
                HideWebViewStatus();
            else
                SetWebViewStatus($"正在打开 {target.Host}...");
            _webView.Navigate(target);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BrowserTabView] Navigate failed: {ex}");
            SetWebViewStatus("页面加载失败，正在重试...");
            ScheduleNavigateAfterAdapter();
        }
    }

    private void ScheduleNavigateAfterAdapter()
    {
        StartupPerformance.RunAfterDelay(
            () => Dispatcher.UIThread.Post(() => TryNavigateToViewModel(force: false), DispatcherPriority.Loaded),
            delayMs: 300);
        StartupPerformance.RunAfterDelay(
            () => Dispatcher.UIThread.Post(() => TryNavigateToViewModel(force: false), DispatcherPriority.Loaded),
            delayMs: 1200);
    }

    private void SetWebViewStatus(string message)
    {
        WebViewStatusText.Text = message;
        WebViewStatusText.IsVisible = true;
    }

    private void HideWebViewStatus()
    {
        WebViewStatusText.IsVisible = false;
    }

    private static bool UriEquals(Uri a, Uri b) =>
        string.Equals(a.AbsoluteUri.TrimEnd('/'), b.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _disposed = false;
        AttachTabViewModelIfNeeded();
        if (IsEffectivelyVisible && StartupPerformance.IsLayoutReady)
            ScheduleWebViewCreation();
    }

    public void ReleaseTabResources() => DisposeBrowserResources();

    public void DisposeBrowserResources()
    {
        if (_disposed)
            return;
        _disposed = true;
        _adapterReady = false;

        StopWaitingForHostLayout();
        UnsubscribeProxyChanged();

        DetachWebShellEvents();
        DetachNativeNetworkCapture();

        if (_webView is not null)
        {
            _webView.EnvironmentRequested -= OnWebViewEnvironmentRequested;
            try { WebViewHost.Children.Remove(_webView); } catch { }
            DisposeWebViewControl(_webView);
            _webView = null;
        }

        _webViewCreateScheduled = false;

        if (_tabVm is not null)
        {
            _tabVm.NavigationRequested -= OnNavigationRequested;
            _tabVm.PropertyChanged -= OnTabVmPropertyChanged;
            _tabVm = null;
        }
    }

    private void BeginNavigationPipeline()
    {
        Dispatcher.UIThread.Post(TryBeginNavigation, DispatcherPriority.Loaded);
        ScheduleNavigateAfterAdapter();
        ScheduleAdapterProbe();
    }

    private void TryBeginNavigation()
    {
        if (_disposed || _webView is null)
            return;

        if (_webView.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle { CoreWebView2: not 0 })
        {
            SetWebViewStatus("正在初始化 WebView...");
            return;
        }

        Console.WriteLine("[BrowserTabView] WebView2 adapter ready");
        TryAttachNativeNetworkCapture();
        TryNavigateToViewModel(force: false);
    }

    private void ScheduleAdapterProbe()
    {
        foreach (var delayMs in new[] { 150, 400, 800, 1500, 3000, 6000 })
        {
            StartupPerformance.RunAfterDelay(
                () => Dispatcher.UIThread.Post(TryBeginNavigation, DispatcherPriority.Loaded),
                delayMs);
        }
    }

    private void OnWebViewAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        Console.WriteLine("[BrowserTabView] AdapterCreated");
        _adapterReady = true;
        WebView2CreationCoordinator.ReleaseInit();
        TryApplyBrowserWebViewAppearance();
        RefreshWebViewLayout();
        Dispatcher.UIThread.Post(TryBeginNavigation, DispatcherPriority.Loaded);
    }

    private void OnWebViewAdapterDestroyed(object? sender, WebViewAdapterEventArgs e)
    {
        _adapterReady = false;
        DetachNativeNetworkCapture();
        WebView2CreationCoordinator.ReleaseInit();
        // 切换 Tab 时 OverlayPanel 会销毁适配器，勿在此处拆除 NativeWebView；AdapterCreated 后会重新导航。
    }

    private void TryApplyBrowserWebViewAppearance()
    {
        if (_webView?.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle { CoreWebView2: not 0 } handle)
            return;

        BrowserWebViewAppearance.ApplyFixedLight(handle.CoreWebView2, handle.CoreWebView2Controller);
    }

    private void TryAttachNativeNetworkCapture()
    {
        if (OpenSourceBrowserPolicy.ProxyOnlyTrafficCapture)
            return;

        if (_webView is null)
            return;

        if (_nativeNetworkAttached)
            return;

        if (_webView.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle
            || handle.CoreWebView2 == IntPtr.Zero)
        {
            ScheduleAttachRetry();
            return;
        }

        var tabVm = ResolveTabViewModel();
        if (tabVm is null)
            return;

        try
        {
            TryApplyBrowserWebViewAppearance();
            _webView2Wrapper = WebView2NativeWrapper.TryCreate(handle.CoreWebView2, tabVm);
            if (_webView2Wrapper is null)
                return;

            _webView2Wrapper.AttachEvents();
            _nativeNetworkAttached = true;
            RegisterCdpBridge(_webView2Wrapper);
            _attachRetryAttempts = 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BrowserTabView] WebView2NativeWrapper creation failed: {ex}");
            _webView2Wrapper = null;
            _nativeNetworkAttached = false;
        }
    }

    private void ScheduleAttachRetry()
    {
        if (_attachRetryAttempts >= 40 || _webView is null || _disposed)
            return;

        _attachRetryAttempts++;
        StartupPerformance.RunAfterDelay(
            () => Dispatcher.UIThread.Post(TryAttachNativeNetworkCapture, DispatcherPriority.Background),
            delayMs: 100);
    }

    private void DetachNativeNetworkCapture()
    {
        if (!_nativeNetworkAttached || _webView2Wrapper is null)
            return;

        UnregisterCdpBridge();

        _webView2Wrapper.Dispose();
        _webView2Wrapper = null;
        _nativeNetworkAttached = false;
    }

    private void OnNavigationStarted(object? sender, EventArgs e)
    {
        if (ResolveNavigationUri(sender, e) is not { } uri)
            return;

        if (ResolveTabViewModel() is not { } tabVm)
            return;

        tabVm.ClearFavicon();
        tabVm.SyncAddress(uri.ToString());
        PushAddressToBar();
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        var resolvedUrl = ResolveCompletedUri(sender)?.ToString() ?? _webView?.Source?.ToString() ?? "null";
        Console.WriteLine($"[BrowserTabView] NavigationCompleted success={e.IsSuccess} url={resolvedUrl}");

        if (!_nativeNetworkAttached)
            TryAttachNativeNetworkCapture();

        if (e.IsSuccess)
            HideWebViewStatus();
        else if (IsBlankUriString(resolvedUrl))
        {
            // 初始 about:blank 完成事件不算失败。
        }
        else
        {
            Console.Error.WriteLine($"[BrowserTabView] Navigation failed: {resolvedUrl}");
            SetWebViewStatus("无法打开页面，请检查网络或代理设置");
        }

        if (ResolveTabViewModel() is not { } tabVm)
            return;

        if (ResolveCompletedUri(sender) is not { } uri
            && !(e.IsSuccess
                 && Uri.TryCreate(resolvedUrl, UriKind.Absolute, out uri)
                 && !IsBlankUri(uri)))
        {
            return;
        }

        string? title = e.IsSuccess ? TryGetDocumentTitle(e) : null;
        if (string.IsNullOrWhiteSpace(title) && _webView2Wrapper is not null)
        {
            var dt = _webView2Wrapper.GetDocumentTitle()?.Trim();
            if (!string.IsNullOrWhiteSpace(dt))
                title = dt;
        }

        tabVm.ApplyNavigated(uri, title);
        PushAddressToBar();
        ApplyZoom();
        if (e.IsSuccess)
        {
            _webView2Wrapper?.RefreshFavicon();
            ScheduleFaviconRetries();
        }
    }

    private static Uri? ResolveNavigationUri(object? sender, EventArgs e)
    {
        if (TryGetEventUri(e) is { } eventUri && !IsBlankUri(eventUri))
            return eventUri;

        if (sender is NativeWebView wv && wv.Source is { } source && !IsBlankUri(source))
            return source;

        return null;
    }

    private Uri? ResolveCompletedUri(object? sender)
    {
        if (sender is NativeWebView wv && wv.Source is { } source && !IsBlankUri(source))
            return source;

        if (_webView2Wrapper?.GetSource() is { } coreSource
            && Uri.TryCreate(coreSource, UriKind.Absolute, out var uri)
            && !IsBlankUri(uri))
        {
            return uri;
        }

        return null;
    }

    private void ScheduleFaviconRetries()
    {
        foreach (var delayMs in new[] { 300, 900, 2000 })
        {
            StartupPerformance.RunAfterDelay(
                () => Dispatcher.UIThread.Post(() => _webView2Wrapper?.RefreshFavicon(), DispatcherPriority.Background),
                delayMs);
        }
    }

    private static Uri? TryGetEventUri(EventArgs e)
    {
        var type = e.GetType();
        foreach (var name in new[] { "Request", "Uri" })
        {
            var prop = type.GetProperty(name);
            if (prop?.GetValue(e) is Uri uri)
                return uri;
        }

        return null;
    }

    private static bool IsBlankUri(Uri uri) =>
        uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals("blank", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlankUriString(string url) =>
        url.Equals("about:blank", StringComparison.OrdinalIgnoreCase);

    private async void ApplyZoom()
    {
        if (_webView is null) return;
        if (ResolveTabViewModel() is not { } vm) return;
        if (vm.ZoomFactor == 1.0) return;
        try
        {
            await _webView.InvokeScript($"document.documentElement.style.zoom = '{vm.ZoomFactor}'");
        }
        catch { }
    }

    private void RefreshWebViewLayout()
    {
        WebViewHost.InvalidateMeasure();
        WebViewHost.InvalidateArrange();
        _webView?.InvalidateMeasure();
        _webView?.InvalidateArrange();
    }

    private static string? TryGetDocumentTitle(WebViewNavigationCompletedEventArgs e)
    {
        var prop = e.GetType().GetProperty("Title");
        return prop?.GetValue(e) as string;
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        var uri = e.Request;
        if (sender is NativeWebView wv && uri is not null && !uri.IsAbsoluteUri && wv.Source is { } baseUri)
            uri = new Uri(baseUri, uri);

        if (uri is null || !uri.IsAbsoluteUri)
            return;

        e.Handled = true;

        if (ResolveTabViewModel() is { } tabVm)
            tabVm.OpenInNewTab(uri);
    }

    private void OnWebResourceRequested(object? sender, WebResourceRequestedEventArgs e)
    {
        if (OpenSourceBrowserPolicy.ProxyOnlyTrafficCapture)
            return;

        if (_nativeNetworkAttached)
            return;

        if (ResolveTabViewModel() is not { } tabVm)
            return;

        var request = e.Request;
        if (request?.Uri is null)
            return;

        var capture = TrafficCaptureRecord.FromBrowser(
            Guid.NewGuid().ToString("N"),
            DateTime.Now.ToString("HH:mm:ss.fff"),
            string.IsNullOrWhiteSpace(tabVm.Title) ? "浏览页" : tabVm.Title,
            tabVm.TabId,
            tabVm.PageSessionId,
            string.IsNullOrWhiteSpace(tabVm.TopLevelUrl) ? tabVm.Address : tabVm.TopLevelUrl,
            request.Method?.Method ?? "UNKNOWN",
            request.Uri.ToString(),
            0,
            null,
            TrafficHttpHeaders.FromWireText(request.Headers?.ToString() ?? string.Empty),
            TrafficHttpHeaders.FromWireText("N/A(仅 Windows WebView2 可抓完整响应)"),
            string.Empty,
            null,
            (TrafficResourceContext)(int)WebTrafficResourceContext.Unknown);

        tabVm.SubmitCapture(capture);
    }

    private void RegisterCdpBridge(WebView2NativeWrapper wrapper)
    {
        var vm = ResolveTabViewModel();
        var tabId = string.IsNullOrEmpty(vm?.TabId) ? "browser" : vm.TabId;
        var bridge = vm?.CdpBridge ?? CdpBridge.Instance;
        bridge.Register(tabId, wrapper);
        bridge.SetActiveTab(tabId);
        vm?.TabManager?.OnTabRegistered(tabId, vm);
    }

    private void UnregisterCdpBridge()
    {
        var vm = ResolveTabViewModel();
        var tabId = string.IsNullOrEmpty(vm?.TabId) ? "browser" : vm.TabId;
        var bridge = vm?.CdpBridge ?? CdpBridge.Instance;
        bridge.Unregister(tabId);
        vm?.TabManager?.OnTabUnregistered(tabId);
    }

    private void AddressBar_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ResolveTabViewModel() is not { } tabVm)
            return;

        tabVm.Address = AddressBar.Text?.Trim() ?? string.Empty;
        tabVm.GoCommand.Execute(null);
        e.Handled = true;
    }

    private void ZoomCombo_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ZoomCombo.SelectedItem is not string selected || ResolveTabViewModel() is not { } tabVm)
            return;
        var percentStr = selected.TrimEnd('%');
        if (int.TryParse(percentStr, out var percent))
            tabVm.SetZoomCommand.Execute(percent);
    }

    private void DevToolsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        // wrapper 尚未创建（WebView 未初始化完成）时静默忽略
        _webView2Wrapper?.OpenDevToolsWindow();
    }

    private void SyncZoomComboFromViewModel(BrowserTabViewModel tabVm)
    {
        if (ZoomCombo.ItemsSource == null)
            ZoomCombo.ItemsSource = BrowserTabViewModel.ZoomLevelDisplay;

        if (ZoomCombo.DataContext is not BrowserTabViewModel)
            ZoomCombo.DataContext = tabVm;

        var percent = (int)(tabVm.ZoomFactor * 100);
        var display = $"{percent}%";
        if (BrowserTabViewModel.ZoomLevelDisplay.Contains(display))
            ZoomCombo.SelectedItem = display;
    }
}
