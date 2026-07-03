using System;
using System.ComponentModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ZeroFall.AiPanel.ViewModels;

namespace ZeroFall.AiPanel.Views;

public partial class NativeChatPanel : UserControl
{
    private AiPanelViewModel? _vm;
    private bool _stickToEnd = true;
    private bool _suppressStickRecompute;
    private int _scrollScheduled;
    private long _lastScrollUtcTicks;

    public NativeChatPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ChatScroll.ScrollChanged += OnChatScrollChanged;
    }

    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_suppressStickRecompute)
            return;

        var extent = ChatScroll.Extent.Height;
        var viewport = ChatScroll.Viewport.Height;
        var offset = ChatScroll.Offset.Y;
        _stickToEnd = extent <= viewport || offset + viewport >= extent - 48;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = DataContext as AiPanelViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _stickToEnd = true;
            ScheduleScrollToEnd(force: true);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AiPanelViewModel.TranscriptDisplayText)
            or nameof(AiPanelViewModel.IsSending)
            or nameof(AiPanelViewModel.IsWaitingForReply))
        {
            if (e.PropertyName is nameof(AiPanelViewModel.IsSending) && _vm?.IsSending == true)
                _stickToEnd = true;

            var force = e.PropertyName is not nameof(AiPanelViewModel.TranscriptDisplayText);
            ScheduleScrollToEnd(force);
        }
    }

    private void ScheduleScrollToEnd(bool force = false)
    {
        if (!_stickToEnd)
            return;

        if (!force)
        {
            var now = Environment.TickCount64;
            if (now - _lastScrollUtcTicks < 450)
                return;

            _lastScrollUtcTicks = now;
        }

        if (Interlocked.CompareExchange(ref _scrollScheduled, 1, 0) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _scrollScheduled, 0);
            ScrollToEndCore();
        }, DispatcherPriority.Background);
    }

    private void ScrollToEndCore()
    {
        if (!_stickToEnd)
            return;

        var maxOffset = Math.Max(0, ChatScroll.Extent.Height - ChatScroll.Viewport.Height);
        if (maxOffset <= 0)
            return;

        _suppressStickRecompute = true;
        try
        {
            ChatScroll.Offset = new Vector(ChatScroll.Offset.X, maxOffset);
        }
        finally
        {
            _suppressStickRecompute = false;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }
}
