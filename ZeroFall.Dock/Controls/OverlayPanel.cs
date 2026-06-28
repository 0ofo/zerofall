using Avalonia;
using Avalonia.Controls;

namespace ZeroFall.Dock.Controls;

public sealed class OverlayPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;
            child.Measure(availableSize);
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(finalSize);
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;
            child.Arrange(rect);
        }

        return finalSize;
    }
}
