using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;

namespace ZeroFall.AiPanel.Views;

/// <summary>
/// 内容级懒加载 Markdown 宿主：接近视口时创建 <see cref="MarkdownRenderer"/>，
/// 离开较远视口后释放并用实测高度占位，避免长会话控件树过大。
/// </summary>
public sealed class LazyMarkdownPresenter : Decorator
{
    /// <summary>进入视口前预取距离（像素）。</summary>
    private const double ActivateBufferPx = 280;

    /// <summary>离开视口后仍保留渲染器，直到完全超出此距离再释放。</summary>
    private const double DeactivateBufferPx = 520;

    public static Action<Uri>? OpenLink { get; set; }

    public static readonly StyledProperty<ObservableStringBuilder?> MarkdownBuilderProperty =
        AvaloniaProperty.Register<LazyMarkdownPresenter, ObservableStringBuilder?>(nameof(MarkdownBuilder));

    public ObservableStringBuilder? MarkdownBuilder
    {
        get => GetValue(MarkdownBuilderProperty);
        set => SetValue(MarkdownBuilderProperty, value);
    }

    private MarkdownRenderer? _renderer;
    private bool _active;
    private double _cachedHeight;

    public LazyMarkdownPresenter()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        SizeChanged += OnSizeChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        Deactivate();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != MarkdownBuilderProperty)
            return;

        _cachedHeight = 0;
        if (_active)
        {
            Deactivate();
            TryActivate();
        }
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var viewport = e.EffectiveViewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
            return;

        var self = new Rect(Bounds.Size);
        if (viewport.Inflate(ActivateBufferPx).Intersects(self))
            TryActivate();
        else if (!viewport.Inflate(DeactivateBufferPx).Intersects(self))
            Deactivate();
    }

    private void TryActivate()
    {
        if (_active || MarkdownBuilder is null)
            return;

        _active = true;
        // 先用上次高度占位，避免 0 → 实际高度 导致列表 extent 突变。
        if (_cachedHeight > 0)
            MinHeight = _cachedHeight;

        _renderer = new MarkdownRenderer
        {
            MarkdownBuilder = MarkdownBuilder,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        _renderer.LinkClick += OnLinkClick;
        Child = _renderer;
    }

    private void Deactivate()
    {
        if (!_active)
            return;

        if (Bounds.Height > 0)
            _cachedHeight = Bounds.Height;

        _active = false;
        if (_renderer is not null)
        {
            _renderer.LinkClick -= OnLinkClick;
            _renderer.MarkdownBuilder = null;
            _renderer = null;
        }

        Child = null;
        MinHeight = _cachedHeight;
    }

    /// <summary>
    /// 控件位于视口上方且高度变化时，补偿 ScrollViewer 偏移，避免浏览历史时画面跳动。
    /// </summary>
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var delta = e.NewSize.Height - e.PreviousSize.Height;
        if (Math.Abs(delta) < 0.5)
            return;

        if (_active && e.NewSize.Height > 0)
            _cachedHeight = e.NewSize.Height;

        if (this.FindAncestorOfType<ScrollViewer>() is not { } scroll)
            return;

        var top = this.TranslatePoint(default, scroll)?.Y;
        if (top is null)
            return;

        // 整个控件在可视区域之上：extent 变化不应推动当前视口内容。
        if (top.Value + e.PreviousSize.Height <= scroll.Offset.Y + 0.5)
            scroll.Offset = scroll.Offset.WithY(Math.Max(0, scroll.Offset.Y + delta));
    }

    private void OnLinkClick(object? sender, LinkClickedEventArgs e)
    {
        if (e.HRef is { } uri && uri.IsAbsoluteUri)
            OpenLink?.Invoke(uri);
    }
}
