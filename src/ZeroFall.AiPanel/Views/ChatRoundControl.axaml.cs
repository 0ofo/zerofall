using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ZeroFall.AiPanel.Models;
using ZeroFall.AiPanel.ViewModels;

namespace ZeroFall.AiPanel.Views;

public partial class ChatRoundControl : UserControl
{
    private const double UserBubbleAbsoluteMaxWidth = 520;
    private const double RevertButtonReserve = 26; // 22px button + 4px gap
    private const double BubbleHorizontalPadding = 12; // Padding 6,4

    public static readonly StyledProperty<AiPanelViewModel?> PanelViewModelProperty =
        AvaloniaProperty.Register<ChatRoundControl, AiPanelViewModel?>(nameof(PanelViewModel));

    private Border? _userBubbleBorder;
    private SelectableTextBlock? _userBubbleText;
    private DockPanel? _userRowPanel;

    public AiPanelViewModel? PanelViewModel
    {
        get => GetValue(PanelViewModelProperty);
        set => SetValue(PanelViewModelProperty, value);
    }

    public ChatRoundControl()
    {
        InitializeComponent();
        _userRowPanel = this.FindControl<DockPanel>("UserRowPanel");
        _userBubbleBorder = this.FindControl<Border>("UserBubbleBorder");
        _userBubbleText = this.FindControl<SelectableTextBlock>("UserBubbleText");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_userRowPanel is not null)
            _userRowPanel.SizeChanged += OnUserRowSizeChanged;

        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateUserBubbleLayout, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateUserBubbleLayout, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_userRowPanel is not null)
            _userRowPanel.SizeChanged -= OnUserRowSizeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) => UpdateUserBubbleLayout();

    private void OnUserRowSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateUserBubbleLayout();

    private void UpdateUserBubbleLayout()
    {
        if (_userBubbleBorder is null || _userBubbleText is null)
            return;

        var available = _userRowPanel?.Bounds.Width ?? 0;
        if (available <= 0)
            available = Bounds.Width;
        if (available <= 0)
            return;

        var reserve = PanelViewModel?.CanRevertMessages == true ? RevertButtonReserve : 0;
        var bubbleMax = Math.Max(48, Math.Min(UserBubbleAbsoluteMaxWidth, available - reserve));
        var textMax = Math.Max(24, bubbleMax - BubbleHorizontalPadding);

        _userBubbleBorder.MaxWidth = bubbleMax;
        _userBubbleText.MaxWidth = textMax;
    }

    private void OnChatCardExpanderExpanding(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander)
            return;

        this.FindAncestorOfType<AiChatNativeView>()?.BeginExpanderScrollAnchor(expander);
    }

    private void OnChatCardExpanderCollapsing(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander)
            return;

        this.FindAncestorOfType<AiChatNativeView>()?.BeginExpanderScrollAnchor(expander);
    }

    private async void OnRevertUserMessageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatRoundBlock round || round.UserMessage is null)
            return;

        var vm = PanelViewModel;
        if (vm is null || !vm.CanRevertMessages)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
            return;

        if (!await ChatRevertConfirmDialog.ShowAsync(owner))
            return;

        await vm.TryRevertMessagesFromUserAsync(ChatMessageIds.UiId(round.UserMessage));
    }
}
