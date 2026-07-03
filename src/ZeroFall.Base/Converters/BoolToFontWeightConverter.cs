using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ZeroFall.Base.Converters;

public class BoolToFontWeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isBold && isBold)
        {
            return FontWeight.Bold;
        }
        return FontWeight.Normal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Avalonia.AvaloniaProperty.UnsetValue;
    }
}
