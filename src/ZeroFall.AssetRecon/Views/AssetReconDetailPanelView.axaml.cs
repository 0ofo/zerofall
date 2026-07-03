using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ZeroFall.AssetRecon.ViewModels;

namespace ZeroFall.AssetRecon.Views;

public partial class AssetReconDetailPanelView : UserControl
{
    private const double ScrollPaddingHorizontal = 8;
    private const double ScrollPaddingVertical = 12;
    /// <summary>与 XAML 中 10px 字号 + DescriptionsItem 行距一致。</summary>
    private const double RowHeight = 15;
    private const double LabelColumnWidth = 76;
    private const double CharWidthEstimate = 5.5;

    public AssetReconDetailPanelView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 估算 Flyout 尺寸。禁止对已挂树的 <see cref="DetailDescriptions"/> 调用 <see cref="Layoutable.Measure"/>，
    /// 否则会触发 Ursa Items 重挂载并抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public Size EstimateFlyoutContentSize(double maxWidth, double maxHeight, int itemCount)
    {
        maxWidth = AssetReconFlyoutLayout.Sanitize(maxWidth, 1, AssetReconDetailFlyoutChrome.MaxWidth);
        maxHeight = AssetReconFlyoutLayout.Sanitize(maxHeight, 1, AssetReconDetailFlyoutChrome.MaxHeight);

        if (itemCount <= 0)
            return new Size(Math.Min(220, maxWidth), Math.Min(80, maxHeight));

        var maxChars = 0;
        if (DataContext is AssetReconLeftPanelViewModel vm)
        {
            foreach (var p in vm.DetailProperties)
                maxChars = Math.Max(maxChars, p.Value.Length);
        }

        var contentMaxW = Math.Max(1, maxWidth - ScrollPaddingHorizontal);
        var valueWidth = Math.Min(maxChars * CharWidthEstimate, contentMaxW - LabelColumnWidth);
        var w = LabelColumnWidth + Math.Max(valueWidth, 48) + ScrollPaddingHorizontal;
        var h = itemCount * RowHeight + ScrollPaddingVertical;

        return new Size(
            AssetReconFlyoutLayout.Sanitize(w, 1, maxWidth),
            AssetReconFlyoutLayout.Sanitize(h, 1, maxHeight));
    }

    /// <summary>Flyout 展示并完成布局后，用实际边界修正高度（仍不 Measure Descriptions）。</summary>
    public Size? TryReadLayoutContentSize(double maxWidth, double maxHeight)
    {
        if (DetailScroll is null || !DetailScroll.IsAttachedToVisualTree())
            return null;

        DetailScroll.UpdateLayout();
        var extent = DetailScroll.Extent;
        var bounds = Bounds;
        if (extent.Height <= 0 && bounds.Height <= 0)
            return null;

        var w = AssetReconFlyoutLayout.Sanitize(
            Math.Max(bounds.Width, extent.Width + ScrollPaddingHorizontal),
            1,
            maxWidth);
        var h = AssetReconFlyoutLayout.Sanitize(
            Math.Max(bounds.Height, extent.Height + ScrollPaddingVertical),
            1,
            maxHeight);
        return new Size(w, h);
    }
}

internal static class AssetReconFlyoutLayout
{
    internal static double Sanitize(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return min;
        return Math.Clamp(value, min, max);
    }
}
