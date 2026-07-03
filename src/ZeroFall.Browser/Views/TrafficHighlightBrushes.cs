using Avalonia.Media;
using ZeroFall.Browser.ViewModels;

namespace ZeroFall.Browser.Views;

internal static class TrafficHighlightBrushes
{
    public static readonly TrafficHighlightColor[] SelectableColors =
    [
        TrafficHighlightColor.Red,
        TrafficHighlightColor.Orange,
        TrafficHighlightColor.Yellow,
        TrafficHighlightColor.Green,
        TrafficHighlightColor.Cyan,
        TrafficHighlightColor.Blue,
        TrafficHighlightColor.Pink,
        TrafficHighlightColor.Gray
    ];

    public static string GetDisplayName(TrafficHighlightColor color) => color switch
    {
        TrafficHighlightColor.Red => "红色",
        TrafficHighlightColor.Orange => "橙色",
        TrafficHighlightColor.Yellow => "黄色",
        TrafficHighlightColor.Green => "绿色",
        TrafficHighlightColor.Cyan => "青色",
        TrafficHighlightColor.Blue => "蓝色",
        TrafficHighlightColor.Pink => "粉色",
        TrafficHighlightColor.Gray => "灰色",
        _ => "无"
    };

    public static IBrush? GetRowBackground(TrafficHighlightColor color) => color switch
    {
        TrafficHighlightColor.Red => Solid("#55FF6666"),
        TrafficHighlightColor.Orange => Solid("#55FFAA66"),
        TrafficHighlightColor.Yellow => Solid("#55FFEE66"),
        TrafficHighlightColor.Green => Solid("#5566DD88"),
        TrafficHighlightColor.Cyan => Solid("#5566DDEE"),
        TrafficHighlightColor.Blue => Solid("#556688FF"),
        TrafficHighlightColor.Pink => Solid("#55FF88CC"),
        TrafficHighlightColor.Gray => Solid("#55999999"),
        _ => null
    };

    public static IBrush GetMenuSwatch(TrafficHighlightColor color) =>
        GetRowBackground(color) ?? Brushes.Transparent;

    private static SolidColorBrush Solid(string hex) => new(Color.Parse(hex));
}
