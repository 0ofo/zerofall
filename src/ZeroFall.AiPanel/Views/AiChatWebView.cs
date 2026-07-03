using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZeroFall.Base.AiTools;
using ZeroFall.Base.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZeroFall.AiPanel.Models;
using ZeroFall.AiPanel.Services;
using ZeroFall.AiPanel.ViewModels;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Views;

public sealed class AiChatWebViewStatusEventArgs(string message, bool isReady) : EventArgs
{
    public string Message { get; } = message;
    public bool IsReady { get; } = isReady;
}

public sealed class AiChatWebView : UserControl
{
    private readonly Panel _host;
    private readonly Dictionary<ChatMessage, MessageRenderState> _messageStates = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<JsonObject> _dispatchQueue = new();
    private readonly object _dispatchGate = new();
    private int _dispatchWorkerRunningInt;
    private readonly HashSet<INotifyCollectionChanged> _followingSubscriptions = [];
    private AiPanelViewModel? _vm;
    private NativeWebView? _webView;
    private bool _isReady;
    private bool _htmlLoaded;
    private bool _webViewCreateScheduled;
    private bool _webViewCreating;
    private EventHandler? _hostLayoutHandler;
    private int _htmlLoadAttempts;
    private int _readyProbeAttempts;
    private string? _lastSurfaceSyncFingerprint;
    private bool _syncAllMessagesScheduled;
    private bool _syncAllForcePending;
    private bool _sessionSwitchSyncPending;
    private bool _suppressNextSurfaceRemoveSync;
    private bool _surfaceSyncInProgress;
    private int _dispatchGeneration;
    private bool _syncAllRetryPosted;
    private bool _deferredFullSyncScheduled;
    private bool _deferredFullSyncPending;

    private const string ReadyProbeScript =
        "(function(){try{return !!(window.aiChatReady&&window.zerofallChat&&document.querySelector('.chat-root'));}catch(e){return false;}})()";
    private const int MaxInlineScriptJsonChars = 384 * 1024;
    private const int MaxInlineTailMarkdownChars = 48 * 1024;
    private const int InitialHydrateTailCount = 32;
    private const int PendingMarkdownTailCount = 32;
    private const int SyncAllRetryDelayMs = 150;
    private long _lastWebDispatchTicks;

    public event EventHandler<AiChatWebViewStatusEventArgs>? StatusChanged;

    public AiChatWebView()
    {
        _host = new Panel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Content = _host;
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        if (Application.Current is not null)
            Application.Current.ActualThemeVariantChanged += OnThemeChanged;
        ReportStatus("正在加载 AI 聊天...");
    }

    public void AttachWebViewWhenReady()
    {
        _webViewCreateScheduled = false;
        ScheduleWebViewCreation();
    }

    /// <summary>历史恢复或 WebView 晚于 Messages 就绪时，优先从会话镜像恢复。</summary>
    public void RequestFullSync()
    {
        _lastSurfaceSyncFingerprint = null;
        if (_vm is null)
            return;

        SubscribeVisibleMessages();
        if (ShouldDeferFullSurfaceSync())
        {
            _deferredFullSyncPending = true;
            ScheduleDeferredFullSync();
            return;
        }

        ScheduleSyncAllMessages(force: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ReleaseResources();
    }

    public void ReleaseResources()
    {
        Loaded -= OnLoaded;
        DetachViewModel();
        StopWaitingForHostLayout();
        if (Application.Current is not null)
            Application.Current.ActualThemeVariantChanged -= OnThemeChanged;
        DisposeWebView();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is null)
            AttachViewModel(DataContext as AiPanelViewModel);

        if (IsVisible)
            ScheduleWebViewCreation();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty)
            return;

