using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ZeroFall.Base.Converters;

public class EqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        try
        {
            var valueType = value.GetType();
            var convertedParameter = System.Convert.ChangeType(parameter, valueType);
            return value.Equals(convertedParameter);
        }
        catch
        {
            return value.ToString() == parameter.ToString();
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Avalonia.AvaloniaProperty.UnsetValue;
    }
}
