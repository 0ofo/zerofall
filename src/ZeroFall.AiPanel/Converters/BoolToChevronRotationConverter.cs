using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ZeroFall.AiPanel.Converters;

/// <summary>折叠 false→0°，展开 true→90°（SemiIconChevronRight 朝下）。</summary>
public sealed class BoolToChevronRotationConverter : IValueConverter
{
    public static readonly BoolToChevronRotationConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 90d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double angle && angle >= 45d;
}