        if (change.NewValue is true)
        {
            if (_webView is not null && !_htmlLoaded)
                RecreateWebView();
            else if (_webView is null)
                ScheduleWebViewCreation();

            RequestSurfaceResyncIfHistoryPresent();
        }
    }

    private void RequestSurfaceResyncIfHistoryPresent()
    {
        if (_vm is null || GetVisibleMessages().Count == 0)
            return;

        _sessionSwitchSyncPending = true;
        _deferredFullSyncPending = true;
        _lastSurfaceSyncFingerprint = null;

        if (_webView is not null && _isReady && !ShouldDeferFullSurfaceSync())
            RunPendingFullSync();
        else
            ScheduleDeferredFullSync();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (IsVisible && StartupPerformance.IsLayoutReady)
            ScheduleWebViewCreation();
    }

    private void ScheduleWebViewCreation()
    {
        if (_webView is not null || _webViewCreateScheduled)
            return;

        if (!StartupPerformance.IsLayoutReady)
            return;

        _webViewCreateScheduled = true;
        TryCreateWebViewWhenHostReady();
        if (_webView is null)
            StartupPerformance.RunOnUiIdle(TryCreateWebViewWhenHostReady);
    }

    private bool IsHostReady()
    {
        if (!IsLoaded || !IsVisible)
            return false;
        if (TopLevel.GetTopLevel(this) is null)
            return false;

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = _host.Bounds;
        return bounds.Width > 8 && bounds.Height > 8;
    }

    private void TryCreateWebViewWhenHostReady()
    {
        if (_webView is not null || _webViewCreating)
            return;

        if (!IsHostReady())
        {
            if (_hostLayoutHandler is null)
            {
                _hostLayoutHandler = OnHostLayoutUpdated;
                _host.LayoutUpdated += _hostLayoutHandler;
                LayoutUpdated += _hostLayoutHandler;
            }

            _webViewCreateScheduled = false;
            return;
        }

        StopWaitingForHostLayout();
        EnsureWebViewCreated();
    }

    private void OnHostLayoutUpdated(object? sender, EventArgs e) => TryCreateWebViewWhenHostReady();

    private void StopWaitingForHostLayout()
    {
        if (_hostLayoutHandler is null)
            return;

        _host.LayoutUpdated -= _hostLayoutHandler;
        LayoutUpdated -= _hostLayoutHandler;
        _hostLayoutHandler = null;
    }

    private void EnsureWebViewCreated()
    {
        if (_webView is not null || _webViewCreating)
            return;

        if (!IsHostReady())
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
            await WebView2CreationCoordinator.WaitForInitAsync().ConfigureAwait(false);

            if (_webView is not null || !IsHostReady())
            {
                WebView2CreationCoordinator.ReleaseInit();
                return;
            }

            WebView2CreationCoordinator.ArmInitRelease(20_000, () =>
                System.Diagnostics.Debug.WriteLine("[AiChatWebView] init gate timeout"));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_webView is not null || !IsHostReady())
                    return;

                _webView = new NativeWebView
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                _webView.AdapterCreated += OnAdapterCreated;
                _webView.NavigationStarted += OnNavigationStarted;
                _webView.NavigationCompleted += OnNavigationCompleted;
                _webView.NewWindowRequested += OnNewWindowRequested;
                _webView.WebMessageReceived += OnWebMessageReceived;
                _host.Children.Add(_webView);
                ReportStatus("正在加载聊天界面...");
                ScheduleLoadHtmlAttempts();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiChatWebView] WebView create failed: {ex}");
            WebView2CreationCoordinator.ReleaseInit();
            ReportStatus("WebView 初始化失败");
            DisposeWebView();
            _webViewCreateScheduled = false;
            StartupPerformance.RunAfterDelay(TryCreateWebViewWhenHostReady, delayMs: 1500);
        }
        finally
        {
            _webViewCreating = false;
        }
    }

    private void RecreateWebView()
    {
        DisposeWebView();
        _webViewCreateScheduled = false;
        ScheduleWebViewCreation();
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        WebView2CreationCoordinator.MarkAiAdapterReady();
        WebView2CreationCoordinator.ReleaseInit();
        Dispatcher.UIThread.Post(LoadHtmlOnce, DispatcherPriority.Loaded);
    }

    private void OnNavigationStarted(object? sender, EventArgs e)
    {
        // 初始 NavigateToString 期间勿误判外链并 Reload，否则桥接未就绪、历史无法注入。
        if (!_isReady)
            return;

        if (TryGetEventUri(e) is not { } uri || IsAllowedChatUri(uri))
            return;

        if (TryGetIsUserInitiated(e) == false)
            return;

        _vm?.OpenMarkdownLink(uri.ToString());
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_webView is null)
            return;

        if (e.IsSuccess && _webView.Source is { } source && !IsAllowedChatUri(source))
        {
            RestoreChatPageAfterStrayNavigation();
            return;
        }

        if (!e.IsSuccess || !_htmlLoaded || _isReady)
            return;

        ScheduleReadyProbe();
    }

    private static bool? TryGetIsUserInitiated(EventArgs e)
    {
        var prop = e.GetType().GetProperty("IsUserInitiated");
        if (prop?.PropertyType == typeof(bool))
            return (bool)prop.GetValue(e)!;

        return null;
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        var uri = e.Request;
        if (uri is null)
            return;

        if (sender is NativeWebView wv && !uri.IsAbsoluteUri && wv.Source is { } baseUri)
            uri = new Uri(baseUri, uri);

        if (uri.IsAbsoluteUri)
            _vm?.OpenMarkdownLink(uri.ToString());
    }

    private bool _restoringChatPage;

    private void RestoreChatPageAfterStrayNavigation()
    {
        if (_restoringChatPage || _webView is null)
            return;

        _restoringChatPage = true;
        _isReady = false;
        _htmlLoaded = false;
        _lastSurfaceSyncFingerprint = null;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                LoadHtmlOnce();
            }
            finally
            {
                _restoringChatPage = false;
            }
        }, DispatcherPriority.Loaded);
    }

    private static bool IsAllowedChatUri(Uri uri)
    {
        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("blank", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = uri.LocalPath;
        return path.Contains("ZeroFall", StringComparison.OrdinalIgnoreCase)
               && path.Contains("ai-chat", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsProbeReady(string? result) =>
        !string.IsNullOrWhiteSpace(result)
        && result.Contains("true", StringComparison.OrdinalIgnoreCase);

    private void ScheduleLoadHtmlAttempts()
    {
        Dispatcher.UIThread.Post(LoadHtmlOnce, DispatcherPriority.Loaded);
        StartupPerformance.RunAfterDelay(
            () => Dispatcher.UIThread.Post(LoadHtmlOnce, DispatcherPriority.Loaded),
            delayMs: 300);
        StartupPerformance.RunAfterDelay(
            () => Dispatcher.UIThread.Post(LoadHtmlOnce, DispatcherPriority.Loaded),
            delayMs: 1200);
    }

    private void LoadHtmlOnce()
    {
        if (_webView is null || _htmlLoaded)
            return;

        _htmlLoadAttempts++;
        try
        {
            _webView.NavigateToString(InjectBootThemeHtml(AiChatHtmlTemplate.Value));
            _htmlLoaded = true;
            ScheduleReadyProbe();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiChatWebView] NavigateToString failed: {ex}");
            TryNavigateFromFileFallback();
        }
    }

    private void TryNavigateFromFileFallback()
    {
        if (_webView is null || _htmlLoaded)
            return;

        try
        {
            var fileUri = EnsureChatHtmlFileUri();
            _webView.Navigate(fileUri);
            _htmlLoaded = true;
            ScheduleReadyProbe();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiChatWebView] file Navigate failed: {ex}");
            if (_htmlLoadAttempts >= 15)
                ReportStatus("聊天页面加载失败，请切换 Tab 后重试");
            else
                ReportStatus("聊天页面加载失败，正在重试...");
        }
    }

    private static Uri EnsureChatHtmlFileUri()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ZeroFall", "ai-chat");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "index.html");
        File.WriteAllText(path, InjectBootThemeHtml(AiChatHtmlTemplate.Value), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new Uri(path);
    }

    private void ScheduleReadyProbe()
    {
        _readyProbeAttempts = 0;
        _ = ProbeReadyAsync();
    }

    private async Task ProbeReadyAsync()
    {
        while (_webView is not null && _htmlLoaded && !_isReady && _readyProbeAttempts < 60)
        {
            _readyProbeAttempts++;
            var ready = await EvaluateScriptResultAsync(ReadyProbeScript);
            if (IsProbeReady(ready))
            {
                MarkReadyAndSync();
                return;
            }

            await Task.Delay(100);
        }

        if (_webView is not null && _htmlLoaded && !_isReady)
        {
            System.Diagnostics.Debug.WriteLine("[AiChatWebView] Ready probe timed out, forcing sync");
            MarkReadyAndSync();
        }
    }

    private void MarkReadyAndSync()
    {
        if (_isReady)
            return;

        _isReady = true;
        ReportStatus(string.Empty, ready: true);
        _sessionSwitchSyncPending = true;
        _deferredFullSyncPending = true;
        lock (_dispatchGate)
            _dispatchQueue.Clear();

        _dispatchGeneration++;

        SubscribeVisibleMessages();
        if (_vm is { Messages.Count: > 0 } && !ShouldDeferFullSurfaceSync())
            SyncAllMessages(force: true);
        else
            RequestFullSync();

        SendWaitingState();
        SendReadOnlyState();
        SendTheme();
    }

    private void ReportStatus(string message, bool ready = false)
    {
        StatusChanged?.Invoke(this, new AiChatWebViewStatusEventArgs(message, ready));
    }

    private void OnThemeChanged(object? sender, EventArgs e) => SendTheme();

    private void SendTheme()
    {
        SendCommand(new JsonObject
        {
            ["type"] = "setTheme",
            ["theme"] = ResolveChatTheme()
        }, bypassDefer: true);
    }

    private static string ResolveChatTheme() =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? "dark" : "light";

    private static string InjectBootThemeHtml(string html)
    {
        if (Application.Current?.ActualThemeVariant != ThemeVariant.Dark)
            return html;

        return html.Replace(
            "<html lang=\"zh-CN\">",
            "<html lang=\"zh-CN\" class=\"dark\">",
            StringComparison.Ordinal);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachViewModel();
        AttachViewModel(DataContext as AiPanelViewModel);
    }

    private void AttachViewModel(AiPanelViewModel? vm)
    {
        _vm = vm;
        if (_vm is null)
            return;

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.SurfaceRounds.CollectionChanged += OnSurfaceRoundsChanged;
        _vm.ChatSurfaceRestored += OnChatSurfaceRestored;
        _vm.ChatMessagesTruncated += OnChatMessagesTruncated;
        _vm.ChatMessageUiIdRemapped += OnChatMessageUiIdRemapped;
        SubscribeVisibleMessages();
        RequestFullSync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiPanelViewModel.IsWaitingForReply))
            SendWaitingState();
        if (e.PropertyName == nameof(AiPanelViewModel.IsReadOnlySession))
            SendReadOnlyState();
        if (e.PropertyName is nameof(AiPanelViewModel.IsSending) or nameof(AiPanelViewModel.IsCompressingContext))
        {
            TryRunDeferredFullSync();
            if (e.PropertyName == nameof(AiPanelViewModel.IsSending) && (_vm?.IsSending ?? false))
                Dispatcher.UIThread.Post(FlushLiveRoundSurfaceToWeb, DispatcherPriority.Loaded);
        }
    }

    private void SendReadOnlyState()
    {
        if (_vm is null)
            return;

        SendCommand(new JsonObject
        {
            ["type"] = "setReadOnly",
            ["readOnly"] = _vm.IsReadOnlySession
        });
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (_vm is null || string.IsNullOrWhiteSpace(e.Body))
            return;

        try
        {
            using var doc = JsonDocument.Parse(e.Body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
                return;

            var type = typeEl.GetString();
            if (string.Equals(type, "revertMessage", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("id", out var idEl))
                    return;

                var id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id))
                    return;

                _ = _vm.TryRevertMessagesFromUserAsync(id);
                return;
            }

            if (string.Equals(type, "requestToolPayload", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("id", out var idEl))
                    return;

                var id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id))
                    return;

                SendToolPayloadToWeb(id);
                return;
            }

            if (string.Equals(type, "requestReasoningPayload", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("id", out var idEl))
                    return;

                var id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id))
                    return;

                SendReasoningPayloadToWeb(id);
                return;
            }

            if (string.Equals(type, "requestMessageWindow", StringComparison.Ordinal))
            {
                var from = root.TryGetProperty("from", out var fromEl) && fromEl.TryGetInt32(out var fromValue)
                    ? fromValue
                    : 0;
                var to = root.TryGetProperty("to", out var toEl) && toEl.TryGetInt32(out var toValue)
                    ? toValue
                    : -1;
                _ = HydrateMessageWindowAsync(from, to);
                return;
            }

            if (string.Equals(type, "requestToolExpanded", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("id", out var idEl))
                    return;

                var id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id))
                    return;

                SetToolExpandedFromWeb(id, true);
                return;
            }

            if (string.Equals(type, "setToolExpanded", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("id", out var idEl))
                    return;

                var id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id))
                    return;

                var expanded = root.TryGetProperty("expanded", out var expandedEl) && expandedEl.ValueKind == JsonValueKind.True;
                SetToolExpandedFromWeb(id, expanded);
                return;
            }

            if (string.Equals(type, "setReasoningExpanded", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("id", out var idEl))
                    return;

                var id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id))
                    return;

                var expanded = root.TryGetProperty("expanded", out var expandedEl) && expandedEl.ValueKind == JsonValueKind.True;
                SetReasoningExpandedFromWeb(id, expanded);
                return;
            }

            if (string.Equals(type, "openLink", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("url", out var urlEl))
                    return;

                var url = urlEl.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                    _vm.OpenMarkdownLink(url);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiChatWebView] WebMessage parse failed: {ex.Message}");
        }
    }

    private void SendWaitingState()
    {
        if (_vm is null)
            return;

        SendCommand(new JsonObject
        {
            ["type"] = "setWaiting",
            ["waiting"] = _vm.IsWaitingForReply
        }, bypassDefer: HasLiveRoundInProgress());
    }

    /// <summary>发送/压缩/流式进行中：须持续向 WebView 推送增量，不受会话全量恢复标志影响。</summary>
    private bool HasLiveRoundInProgress() =>
        (_vm?.IsSending ?? false)
        || (_vm?.IsCompressingContext ?? false)
        || HasActiveStreaming();

    private void DetachViewModel()
    {
        if (_vm is not null)
        {
            _vm.ChatSurfaceRestored -= OnChatSurfaceRestored;
            _vm.ChatMessagesTruncated -= OnChatMessagesTruncated;
            _vm.ChatMessageUiIdRemapped -= OnChatMessageUiIdRemapped;
            _vm.SurfaceRounds.CollectionChanged -= OnSurfaceRoundsChanged;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        foreach (var collection in _followingSubscriptions)
            collection.CollectionChanged -= OnFollowingChanged;
        _followingSubscriptions.Clear();

        foreach (var state in _messageStates.Values.ToList())
            state.Message.PropertyChanged -= OnMessagePropertyChanged;
        _messageStates.Clear();

        _lastSurfaceSyncFingerprint = null;
        _syncAllMessagesScheduled = false;
        _syncAllForcePending = false;
        _sessionSwitchSyncPending = false;
        _suppressNextSurfaceRemoveSync = false;
        _deferredFullSyncScheduled = false;
        _deferredFullSyncPending = false;
        _vm = null;
    }

    private void PruneMessageStatesNotOnSurface()
    {
        if (_vm is null)
            return;

        var live = new HashSet<ChatMessage>(GetVisibleMessages(), ReferenceEqualityComparer.Instance);

        var stale = _messageStates.Keys.Where(k => !live.Contains(k)).ToList();
        foreach (var message in stale)
        {
            if (!_messageStates.Remove(message, out var state))
                continue;

            message.PropertyChanged -= OnMessagePropertyChanged;
        }
    }

    private void OnChatSurfaceRestored(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            var fingerprint = ChatMessageRenderFingerprint.ForMessages(GetVisibleMessages());
            if (_isReady
                && !_sessionSwitchSyncPending
                && string.Equals(fingerprint, _lastSurfaceSyncFingerprint, StringComparison.Ordinal))
                return;
        }

        _lastSurfaceSyncFingerprint = null;
        _deferredFullSyncPending = true;

        if (HasLiveRoundInProgress())
        {
            ScheduleDeferredFullSync();
            return;
        }

        _sessionSwitchSyncPending = true;
        if (_webView is null || !_isReady)
        {
            ScheduleDeferredFullSync();
            return;
        }

        if (ShouldDeferFullSurfaceSync())
        {
            ScheduleDeferredFullSync();
            return;
        }

        RunPendingFullSync();
    }

    private void OnChatMessagesTruncated(object? sender, ChatMessagesTruncatedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.FromMessageUiId))
            return;

        _suppressNextSurfaceRemoveSync = true;
        Dispatcher.UIThread.Post(() => _suppressNextSurfaceRemoveSync = false, DispatcherPriority.Background);
        PruneMessageStatesNotOnSurface();
        _lastSurfaceSyncFingerprint = null;
        SendCommand(new JsonObject
        {
            ["type"] = "truncateMessagesFrom",
            ["id"] = e.FromMessageUiId,
            ["scrollToEnd"] = true
        }, bypassDefer: true);
    }

    private void OnChatMessageUiIdRemapped(object? sender, ChatMessageUiIdRemappedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.FromMessageUiId) || string.IsNullOrWhiteSpace(e.ToMessageUiId))
            return;

        SendCommand(new JsonObject
        {
            ["type"] = "remapMessageId",
            ["fromId"] = e.FromMessageUiId,
            ["toId"] = e.ToMessageUiId
        }, bypassDefer: true);
    }

    private bool ShouldDeferFullSurfaceSync() =>
        !_sessionSwitchSyncPending
        && ((_vm?.IsSending ?? false)
            || (_vm?.IsCompressingContext ?? false)
            || HasActiveStreaming());

    private void ScheduleDeferredFullSync()
    {
        if (_deferredFullSyncScheduled)
            return;

        _deferredFullSyncScheduled = true;
        Dispatcher.UIThread.Post(TryRunDeferredFullSync, DispatcherPriority.Background);
    }

    private void TryRunDeferredFullSync()
    {
        _deferredFullSyncScheduled = false;
        if (!_deferredFullSyncPending)
            return;

        if (ShouldDeferFullSurfaceSync())
        {
            ScheduleDeferredFullSync();
            return;
        }

        RunPendingFullSync();
    }

    private void RunPendingFullSync()
    {
        _deferredFullSyncPending = false;
        RequestFullSync();
    }

    private List<ChatMessage> GetVisibleMessages() =>
        _vm?.GetVisibleMessagesForWeb().ToList() ?? [];

    private void OnSurfaceRoundsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SubscribeVisibleMessages();

        if (ShouldDeferIncrementalSync())
            return;

        if (e.NewItems is not null)
        {
            foreach (ChatRoundBlock round in e.NewItems)
            {
                if (round.UserMessage is { } user)
                    UpsertSingleMessage(user);
            }

            // 增量 Add 已由 upsertMessage 同步；全量 initSessionWindow 仅在会话切换/撤销等显式 NotifyChatSurfaceRestored 时触发。
            if (e.Action == NotifyCollectionChangedAction.Add)
                return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
            return;

        if (e.Action == NotifyCollectionChangedAction.Remove && _suppressNextSurfaceRemoveSync)
            return;

        if (HasActiveStreaming())
            return;

        if (HasLiveRoundInProgress())
            return;

        ScheduleSyncAllMessages();
    }

    private void OnFollowingChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SubscribeVisibleMessages();

        if (ShouldDeferIncrementalSync())
            return;

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (ChatMessage msg in e.NewItems)
            {
                if (msg.IsUser)
                {
                    UpsertSingleMessage(msg);
                    continue;
                }

                if (HasActiveStreaming())
                    EnsureMessageShellInWeb(msg);
                else
                    UpsertSingleMessage(msg);
            }

            return;
        }

        if (HasActiveStreaming())
            return;

        if (HasLiveRoundInProgress())
            return;

        ScheduleSyncAllMessages();
    }

    private bool HasActiveStreaming() =>
        _vm is not null && GetVisibleMessages().Any(m => m.IsStreaming || m.IsToolRunning);

    private static bool IsLiveSurfaceMessage(ChatMessage message) =>
        message.IsStreaming || message.IsThinking || message.IsToolRunning;

    /// <summary>其它消息在流式/工具执行中时，历史消息不应再收增量 patch；工具消息始终同步 call/result。</summary>
    private bool ShouldDeferHistoricalIncrementalSync(ChatMessage message) =>
        HasActiveStreaming() && !IsLiveSurfaceMessage(message) && !message.HasToolCall;

    /// <summary>用户已点发送或助手/工具仍在输出；此期间禁止全量 initSessionWindow 与大 payload。</summary>
    private bool IsLiveAiRound() => ShouldDeferFullSurfaceSync();

    private bool HasBrowserToolInFlight() =>
        _vm is not null && _vm.Messages.Any(m => m.IsToolRunning);

    private void UpsertSingleMessage(ChatMessage message)
    {
        if (!message.Visual.IsVisibleInUi())
            return;

        if (ShouldDeferIncrementalSync())
            return;

        if (message.IsStreaming || message.IsToolRunning)
        {
            EnsureMessageShellInWeb(message);
            return;
        }

        TryPushMessageShellToWeb(message, bypassDefer: HasLiveRoundInProgress());
    }

    /// <summary>压缩后会话恢复期间用户已点发送：补推当前轮 user/流式壳，避免 C# 在发送中而 WebView 空白。</summary>
    private void FlushLiveRoundSurfaceToWeb()
    {
        if (_vm is null)
            return;

        var pushed = false;
        if (_vm.SurfaceRounds.Count > 0)
        {
            var round = _vm.SurfaceRounds[^1];
            if (round.UserMessage is { } user)
            {
                PushLiveMessageToWeb(user);
                pushed = true;
            }

            foreach (var following in round.Following)
            {
                PushLiveMessageToWeb(following);
                pushed = true;
            }
        }

        if (!pushed)
        {
            for (var i = GetVisibleMessages().Count - 1; i >= 0; i--)
            {
                var message = GetVisibleMessages()[i];
                if (!message.IsUser)
                    continue;

                PushLiveMessageToWeb(message);
                break;
            }
        }

        SendWaitingState();
    }

    private void PushLiveMessageToWeb(ChatMessage message)
    {
        if (message.IsStreaming || message.IsToolRunning || message.IsThinking)
        {
            EnsureMessageShellInWeb(message);
            return;
        }

        TryPushMessageShellToWeb(message, bypassDefer: true);
    }

    /// <summary>流式阶段首次 upsert 完整消息 JSON（无 deferred / 懒加载）。</summary>
    private void EnsureMessageShellInWeb(ChatMessage message) =>
        TryPushMessageShellToWeb(message, bypassDefer: true, patchToolSummary: true);

    private void TryPushMessageShellToWeb(ChatMessage message, bool bypassDefer, bool patchToolSummary = false)
    {
        if (!message.Visual.IsVisibleInUi())
            return;

        var state = EnsureState(message);
        if (state.ShellPushedInWeb)
            return;

        state.ShellPushedInWeb = true;
        SendCommand(new JsonObject
        {
            ["type"] = "upsertMessage",
            ["message"] = (JsonObject)BuildListMessageJson(message).DeepClone()
        }, bypassDefer: bypassDefer);

        if (patchToolSummary && message.HasToolCall)
            PatchToolSummary(message);
    }

    /// <summary>单条 SSE/工具流结束：正文后台全量 Markdig 一次；思考仅保留 raw tail。</summary>
    private void FinalizeMessageInWeb(ChatMessage message)
    {
        if (!message.Visual.IsVisibleInUi())
            return;

        EnsureState(message);

        if (message.HasToolCall)
            PatchToolSummary(message);
        else
        {
            if (message.HasReasoning)
            {
                PatchReasoningMeta(message);
                if (!message.IsThinking)
                    SyncRawStreamTail(EnsureState(message), ResolveReasoningMarkdown(message), reasoning: true);
            }

            if (message.ShowAssistantMarkdown)
            {
                SyncAssistantMarkdown(message);
                ApplyFinalContentHtmlToWeb(message);
            }
        }

        PatchStreamingState(message);
    }

    /// <summary>
    /// 全量同步以 <see cref="AiPanelViewModel.Messages"/> 为准，帧末一次 initMessages 覆盖 Vue 状态。
    /// </summary>
    private void ScheduleSyncAllMessages(bool force = false)
    {
        if (_surfaceSyncInProgress)
            return;

        if (force)
            _syncAllForcePending = true;

        if (_syncAllMessagesScheduled)
            return;

        _syncAllMessagesScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _syncAllMessagesScheduled = false;
            var forceNow = _syncAllForcePending;
            _syncAllForcePending = false;
            if (!forceNow && ShouldDelayFullSurfaceSync())
            {
                ScheduleSyncAllMessagesRetry();
                return;
            }

            SyncAllMessages(forceNow);
        }, DispatcherPriority.Background);
    }

    private bool ShouldDelayFullSurfaceSync() =>
        ShouldDeferFullSurfaceSync()
        || HasPendingWebDispatch();

    private void ScheduleSyncAllMessagesRetry()
    {
        if (_syncAllRetryPosted)
            return;

        _syncAllRetryPosted = true;
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(SyncAllRetryDelayMs).ConfigureAwait(true);
            _syncAllRetryPosted = false;
            ScheduleSyncAllMessages();
        }, DispatcherPriority.Background);
    }

    private void SubscribeVisibleMessages()
    {
        if (_vm is null)
            return;

        foreach (var message in GetVisibleMessages())
            EnsureState(message);

        foreach (var round in _vm.SurfaceRounds)
        {
            if (_followingSubscriptions.Add(round.Following))
                round.Following.CollectionChanged += OnFollowingChanged;
        }
    }

    private MessageRenderState EnsureState(ChatMessage message)
    {
        if (_messageStates.TryGetValue(message, out var state))
            return state;

        state = new MessageRenderState(message);
        _messageStates[message] = state;
        message.PropertyChanged += OnMessagePropertyChanged;
        HydrateStateFromMessageDbCache(message, state);
        return state;
    }

    private void HydrateStateFromMessageDbCache(ChatMessage message, MessageRenderState state)
    {
        // ContentHtml/ReasoningHtml 按需反序列化，避免 EnsureState 为每条消息常驻一份 blocks。
    }

    private bool TryLoadCachedBlocks(
        ChatMessage message,
        MessageRenderState state,
        bool reasoning,
        out IReadOnlyList<RenderedMarkdownBlock> blocks)
    {
        var target = reasoning ? state.ReasoningBlocks : state.ContentBlocks;
        var isActive = reasoning ? message.IsThinking : message.IsStreaming;

        if (isActive)
        {
            blocks = target;
            return target.Count > 0;
        }

        var cachedHtml = reasoning ? message.ReasoningHtml : message.ContentHtml;
        if (!string.IsNullOrEmpty(cachedHtml))
        {
            blocks = ChatMessageRenderCache.Deserialize(cachedHtml);
            return blocks.Count > 0;
        }

        if (target.Count > 0)
        {
            blocks = target;
            return true;
        }

        blocks = target;
        return false;
    }

    private static void ReleaseInMemoryMarkdownBlocks(MessageRenderState state, bool reasoning)
    {
        if (reasoning)
        {
            state.ReasoningBlocks.Clear();
            state.SentReasoningBlockIds.Clear();
            return;
        }

        state.ContentBlocks.Clear();
        state.SentContentBlockIds.Clear();
        state.Streamer.Reset();
    }

    private static void ApplyBlocksToState(
        MessageRenderState state,
        bool reasoning,
        IReadOnlyList<RenderedMarkdownBlock> blocks)
    {
        if (reasoning)
        {
            ChatMessageRenderCache.CopyBlocks(blocks, state.ReasoningBlocks, state.SentReasoningBlockIds);
            state.ReasoningMarkdigFinalized = true;
            state.ReasoningRenderPending = false;
            return;
        }

        ChatMessageRenderCache.CopyBlocks(blocks, state.ContentBlocks, state.SentContentBlockIds);
        state.ContentMarkdigFinalized = true;
        state.ContentRenderPending = false;
    }

    private void RequestBackgroundMarkdownRender(ChatMessage message, MessageRenderState state, string markdown, bool reasoning)
    {
        if (_vm is null || reasoning)
            return;

        if (message.IsStreaming)
            return;

        if (TryLoadCachedBlocks(message, state, reasoning, out var cached))
        {
            SendReplaceBlocks(state.Id, reasoning, cached);
            return;
        }

        if (string.IsNullOrWhiteSpace(markdown))
        {
            SetMarkdigFinalized(state, reasoning, true);
            SendStreamTail(state, reasoning, string.Empty);
            return;
        }

        if (reasoning ? state.ReasoningRenderPending : state.ContentRenderPending)
            return;

        if (reasoning)
            state.ReasoningRenderPending = true;
        else
            state.ContentRenderPending = true;

        _vm.MarkdownRenderQueue.Enqueue(new ChatMarkdownRenderRequest
        {
            MessageUiId = state.Id,
            Reasoning = reasoning,
            Markdown = markdown,
            OnCompleted = result => Dispatcher.UIThread.Post(() => OnBackgroundMarkdownRenderCompleted(message, result))
        });
    }

    private void OnBackgroundMarkdownRenderCompleted(ChatMessage message, ChatMarkdownRenderResult result)
    {
        if (_vm is null || !message.Visual.IsVisibleInUi() || !_vm.Messages.Contains(message))
            return;

        if (!_messageStates.TryGetValue(message, out var state))
            return;

        if (!string.Equals(state.Id, result.MessageUiId, StringComparison.Ordinal))
            return;

        if (ShouldDeferHistoricalIncrementalSync(message))
        {
            if (result.Reasoning)
                state.ReasoningRenderPending = false;
            else
                state.ContentRenderPending = false;
            return;
        }

        if (result.Reasoning ? message.IsThinking : message.IsStreaming)
        {
            if (result.Reasoning)
                state.ReasoningRenderPending = false;
            else
                state.ContentRenderPending = false;
            return;
        }

        if (result.Reasoning)
            return;

        var currentMarkdown = ResolveContentMarkdown(message);
        ApplyBlocksToState(state, false, result.Blocks);
        SetFedLength(state, false, currentMarkdown.Length);
        state.Streamer.Reset();
        message.ApplyRenderedHtml(reasoning: false, result.Html);
        state.ContentRenderPending = false;
        if (result.Blocks.Count > 0)
        {
            SendReplaceBlocks(state.Id, reasoning: false, result.Blocks);
            SendStreamTail(state, reasoning: false, string.Empty);
        }
        else if (!string.IsNullOrEmpty(currentMarkdown))
            SendStreamTail(state, reasoning: false, currentMarkdown);

        ReleaseInMemoryMarkdownBlocks(state, reasoning: false);
        _vm.NotifyMarkdownRenderCompleted(message);
    }

    private void SchedulePendingMarkdownRenders(int from = 0, int? to = null)
    {
        var visibleMessages = GetVisibleMessages();
        if (_vm is null || visibleMessages.Count == 0 || HasActiveStreaming())
            return;

        from = Math.Clamp(from, 0, visibleMessages.Count - 1);
        var end = Math.Clamp(to ?? visibleMessages.Count - 1, from, visibleMessages.Count - 1);

        for (var i = from; i <= end; i++)
        {
            var message = visibleMessages[i];
            if (message.IsStreaming || message.IsToolRunning)
                continue;

            var state = EnsureState(message);
            if (!message.ShowAssistantMarkdown)
                continue;

            var content = ResolveContentMarkdown(message);
            if (!string.IsNullOrEmpty(content) && !TryLoadCachedBlocks(message, state, reasoning: false, out _))
                RequestBackgroundMarkdownRender(message, state, content, reasoning: false);
        }
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnMessagePropertyChanged(sender, e));
            return;
        }

        if (sender is not ChatMessage message)
            return;

        if (ShouldDeferIncrementalSync())
            return;

        switch (e.PropertyName)
        {
            case nameof(ChatMessage.Content):
            case nameof(ChatMessage.ReasoningContent):
                if (ShouldDeferHistoricalIncrementalSync(message))
                    return;
                SyncMessage(message);
                break;
            case nameof(ChatMessage.ToolOutput):
            case nameof(ChatMessage.ToolArgumentsJson):
                if (message.HasToolCall)
                {
                    if (message.IsToolRunning && e.PropertyName == nameof(ChatMessage.ToolArgumentsJson))
                        ScheduleStreamingToolPatch(message);
                    else
                        PatchToolSummary(message);
                    break;
                }

                if (ShouldDeferHistoricalIncrementalSync(message))
                    return;
                SyncMessage(message);
                break;
            case nameof(ChatMessage.ToolCommand):
                if (message.IsToolRunning)
                    ScheduleStreamingToolPatch(message);
                else if (message.HasToolCall)
                    PatchToolSummary(message);
                else if (!ShouldDeferHistoricalIncrementalSync(message))
                    SyncMessage(message);
                break;
            case nameof(ChatMessage.HasReasoning):
                if (ShouldDeferHistoricalIncrementalSync(message))
                    return;
                SyncMessage(message);
                break;
            case nameof(ChatMessage.IsReasoningExpanded):
                PatchReasoningExpandedState(message);
                if (message.IsReasoningExpanded && message.HasReasoning)
                    SendReasoningPayloadToWeb(ChatMessageIds.UiId(message));
                break;
            case nameof(ChatMessage.IsStreaming):
                if (!message.IsStreaming)
                    FinalizeMessageInWeb(message);
                else
                    SyncMessage(message);
                break;
            case nameof(ChatMessage.IsThinking):
                PatchReasoningMeta(message);
                if (message.HasReasoning)
                {
                    var thinkingState = EnsureState(message);
                    SyncRawStreamTail(thinkingState, ResolveReasoningMarkdown(message), reasoning: true);
                }
                break;
            case nameof(ChatMessage.IsToolExpanded):
                PatchToolExpandedState(message);
                break;
            case nameof(ChatMessage.ToolCallLabel):
                PatchToolStreamingUpdate(message);
                break;
            case nameof(ChatMessage.IsToolRunning):
                PatchToolStreamingUpdate(message);
                if (!message.IsToolRunning)
                {
                    FinalizeMessageInWeb(message);
                    EnsureDispatchWorker();
                }
                else
                    SyncMessage(message);
                break;
        }
    }

    private void SyncAllMessages(bool force = false)
    {
        if (_vm is null)
            return;

        PruneMessageStatesNotOnSurface();
        var fingerprint = ChatMessageRenderFingerprint.ForMessages(GetVisibleMessages());
        if (HasLiveRoundInProgress())
            return;

        if (!force
            && !_sessionSwitchSyncPending
            && _isReady
            && string.Equals(fingerprint, _lastSurfaceSyncFingerprint, StringComparison.Ordinal))
            return;

        if (_webView is null)
        {
            _lastSurfaceSyncFingerprint = null;
            _deferredFullSyncPending = true;
            return;
        }

        _lastSurfaceSyncFingerprint = fingerprint;
        try
        {
            SendSessionWindow(scrollToEnd: true);
        }
        catch (Exception ex)
        {
            _lastSurfaceSyncFingerprint = null;
            AppDiagnostics.Exception("AiChatWebView SyncAllMessages failed", ex);
        }
    }

    private void SendSessionWindow(bool scrollToEnd = true)
    {
        if (_vm is null || _surfaceSyncInProgress)
            return;

        if (_webView is null || !_isReady)
        {
            _deferredFullSyncPending = true;
            return;
        }

        if (HasLiveRoundInProgress())
        {
            AppDiagnostics.Mark("AiChatWebView initSessionWindow skipped liveRound");
            _deferredFullSyncPending = true;
            ScheduleDeferredFullSync();
            return;
        }

        if (ShouldDeferFullSurfaceSync())
        {
            _deferredFullSyncPending = true;
            ScheduleDeferredFullSync();
            return;
        }

        _sessionSwitchSyncPending = true;

        _surfaceSyncInProgress = true;
        BeginSurfaceSync();
        try
        {
            if (HasLiveRoundInProgress())
            {
                AppDiagnostics.Mark("AiChatWebView initSessionWindow aborted liveRound");
                _deferredFullSyncPending = true;
                return;
            }

            var items = BuildAllMessagesJson();
            if (EstimateJsonChars(items) > MaxInlineScriptJsonChars)
            {
                SendInitSessionWindowChunked(items, scrollToEnd);
            }
            else
            {
                AppDiagnostics.Mark($"AiChatWebView initSessionWindow count={items.Count} full=1");
                SendCommand(new JsonObject
                {
                    ["type"] = "initSessionWindow",
                    ["messages"] = items,
                    ["scrollToEnd"] = scrollToEnd,
                    ["waiting"] = _vm.IsWaitingForReply,
                    ["readOnly"] = _vm.IsReadOnlySession,
                    ["theme"] = ResolveChatTheme()
                }, bypassDefer: true);
            }

            SendWaitingState();
            SendReadOnlyState();
            SchedulePendingMarkdownRenders(
                Math.Max(0, GetVisibleMessages().Count - PendingMarkdownTailCount));
            PruneMessageStatesNotOnSurface();
            SubscribeVisibleMessages();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Exception("AiChatWebView SendSessionWindow failed", ex);
            _deferredFullSyncPending = true;
        }
        finally
        {
            EndSurfaceSyncEnqueue();
        }
    }

    private async Task HydrateMessageWindowAsync(int from, int to)
    {
        if (_vm is null || _webView is null || !_isReady)
            return;

        var loaded = await _vm.HydrateVisibleRangeForWebAsync(from, to).ConfigureAwait(true);
        if (loaded.Count == 0)
            return;

        var visible = GetVisibleMessages();
        var context = CreateMessageBuildContext(visible);
        var batch = new JsonArray();
        foreach (var message in loaded)
            batch.Add((JsonObject)BuildListMessageJson(message, context, deferredShell: false).DeepClone());

        if (batch.Count == 0)
            return;

        SendCommand(new JsonObject
        {
            ["type"] = "hydrateMessages",
            ["messages"] = batch
        }, bypassDefer: true);

        var fromIndex = visible.IndexOf(loaded[0]);
        var toIndex = visible.IndexOf(loaded[^1]);
        if (fromIndex >= 0 && toIndex >= fromIndex)
            SchedulePendingMarkdownRenders(fromIndex, toIndex);
    }

    private void SendInitSessionWindowChunked(JsonArray items, bool scrollToEnd)
    {
        if (_vm is null)
            return;

        AppDiagnostics.Mark($"AiChatWebView initSessionWindow chunked count={items.Count}");
        SendCommand(new JsonObject
        {
            ["type"] = "initSessionWindow",
            ["messages"] = new JsonArray(),
            ["scrollToEnd"] = items.Count == 0 && scrollToEnd,
            ["waiting"] = _vm.IsWaitingForReply,
            ["readOnly"] = _vm.IsReadOnlySession,
            ["theme"] = ResolveChatTheme()
        }, bypassDefer: true);

        if (items.Count == 0)
            return;

        foreach (var command in BuildAdaptiveAppendCommands(items, scrollToEnd))
            SendCommand(command, bypassDefer: true);
    }

    /// <summary>
    /// 按 InvokeScript 体积把整表切成多条独立 appendMessages（构建期即保证每条命令 &lt; 限额），
    /// 单条消息本身超限时降级为 deferred 壳、内容按需 hydrate。分派层无需再做运行时递归拆分。
    /// </summary>
    private List<JsonObject> BuildAdaptiveAppendCommands(JsonArray items, bool scrollToEnd)
    {
        var commands = new List<JsonObject>();
        var batch = new JsonArray();
        var batchChars = 0;
        var targetChars = (int)(MaxInlineScriptJsonChars * 0.72);

        void Flush(bool last)
        {
            if (batch.Count == 0)
                return;
            commands.Add(CreateAppendMessagesCommand(batch, scrollToEnd: last && scrollToEnd));
            batch = new JsonArray();
            batchChars = 0;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var clone = items[i]!.DeepClone();
            var itemChars = EstimateJsonChars(clone);

            if (itemChars > targetChars && clone is JsonObject oversized)
            {
                clone = DegradeToDeferredShell(oversized);
                itemChars = EstimateJsonChars(clone);
            }

            if (batch.Count > 0 && batchChars + itemChars > targetChars)
                Flush(last: false);

            batch.Add(clone);
            batchChars += itemChars;
        }

        Flush(last: true);
        return commands;
    }

    private static JsonObject CreateAppendMessagesCommand(JsonArray batch, bool scrollToEnd) =>
        new()
        {
            ["type"] = "appendMessages",
            ["messages"] = batch,
            ["scrollToEnd"] = scrollToEnd
        };

    private static JsonObject DegradeToDeferredShell(JsonObject message)
    {
        var lite = (JsonObject)message.DeepClone();
        lite["deferred"] = true;
        lite["blocks"] = new JsonArray();
        lite["tailMarkdown"] = string.Empty;
        lite["reasoningBlocks"] = new JsonArray();
        lite["reasoningTailMarkdown"] = string.Empty;
        lite["toolArgumentsJson"] = string.Empty;
        lite["toolResultJson"] = string.Empty;
        if (lite["role"]?.GetValue<string>() == "user")
            lite["content"] = string.Empty;
        return lite;
    }

    private static int EstimateJsonChars(JsonNode? node) =>
        node?.ToJsonString().Length ?? 0;

    private JsonArray BuildAllMessagesJson()
    {
        var visible = GetVisibleMessages();
        var messages = new JsonArray();
        if (_vm is null || visible.Count == 0)
            return messages;

        var context = CreateMessageBuildContext(visible);
        var tailStart = Math.Max(0, visible.Count - InitialHydrateTailCount);
        for (var i = 0; i < visible.Count; i++)
        {
            var deferredShell = i < tailStart;
            messages.Add((JsonObject)BuildListMessageJson(visible[i], context, deferredShell).DeepClone());
        }

        return messages;
    }

    private sealed class MessageBuildContext
    {
        public required string SessionId { get; init; }
        public required string ModelId { get; init; }
        public required Dictionary<ChatMessage, int> IndexByMessage { get; init; }
    }

    private MessageBuildContext CreateMessageBuildContext(IReadOnlyList<ChatMessage> visible)
    {
        var indexByMessage = new Dictionary<ChatMessage, int>(visible.Count);
        for (var i = 0; i < visible.Count; i++)
            indexByMessage[visible[i]] = i;

        foreach (var message in visible)
            EnsureState(message);

        return new MessageBuildContext
        {
            SessionId = _vm!.CurrentSessionId ?? string.Empty,
            ModelId = _vm.SelectedModel ?? string.Empty,
            IndexByMessage = indexByMessage
        };
    }

    private long ResolveToolMessageId(ChatMessage message)
    {
        if (!message.HasToolCall
            || message.IsStreaming
            || message.IsToolRunning
            || message.IsThinking)
            return -1;

        return message.Id > 0 ? message.Id : -1;
    }

    private void SyncMessage(ChatMessage message)
    {
        if (_vm is null || !message.Visual.IsVisibleInUi() || !_vm.Messages.Contains(message))
            return;

        if (ShouldDeferHistoricalIncrementalSync(message))
            return;

        EnsureState(message);
        EnsureMessageShellInWeb(message);

        if (message.HasToolCall)
        {
            PatchToolSummary(message);
            return;
        }

        var state = EnsureState(message);

        if (message.HasReasoning)
        {
            PatchReasoningMeta(message);
            if (message.IsThinking)
                SyncRawStreamTail(state, ResolveReasoningMarkdown(message), reasoning: true);
        }

        if (message.ShowAssistantMarkdown)
            SyncAssistantMarkdown(message);
        else if (!message.HasReasoning)
        {
            SendCommand(new JsonObject
            {
                ["type"] = "upsertMessage",
                ["message"] = (JsonObject)BuildListMessageJson(message).DeepClone()
            });
        }

        if (!message.IsStreaming)
            PatchStreamingState(message);
    }

    private void SyncAssistantMarkdown(ChatMessage message)
    {
        var state = EnsureState(message);
        var content = message.Content ?? string.Empty;

        if (message.IsStreaming)
        {
            PushIncrementalMarkdown(state, state.Streamer, content);
            return;
        }

        if (!string.IsNullOrEmpty(content))
        {
            if (state.ContentMarkdigFinalized && state.FedContentLength != content.Length)
            {
                state.ContentMarkdigFinalized = false;
                ResetMarkdownChannel(state, state.Streamer);
            }

            if (!state.ContentMarkdigFinalized && !state.ContentRenderPending)
            {
                FinalizeStreamingMarkdownInPlace(message, state);
                if (state.ContentMarkdigFinalized)
                    return;
            }
        }

        RequestBackgroundMarkdownRender(message, state, content, reasoning: false);
    }

    /// <summary>流结束：全量 Markdig → 写入 ContentHtml，并用 blocks 替换 WebView 中的 raw tail。</summary>
    private void FinalizeStreamingMarkdownInPlace(ChatMessage message, MessageRenderState state)
    {
        var content = ResolveContentMarkdown(message);
        if (string.IsNullOrWhiteSpace(content))
            return;

        ResetMarkdownChannel(state, state.Streamer);

        var snapshotBlocks = CommitMarkdownSnapshotOnce(state.Streamer, content);
        if (snapshotBlocks.Count == 0)
            return;

        state.ContentBlocks.AddRange(snapshotBlocks);
        RegisterSentBlocks(state.SentContentBlockIds, snapshotBlocks);

        SetFedLength(state, reasoning: false, content.Length);
        var html = ChatMessageRenderCache.Serialize(state.ContentBlocks);
        message.ApplyRenderedHtml(reasoning: false, html);
        SendReplaceBlocks(state.Id, reasoning: false, state.ContentBlocks);
        SendStreamTail(state, reasoning: false, string.Empty);
        SetMarkdigFinalized(state, reasoning: false, finalized: true);
        state.ContentRenderPending = false;
        ReleaseInMemoryMarkdownBlocks(state, reasoning: false);
        _vm?.NotifyMarkdownRenderCompleted(message);
    }

    private void ApplyFinalContentHtmlToWeb(ChatMessage message)
    {
        if (!message.ShowAssistantMarkdown || message.IsStreaming)
            return;

        var state = EnsureState(message);
        if (!TryLoadCachedBlocks(message, state, reasoning: false, out var blocks) || blocks.Count == 0)
            return;

        SendReplaceBlocks(state.Id, reasoning: false, blocks);
        SendStreamTail(state, reasoning: false, string.Empty);
    }

    private void PatchToolStreamingUpdate(ChatMessage message)
    {
        if (!_messageStates.TryGetValue(message, out var state))
            return;

        SendCommand(new JsonObject
        {
            ["type"] = "patchMessage",
            ["id"] = state.Id,
            ["command"] = message.ToolCommand ?? string.Empty,
            ["isToolRunning"] = message.IsToolRunning,
            ["toolExitCode"] = message.ToolExitCode,
            ["title"] = message.ToolDisplayName,
            ["toolArgumentsJson"] = CloneJsonNodeForWeb(BuildToolArgumentsPayloadForWeb(message)),
            ["toolResultJson"] = CloneJsonNodeForWeb(BuildToolResultPayloadForWeb(message))
        }, bypassDefer: true);
    }

    private void PatchToolExpandedState(ChatMessage message)
    {
        if (!_messageStates.TryGetValue(message, out var state))
            return;

        SendCommand(new JsonObject
        {
            ["type"] = "patchMessage",
            ["id"] = state.Id,
            ["toolExpanded"] = message.IsToolExpanded
        }, bypassDefer: true);
    }

    private void PatchReasoningExpandedState(ChatMessage message)
    {
        if (!_messageStates.TryGetValue(message, out var state))
            return;

        SendCommand(new JsonObject
        {
            ["type"] = "patchMessage",
            ["id"] = state.Id,
            ["reasoningExpanded"] = message.IsReasoningExpanded
        }, bypassDefer: true);
    }

    private void PatchToolSummary(ChatMessage message) => PatchToolStreamingUpdate(message);

    private JsonObject BuildListMessageJson(ChatMessage message) =>
        BuildListMessageJson(message, CreateMessageBuildContext(GetVisibleMessages()), deferredShell: false);

    private JsonObject BuildListMessageJson(ChatMessage message, MessageBuildContext context, bool deferredShell)
    {
        var state = EnsureState(message);
        context.IndexByMessage.TryGetValue(message, out var seq);
        var toolMessageId = ResolveToolMessageId(message);

        if (deferredShell)
        {
            var shell = new JsonObject
            {
                ["id"] = state.Id,
                ["seq"] = seq,
                ["role"] = ResolveRole(message),
                ["content"] = message.IsUser && !message.IsArchiveShell ? message.Content : string.Empty,
                ["title"] = message.HasToolCall ? message.ToolDisplayName : string.Empty,
                ["command"] = message.HasToolCall ? message.ToolCommand : string.Empty,
                ["toolExitCode"] = message.HasToolCall ? message.ToolExitCode : 0,
                ["isStreaming"] = false,
                ["isToolRunning"] = false,
                ["hasReasoning"] = message.HasReasoning,
                ["isThinking"] = false,
                ["reasoningExpanded"] = message.IsReasoningExpanded,
                ["toolExpanded"] = message.IsToolExpanded,
                ["thinkingLabel"] = message.ThinkingLabel,
                ["blocks"] = new JsonArray(),
                ["tailMarkdown"] = string.Empty,
                ["reasoningBlocks"] = new JsonArray(),
                ["reasoningTailMarkdown"] = string.Empty,
                ["toolResultJson"] = string.Empty,
                ["toolArgumentsJson"] = string.Empty
            };

            // 落盘 content_html 可直接还原 blocks，不必 deferred + 二次 hydrate/Markdig。
            if (!message.HasToolCall && !string.IsNullOrEmpty(message.ContentHtml))
            {
                ApplyAssistantContentPayload(shell, message, state);
                ApplyAssistantReasoningPayload(shell, message, state);
                if (HasAssistantRenderablePayload(shell))
                    return shell;
            }

            shell["deferred"] = true;
            return shell;
        }

        var json = new JsonObject
        {
            ["id"] = state.Id,
            ["seq"] = seq,
            ["role"] = ResolveRole(message),
            ["content"] = message.IsUser
                ? message.IsArchiveShell ? string.Empty : message.Content
                : string.Empty,
            ["title"] = message.HasToolCall ? message.ToolDisplayName : string.Empty,
            ["command"] = message.HasToolCall ? message.ToolCommand : string.Empty,
            ["toolExitCode"] = message.HasToolCall ? message.ToolExitCode : 0,
            ["isStreaming"] = message.IsStreaming,
            ["isToolRunning"] = message.IsToolRunning,
            ["hasReasoning"] = message.HasReasoning,
            ["isThinking"] = message.IsThinking,
            ["reasoningExpanded"] = message.IsReasoningExpanded,
            ["toolExpanded"] = message.IsToolExpanded,
            ["thinkingLabel"] = message.ThinkingLabel,
            ["blocks"] = new JsonArray(),
            ["tailMarkdown"] = string.Empty,
            ["reasoningBlocks"] = new JsonArray(),
            ["reasoningTailMarkdown"] = string.Empty,
            ["toolResultJson"] = string.Empty,
            ["toolArgumentsJson"] = string.Empty
        };

        if (!message.HasToolCall)
            ApplyAssistantContentPayload(json, message, state);

        ApplyAssistantReasoningPayload(json, message, state);
        ApplyToolPayload(json, message, context.SessionId, context.ModelId, toolMessageId);

        return json;
    }

    private void ApplyToolPayload(JsonObject json, ChatMessage message, string sessionId, string modelId, long toolMessageId)
    {
        if (!message.HasToolCall)
            return;

        if (message.IsToolRunning || message.IsToolExpanded)
        {
            json["toolArgumentsJson"] = CloneJsonNodeForWeb(BuildToolArgumentsPayloadForWeb(message))
                ?? JsonValue.Create(string.Empty);
            json["toolResultJson"] = CloneJsonNodeForWeb(BuildToolResultPayloadForWeb(message, sessionId, modelId, toolMessageId))
                ?? JsonValue.Create(string.Empty);
            return;
        }

        json["toolArgumentsJson"] = JsonValue.Create(string.Empty);
        json["toolResultJson"] = JsonValue.Create(string.Empty);
    }

    private static bool HasAssistantRenderablePayload(JsonObject json)
    {
        if (json["blocks"] is JsonArray blocks && blocks.Count > 0)
            return true;

        var tail = json["tailMarkdown"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(tail);
    }

    private void ApplyAssistantContentPayload(JsonObject json, ChatMessage message, MessageRenderState state)
    {
        var markdown = ResolveContentMarkdown(message);

        if (message.IsStreaming)
        {
            json["blocks"] = (JsonNode?)BlocksToJson(state.ContentBlocks).DeepClone();
            var tail = !string.IsNullOrEmpty(state.Streamer.CurrentTailMarkdown)
                ? state.Streamer.CurrentTailMarkdown
                : markdown;
            json["tailMarkdown"] = TruncateInlineTail(tail);
            return;
        }

        if (TryLoadCachedBlocks(message, state, reasoning: false, out var blocks) && blocks.Count > 0)
        {
            json["blocks"] = (JsonNode?)BlocksToJson(blocks).DeepClone();
            json["tailMarkdown"] = string.Empty;
            return;
        }

        if (string.IsNullOrEmpty(markdown))
            return;

        json["blocks"] = new JsonArray();
        json["tailMarkdown"] = TruncateInlineTail(markdown);
    }

    private void ApplyAssistantReasoningPayload(JsonObject json, ChatMessage message, MessageRenderState state)
    {
        if (!message.HasReasoning)
            return;

        var markdown = ResolveReasoningMarkdown(message);
        if (string.IsNullOrEmpty(markdown))
            return;

        json["reasoningBlocks"] = new JsonArray();
        json["reasoningTailMarkdown"] = TruncateInlineTail(markdown);
    }

    private static string TruncateInlineTail(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (text.Length <= MaxInlineTailMarkdownChars)
            return text;

        return text[..MaxInlineTailMarkdownChars]
               + "\n\n…（内容较长，正在后台渲染完整 Markdown）";
    }

    private void PatchReasoningMeta(ChatMessage message)
    {
        if (!_messageStates.TryGetValue(message, out var state))
            return;

        var patch = new JsonObject
        {
            ["type"] = "patchMessage",
            ["id"] = state.Id,
            ["hasReasoning"] = message.HasReasoning,
            ["isThinking"] = message.IsThinking,
            ["thinkingLabel"] = message.ThinkingLabel
        };

        // 思考 tail 仅由 SyncRawStreamTail / 全量 upsert / requestReasoningPayload 下发，避免 patch 误刷历史或其它消息。
        if (message.IsThinking)
            patch["reasoningTailMarkdown"] = TruncateInlineTail(ResolveReasoningMarkdown(message));

        SendCommand(patch, bypassDefer: true);
    }

    /// <summary>SSE 流式阶段：Markdig 增量块 + raw tail；每个 chunk 立即推送。</summary>
    private void PushIncrementalMarkdown(
        MessageRenderState state,
        MarkdigBlockStreamer streamer,
        string content)
    {
        if (!IsLiveSurfaceMessage(state.Message))
            return;

        if (state.ContentMarkdigFinalized)
            return;

        if (content.Length < state.FedContentLength)
            ResetMarkdownChannel(state, streamer);

        if (content.Length <= state.FedContentLength)
            return;

        var delta = content[state.FedContentLength..];
        var update = streamer.Append(delta);
        SetFedLength(state, reasoning: false, content.Length);

        if (update.AppendBlocks.Count > 0)
        {
            state.ContentBlocks.AddRange(update.AppendBlocks);
            RegisterSentBlocks(state.SentContentBlockIds, update.AppendBlocks);
            SendAppendBlocks(state.Id, update.AppendBlocks);
        }

        SendStreamTail(state, reasoning: false, update.TailMarkdown);
    }

    private static void ResetMarkdownChannel(MessageRenderState state, MarkdigBlockStreamer streamer)
    {
        streamer.Reset();
        state.ContentBlocks.Clear();
        state.SentContentBlockIds.Clear();
        state.FedContentLength = 0;
        state.ContentMarkdigFinalized = false;
        state.LastDispatchedContentTail = null;
        state.QueuedContentTail = null;
    }

    private static List<RenderedMarkdownBlock> CommitMarkdownSnapshotOnce(MarkdigBlockStreamer streamer, string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var blocks = streamer.CommitSnapshot(markdown).ToList();
        if (blocks.Count == 0)
            blocks.Add(PlainTextMarkdownBlock(markdown));

        return blocks;
    }

    private static RenderedMarkdownBlock PlainTextMarkdownBlock(string text)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var html = string.Join("<br/>", encoded.Split('\n'));
        return new RenderedMarkdownBlock("b0", $"<p>{html}</p>");
    }

    /// <summary>思考链：仅推送 raw Markdown tail，不做 Markdig。</summary>
    private void SyncRawStreamTail(MessageRenderState state, string rawMarkdown, bool reasoning)
    {
        if (!reasoning || !IsLiveSurfaceMessage(state.Message))
            return;

        SendStreamTail(state, reasoning: true, rawMarkdown);
    }

    private static void SetFedLength(MessageRenderState state, bool reasoning, int length)
    {
        if (reasoning)
            state.FedReasoningLength = length;
        else
            state.FedContentLength = length;
    }

    private static void SetMarkdigFinalized(MessageRenderState state, bool reasoning, bool finalized)
    {
        if (reasoning)
            state.ReasoningMarkdigFinalized = finalized;
        else
            state.ContentMarkdigFinalized = finalized;
    }

    private void SendStreamTail(MessageRenderState state, bool reasoning, string tailMarkdown)
    {
        var dispatched = reasoning ? state.LastDispatchedReasoningTail : state.LastDispatchedContentTail;
        var queued = reasoning ? state.QueuedReasoningTail : state.QueuedContentTail;
        if (string.Equals(dispatched, tailMarkdown, StringComparison.Ordinal))
        {
            SetQueuedTail(state, reasoning, null);
            return;
        }

        if (string.Equals(queued, tailMarkdown, StringComparison.Ordinal))
            return;

        if (string.IsNullOrEmpty(tailMarkdown))
        {
            if (string.IsNullOrEmpty(dispatched) && string.IsNullOrEmpty(queued))
                return;

            SetDispatchedTail(state, reasoning, string.Empty);
            SetQueuedTail(state, reasoning, null);
            SendCommand(new JsonObject
            {
                ["type"] = "setStreamTail",
                ["messageId"] = state.Id,
                ["reasoning"] = reasoning,
                ["markdown"] = string.Empty
            }, bypassDefer: true);
            return;
        }

        if (!string.IsNullOrEmpty(dispatched)
            && tailMarkdown.StartsWith(dispatched, StringComparison.Ordinal)
            && tailMarkdown.Length > dispatched.Length)
        {
            var delta = tailMarkdown[dispatched.Length..];
            if (delta.Length == 0)
                return;

            SetDispatchedTail(state, reasoning, tailMarkdown);
            SetQueuedTail(state, reasoning, null);
            SendCommand(new JsonObject
            {
                ["type"] = "appendStreamTail",
                ["messageId"] = state.Id,
                ["reasoning"] = reasoning,
                ["delta"] = delta
            }, bypassDefer: true);
            return;
        }

        SetDispatchedTail(state, reasoning, tailMarkdown);
        SetQueuedTail(state, reasoning, null);
        SendCommand(new JsonObject
        {
            ["type"] = "setStreamTail",
            ["messageId"] = state.Id,
            ["reasoning"] = reasoning,
            ["markdown"] = tailMarkdown
        }, bypassDefer: true);
    }

    private static void SetDispatchedTail(MessageRenderState state, bool reasoning, string tail)
    {
        if (reasoning)
            state.LastDispatchedReasoningTail = tail;
        else
            state.LastDispatchedContentTail = tail;
    }

    private static void SetQueuedTail(MessageRenderState state, bool reasoning, string? tail)
    {
        if (reasoning)
            state.QueuedReasoningTail = tail;
        else
            state.QueuedContentTail = tail;
    }

    private MessageRenderState? FindStateByWebId(string messageId)
    {
        MessageRenderState? fallback = null;
        foreach (var state in _messageStates.Values.ToList())
        {
            if (!string.Equals(state.Id, messageId, StringComparison.Ordinal))
                continue;

            if (IsLiveSurfaceMessage(state.Message))
                return state;

            fallback ??= state;
        }

        return fallback;
    }

    private JsonObject? CreateAuthoritativeStreamTailCommand(string messageId, bool reasoning)
    {
        var state = FindStateByWebId(messageId);
        if (state is null)
            return null;

        var markdown = reasoning
            ? (state.Message.ReasoningContent ?? string.Empty)
            : ResolveContentMarkdown(state.Message);

        SetQueuedTail(state, reasoning, markdown);
        return new JsonObject
        {
            ["type"] = "setStreamTail",
            ["messageId"] = messageId,
            ["reasoning"] = reasoning,
            ["markdown"] = markdown
        };
    }

    private void AckStreamTailDispatched(JsonObject command)
    {
        var type = command["type"]?.GetValue<string>();
        switch (type)
        {
            case "setStreamTail":
            {
                var messageId = command["messageId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(messageId))
                    return;

                var reasoning = command["reasoning"]?.GetValue<bool>() ?? false;
                var markdown = command["markdown"]?.GetValue<string>() ?? string.Empty;
                if (FindStateByWebId(messageId) is not { } state)
                    return;

                SetDispatchedTail(state, reasoning, markdown);
                SetQueuedTail(state, reasoning, null);
                return;
            }
            case "appendStreamTail":
            {
                var messageId = command["messageId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(messageId))
                    return;

                var reasoning = command["reasoning"]?.GetValue<bool>() ?? false;
                var delta = command["delta"]?.GetValue<string>() ?? string.Empty;
                if (FindStateByWebId(messageId) is not { } state)
                    return;

                var dispatched = reasoning ? state.LastDispatchedReasoningTail : state.LastDispatchedContentTail;
                SetDispatchedTail(state, reasoning, (dispatched ?? string.Empty) + delta);
                SetQueuedTail(state, reasoning, null);
                return;
            }
            case "batch" when command["commands"] is JsonArray batch:
                foreach (var node in batch)
                {
                    if (node is JsonObject obj)
                        AckStreamTailDispatched(obj);
                }

                break;
        }
    }

    private void SendAppendBlocks(string messageId, IReadOnlyList<RenderedMarkdownBlock> blocks)
    {
        if (blocks.Count == 0)
            return;

        SendCommand(new JsonObject
        {
            ["type"] = "appendBlocks",
            ["messageId"] = messageId,
            ["reasoning"] = false,
            ["blocks"] = BlocksToJson(blocks)
        }, bypassDefer: true);
    }

    private void SendReplaceBlocks(string messageId, bool reasoning, IReadOnlyList<RenderedMarkdownBlock> blocks)
    {
        if (blocks.Count > 0 && FindStateByWebId(messageId) is { } state)
        {
            SetDispatchedTail(state, reasoning, string.Empty);
            SetQueuedTail(state, reasoning, null);
        }

        SendCommand(new JsonObject
        {
            ["type"] = "replaceBlocks",
            ["messageId"] = messageId,
            ["reasoning"] = reasoning,
            ["blocks"] = BlocksToJson(blocks)
        }, bypassDefer: true);
    }

    private static void RegisterSentBlocks(HashSet<string> sent, IReadOnlyList<RenderedMarkdownBlock> blocks)
    {
        foreach (var block in blocks)
            sent.Add(block.Id);
    }

    private void PatchStreamingState(ChatMessage message)
    {
        if (!_messageStates.TryGetValue(message, out var state))
            return;

        SendCommand(new JsonObject
        {
            ["type"] = "patchMessage",
            ["id"] = state.Id,
            ["isStreaming"] = false,
            ["isThinking"] = false
        });
    }

    private static string ResolveRole(ChatMessage message)
    {
        if (message.IsUser)
            return "user";
        return message.HasToolCall ? "tool" : "assistant";
    }

    private void SetToolExpandedFromWeb(string messageUiId, bool expanded)
    {
        if (_vm is null)
            return;

        var message = _vm.Messages.FirstOrDefault(m => ChatMessageIds.MatchesUiId(m, messageUiId));
        if (message is null || !message.HasToolCall)
            return;

        if (message.IsToolExpanded != expanded)
            message.IsToolExpanded = expanded;

        if (expanded)
            SendToolPayloadToWeb(messageUiId);
    }

    private void SetReasoningExpandedFromWeb(string messageUiId, bool expanded)
    {
        if (_vm is null)
            return;

        var message = _vm.Messages.FirstOrDefault(m => ChatMessageIds.MatchesUiId(m, messageUiId));
        if (message is null || !message.HasReasoning)
            return;

        if (message.IsReasoningExpanded != expanded)
            message.IsReasoningExpanded = expanded;
    }

    private void SendToolPayloadToWeb(string messageUiId)
    {
        if (_vm is null)
            return;

        var message = _vm.Messages.FirstOrDefault(m => ChatMessageIds.MatchesUiId(m, messageUiId));
        if (message is null || !message.HasToolCall)
            return;

        if (!_messageStates.TryGetValue(message, out var state))
            return;

        AppDiagnostics.Mark($"AiChatWebView requestToolPayload id={messageUiId} outputLength={message.ToolOutput?.Length ?? 0}");
        SendCommand(new JsonObject
        {
            ["type"] = "patchMessage",
            ["id"] = state.Id,
            ["toolExpanded"] = true,
            ["toolExitCode"] = message.ToolExitCode,
            ["toolArgumentsJson"] = CloneJsonNodeForWeb(BuildToolArgumentsPayloadForWeb(message)),
            ["toolResultJson"] = CloneJsonNodeForWeb(BuildToolResultPayloadForWeb(message))
        }, bypassDefer: true);
    }

    private void SendReasoningPayloadToWeb(string messageUiId)
    {
        if (_vm is null)
            return;

        var message = _vm.Messages.FirstOrDefault(m => ChatMessageIds.MatchesUiId(m, messageUiId));
        if (message is null || !message.HasReasoning)
            return;

        if (!_messageStates.TryGetValue(message, out var state))
            return;

        var reasoningMarkdown = ResolveReasoningMarkdown(message);
        AppDiagnostics.Mark($"AiChatWebView requestReasoningPayload id={messageUiId} length={reasoningMarkdown.Length}");

        var patch = new JsonObject
        {
            ["type"] = "patchMessage",
            ["id"] = state.Id,
            ["reasoningExpanded"] = true,
            ["hasReasoning"] = message.HasReasoning,
            ["isThinking"] = message.IsThinking,
            ["thinkingLabel"] = message.ThinkingLabel,
            ["reasoningBlocks"] = new JsonArray(),
            ["reasoningTailMarkdown"] = TruncateInlineTail(reasoningMarkdown)
        };
        SendCommand(patch, bypassDefer: true);
    }

    private static JsonNode? BuildToolArgumentsPayloadForWeb(ChatMessage message)
    {
        if (!message.HasToolCall)
            return JsonValue.Create(string.Empty);

        var raw = message.ToolArgumentsJson ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return JsonValue.Create(string.Empty);

        // SSE 流式 arguments 常是不完整 JSON；按 raw 文本下发，避免 Parse 失败只剩一个「{」。
        if (message.IsToolRunning || !IsCompleteJsonDocument(raw))
            return JsonValue.Create(raw);

        return ToolResultJson.ParseToolPayload(raw);
    }

    private JsonNode? BuildToolResultPayloadForWeb(ChatMessage message)
    {
        if (_vm is null || !message.HasToolCall)
            return JsonValue.Create(string.Empty);

        var index = _vm.Messages.IndexOf(message);
        var toolMessageId = index >= 0 ? ChatHistoryMapper.GetToolMessageId(_vm.Messages, index) : -1;
        return BuildToolResultPayloadForWeb(
            message,
            _vm.CurrentSessionId ?? string.Empty,
            _vm.SelectedModel ?? string.Empty,
            toolMessageId);
    }

    private JsonNode? BuildToolResultPayloadForWeb(ChatMessage message, string sessionId, string modelId, long toolMessageId)
    {
        if (!message.HasToolCall)
            return JsonValue.Create(string.Empty);

        var path = toolMessageId > 0
            ? ChatContextCompressionService.BuildToolResultPath(toolMessageId)
            : "@tool_result:unknown";
        var output = ToolResultContextProjection.ProjectForApi(message.ToolOutput, modelId, path);
        return ToolResultJson.ParseToolPayload(output);
    }

    private static bool IsCompleteJsonDocument(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBulkInitCommand(JsonObject command)
    {
        var type = command["type"]?.GetValue<string>();
        return type is "initSessionWindow" or "appendMessages" or "batch" or "hydrateMessages";
    }

    private static string ResolveContentMarkdown(ChatMessage message)
    {
        if (message.HasToolCall)
            return string.Empty;
        if (message.ShowAssistantMarkdown)
            return message.Content;
        return string.Empty;
    }

    private static string ResolveReasoningMarkdown(ChatMessage message)
    {
        if (message.HasReasoning)
            return message.ReasoningContent;
        return string.Empty;
    }

    private static JsonNode? CloneJsonNodeForWeb(JsonNode? node) =>
        node?.DeepClone();

    private static JsonArray BlocksToJson(IEnumerable<RenderedMarkdownBlock> blocks)
    {
        var array = new JsonArray();
        foreach (var block in blocks)
        {
            array.Add(new JsonObject
            {
                ["id"] = block.Id,
                ["html"] = block.Html
            });
        }
        return array;
    }

    private void SendCommand(JsonObject command, bool bypassDefer = false)
    {
        SendCommandImmediate(command, bypassDefer);
    }

    private void SendCommandImmediate(JsonObject command, bool bypassDefer)
    {
        if (ShouldDropCommandDuringLiveRound(command))
        {
            var type = command["type"]?.GetValue<string>();
            if (string.Equals(type, "initSessionWindow", StringComparison.Ordinal)
                || string.Equals(type, "hydrateMessages", StringComparison.Ordinal))
                _deferredFullSyncPending = true;
            return;
        }

        if (!bypassDefer && ShouldDeferIncrementalSync() && IsBulkUpsertCommand(command))
            return;

        EnqueueDispatch((JsonObject)command.DeepClone());
    }

    private bool ShouldDropCommandDuringLiveRound(JsonObject command)
    {
        // 压缩/会话恢复后的 initSessionWindow 若在用户已发起新一轮发送后才到达，会整表覆盖 Vue 状态导致界面无反应。
        if (!HasLiveRoundInProgress())
            return false;

        var type = command["type"]?.GetValue<string>();
        if (type == "batch" && command["commands"] is JsonArray batch)
        {
            foreach (var node in batch)
            {
                if (node is JsonObject obj && ShouldDropCommandDuringLiveRound(obj))
                    return true;
            }

            return false;
        }

        return type is "initSessionWindow" or "hydrateMessages";
    }

    private static bool IsToolLifecyclePatch(JsonObject command) =>
        command["isToolRunning"] is not null
        || command["toolResultJson"] is not null
        || command["toolArgumentsJson"] is not null;

    private bool HasPendingWebDispatch()
    {
        lock (_dispatchGate)
            return _dispatchQueue.Count > 0;
    }

    private JsonObject? TakeNextWebDispatchCommand()
    {
        lock (_dispatchGate)
        {
            if (_dispatchQueue.Count == 0)
                return null;

            return _dispatchQueue.Dequeue();
        }
    }

    private bool ShouldDeferIncrementalSync() =>
        (_vm?.IsLoadingHistory ?? false)
        || (_sessionSwitchSyncPending && !HasLiveRoundInProgress());

    private static bool IsBulkUpsertCommand(JsonObject command) =>
        command["type"]?.GetValue<string>() == "upsertMessage";

    private void BeginSurfaceSync()
    {
        lock (_dispatchGate)
            _dispatchQueue.Clear();

        foreach (var state in _messageStates.Values.ToList())
        {
            state.QueuedContentTail = null;
            state.QueuedReasoningTail = null;
        }

        _lastWebDispatchTicks = 0;
        _dispatchGeneration++;
    }

    private void EndSurfaceSyncEnqueue()
    {
        _surfaceSyncInProgress = false;
        _sessionSwitchSyncPending = false;
    }

    private void ScheduleStreamingToolPatch(ChatMessage message) => PatchToolSummary(message);

    private void EnqueueDispatch(JsonObject command)
    {
        if (command is null)
            return;

        lock (_dispatchGate)
        {
            _dispatchQueue.Enqueue(command);
        }

        EnsureDispatchWorker();
    }

    private void EnsureDispatchWorker()
    {
        if (Interlocked.CompareExchange(ref _dispatchWorkerRunningInt, 1, 0) != 0)
            return;

        _ = RunDispatchWorkerAsync();
    }

    private async Task RunDispatchWorkerAsync()
    {
        var generation = _dispatchGeneration;
        try
        {
            while (HasPendingWebDispatch())
            {
                if (generation != _dispatchGeneration)
                    return;

                var command = TakeNextWebDispatchCommand();
                if (command is null)
                    break;

                try
                {
                    await DispatchCommandsAsync(command).ConfigureAwait(false);
                    _lastWebDispatchTicks = Environment.TickCount64;
                    if (IsBulkInitCommand(command))
                        await Task.Yield();
                }
                catch (Exception ex)
                {
                    AppDiagnostics.Exception(
                        $"AiChatWebView dispatch failed type={command["type"]?.GetValue<string>() ?? "?"}",
                        ex);
                    _lastWebDispatchTicks = Environment.TickCount64;
                }
            }
        }
        finally
        {
            _dispatchWorkerRunningInt = 0;
            if (HasPendingWebDispatch())
                EnsureDispatchWorker();
        }
    }

    private async Task DispatchCommandsAsync(JsonObject command)
    {
        string json;
        try
        {
            json = command.ToJsonString(new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch (Exception ex)
        {
            AppDiagnostics.Exception(
                $"AiChatWebView dispatch serialize failed type={command["type"]?.GetValue<string>() ?? "?"}",
                ex);
            return;
        }

        AppDiagnostics.Mark($"AiChatWebView dispatch type={command["type"]?.GetValue<string>() ?? "?"} jsonLength={json.Length}");
        if (json.Length > MaxInlineScriptJsonChars)
        {
            // 初始化整表已在构建期按体积切分并对超大单条降级，正常不会走到这里；
            // 仅作最终兜底，避免 InvokeScript 静默丢弃超限脚本。
            AppDiagnostics.Mark(
                $"AiChatWebView dispatch skipped oversized type={command["type"]?.GetValue<string>() ?? "?"} jsonLength={json.Length}");
            return;
        }

        var script =
            $"(function(){{var c=window.zerofallChat;if(!c)return 'no-bridge';c.receive({json});return 'ok';}})()";

        var result = await EvaluateScriptResultAsync(script);
        AppDiagnostics.Mark($"AiChatWebView dispatch done type={command["type"]?.GetValue<string>() ?? "?"}");
        if (!string.Equals(result, "no-bridge", StringComparison.OrdinalIgnoreCase))
        {
            AckStreamTailDispatched(command);
            return;
        }

        _lastSurfaceSyncFingerprint = null;
        EnqueueDispatch((JsonObject)command.DeepClone());
    }

    private async Task<string?> EvaluateScriptResultAsync(string script, bool invokeOnly = false)
    {
        if (_webView is null)
            return null;

        async Task<string?> RunOnUiThreadAsync()
        {
            try
            {
                var result = await _webView!.InvokeScript(script);
                return invokeOnly ? null : result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiChatWebView] InvokeScript failed: {ex.Message}");
                return null;
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            return await RunOnUiThreadAsync().ConfigureAwait(true);

        return await Dispatcher.UIThread.InvokeAsync(RunOnUiThreadAsync);
    }

    private void DisposeWebView()
    {
        if (_webView is null)
            return;

        _webView.AdapterCreated -= OnAdapterCreated;
        _webView.NavigationStarted -= OnNavigationStarted;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.NewWindowRequested -= OnNewWindowRequested;
        _webView.WebMessageReceived -= OnWebMessageReceived;
        _host.Children.Remove(_webView);
        try
        {
            if (_webView is IDisposable disposable)
                disposable.Dispose();
        }
        catch
        {
        }

        _webView = null;
        _htmlLoaded = false;
        _htmlLoadAttempts = 0;
        _readyProbeAttempts = 0;
        _isReady = false;
        lock (_dispatchGate)
            _dispatchQueue.Clear();

        _lastWebDispatchTicks = 0;
        _surfaceSyncInProgress = false;
        _dispatchGeneration++;
        _webViewCreateScheduled = false;
    }

    private sealed class MessageRenderState
    {
        private readonly ChatMessage _message;

        public MessageRenderState(ChatMessage message) => _message = message;

        public string Id => ChatMessageIds.UiId(_message);
        public ChatMessage Message => _message;
        public List<RenderedMarkdownBlock> ContentBlocks { get; } = [];
        public List<RenderedMarkdownBlock> ReasoningBlocks { get; } = [];
        public HashSet<string> SentContentBlockIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SentReasoningBlockIds { get; } = new(StringComparer.Ordinal);
        public int FedContentLength { get; set; }
        public int FedReasoningLength { get; set; }
        public bool ContentRenderPending { get; set; }
        public bool ReasoningRenderPending { get; set; }
        public bool ContentMarkdigFinalized { get; set; }
        public bool ReasoningMarkdigFinalized { get; set; }
        public string? LastDispatchedContentTail { get; set; }
        public string? LastDispatchedReasoningTail { get; set; }
        public string? QueuedContentTail { get; set; }
        public string? QueuedReasoningTail { get; set; }
        public bool ShellPushedInWeb { get; set; }
        public MarkdigBlockStreamer Streamer { get; } = new();
    }
}
