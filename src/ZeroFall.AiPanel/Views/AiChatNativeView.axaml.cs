using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZeroFall.AiPanel.ViewModels;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Views;

/// <summary>
/// 原生 Avalonia 聊天渲染（LiveMarkdown）。用普通 <c>ScrollViewer + ItemsControl</c>（非虚拟化，
/// 无容器回收闪烁）承载 <see cref="AiPanelViewModel.SurfaceRounds"/>，配合
/// <see cref="LazyMarkdownPresenter"/> 做内容级懒加载；贴底跟随参考旧 NativeChatPanel。
/// 对外 API 与 <c>AiChatWebView</c> 对齐，便于在 AiPanelView 中平替。
/// </summary>
public partial class AiChatNativeView : UserControl, IDisposable
{
    private const double NearBottomThresholdPx = 48;
    private const double ExpandSurfaceNearTopThresholdPx = 120;

    public event EventHandler<AiChatWebViewStatusEventArgs>? StatusChanged;

    private AiPanelViewModel? _vm;
    private bool _autoFollow = true;
    private bool _scrollScheduled;
    private bool _surfaceExpandScheduled;
    private bool _suppressExtentAutoScroll;
    private bool _disposed;

    public AiChatNativeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ChatScroll.ScrollChanged += OnScrollChanged;
    }

    /// <summary>原生聊天即时就绪；同时解除 Content 浏览器对 AI WebView 的启动门闩。</summary>
    public void AttachWebViewWhenReady()
    {
        WebView2CreationCoordinator.MarkAiAdapterReady();
        StatusChanged?.Invoke(this, new AiChatWebViewStatusEventArgs(string.Empty, true));
        _autoFollow = true;
        ScheduleScrollToEnd();
    }

    public void ReleaseResources() => Detach();

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Detach();
        _vm = DataContext as AiPanelViewModel;
        if (_vm is null)
            return;

        _vm.ChatSurfaceRestored += OnChatSurfaceRestored;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.SurfaceRounds.CollectionChanged += OnSurfaceRoundsChanged;
        LazyMarkdownPresenter.OpenLink = OpenLink;
        StatusChanged?.Invoke(this, new AiChatWebViewStatusEventArgs(string.Empty, true));
        _autoFollow = true;
        ScheduleScrollToEnd();
    }

    private void OpenLink(Uri uri) => _vm?.OpenMarkdownLink(uri.ToString());

    private void Detach()
    {
        if (_vm is not null)
        {
            _vm.ChatSurfaceRestored -= OnChatSurfaceRestored;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.SurfaceRounds.CollectionChanged -= OnSurfaceRoundsChanged;
        }
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_autoFollow && e.PropertyName is nameof(AiPanelViewModel.IsWaitingForReply) or nameof(AiPanelViewModel.IsSending))
            ScheduleScrollToEnd();
    }

    private void OnSurfaceRoundsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_autoFollow && e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            ScheduleScrollToEnd();
    }

    private void OnChatSurfaceRestored(object? sender, EventArgs e)
    {
        _autoFollow = true;
        ScheduleScrollToEnd();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var extent = ChatScroll.Extent.Height;
        var viewport = ChatScroll.Viewport.Height;
        var offsetY = ChatScroll.Offset.Y;
        var atBottom = offsetY + viewport >= extent - NearBottomThresholdPx;

        if (offsetY < ExpandSurfaceNearTopThresholdPx)
            ScheduleExpandSurfaceRounds();

        if (e.OffsetDelta.Y < -1 && !atBottom)
            _autoFollow = false;
        else if (atBottom && !_suppressExtentAutoScroll)
            _autoFollow = true;

        // 仅当本来就在底部附近时，才因 extent 增高贴底（避免浏览历史时被 lazy 布局拽到底）。
        if (e.ExtentDelta.Y > 0.5 && _autoFollow && !_suppressExtentAutoScroll
            && offsetY + viewport >= extent - e.ExtentDelta.Y - NearBottomThresholdPx)
        {
            ScheduleScrollToEnd();
        }
    }

    /// <summary>
    /// 用户手动展开/折叠工具或思考卡片：在布局变化前记录 header 视口 Y，布局后补偿 scroll，使标题行像素位置不变。
    /// 须在 <see cref="Expander.Expanding"/> / <see cref="Expander.Collapsing"/> 调用（<c>Expanded</c> 太晚）。
    /// </summary>
    internal void BeginExpanderScrollAnchor(Expander expander)
    {
        _autoFollow = false;
        _suppressExtentAutoScroll = true;

        var anchor = ResolveExpanderHeader(expander) ?? expander;
        var anchorViewportY = anchor.TranslatePoint(default, ChatScroll)?.Y;
        if (anchorViewportY is null)
        {
            _suppressExtentAutoScroll = false;
            return;
        }

        void ApplyAnchor()
        {
            var yNow = anchor.TranslatePoint(default, ChatScroll)?.Y;
            if (yNow is null)
                return;

            var deltaViewport = yNow.Value - anchorViewportY.Value;
            if (Math.Abs(deltaViewport) > 0.5)
            {
                var next = Math.Max(0, ChatScroll.Offset.Y - deltaViewport);
                ChatScroll.Offset = ChatScroll.Offset.WithY(next);
            }
        }

        // CrossFade / 内容区 IsVisible 可能分多帧完成布局。
        Dispatcher.UIThread.Post(() =>
        {
            ApplyAnchor();
            Dispatcher.UIThread.Post(() =>
            {
                ApplyAnchor();
                _suppressExtentAutoScroll = false;
            }, DispatcherPriority.Render);
        }, DispatcherPriority.Loaded);
    }

    private static Control? ResolveExpanderHeader(Expander expander)
    {
        if (expander.FindControl<ToggleButton>("ExpanderHeader") is { } header)
            return header;

        foreach (var visual in expander.GetVisualDescendants())
        {
            if (visual is ToggleButton { Name: "ExpanderHeader" } tb)
                return tb;
        }

        return null;
    }

    private void ScheduleExpandSurfaceRounds()
    {
        if (_surfaceExpandScheduled || _vm is null || !_vm.CanExpandSurfaceRounds)
            return;

        _surfaceExpandScheduled = true;
        var extentBefore = ChatScroll.Extent.Height;
        var offsetBefore = ChatScroll.Offset.Y;
        _autoFollow = false;

        Dispatcher.UIThread.Post(() =>
        {
            _surfaceExpandScheduled = false;
            if (_vm is null || !_vm.TryExpandSurfaceRoundsUpward(out _))
                return;

            Dispatcher.UIThread.Post(() =>
            {
                var extentDelta = ChatScroll.Extent.Height - extentBefore;
                if (extentDelta > 0.5)
                    ChatScroll.Offset = ChatScroll.Offset.WithY(offsetBefore + extentDelta);
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Background);
    }

    private void ScheduleScrollToEnd()
    {
        if (_scrollScheduled || _suppressExtentAutoScroll)
            return;

        _scrollScheduled = true;
        // 等布局完成后再滚，避免同一帧多次 extent 变化造成来回跳。
        Dispatcher.UIThread.Post(() =>
        {
            _scrollScheduled = false;
            if (!_autoFollow || _suppressExtentAutoScroll)
                return;

            var maxOffset = Math.Max(0, ChatScroll.Extent.Height - ChatScroll.Viewport.Height);
            var current = ChatScroll.Offset.Y;
            if (Math.Abs(current - maxOffset) < 1)
                return;

            ChatScroll.Offset = ChatScroll.Offset.WithY(maxOffset);
        }, DispatcherPriority.Loaded);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DataContextChanged -= OnDataContextChanged;
        ChatScroll.ScrollChanged -= OnScrollChanged;
        Detach();
    }
}
