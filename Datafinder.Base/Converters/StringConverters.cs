using System;
using Avalonia.Data.Converters;

namespace Datafinder.Base.Converters;

public static class StringConverters
{
    public static readonly IValueConverter IsNotNullOrEmpty = new FuncValueConverter<string?, bool>(
        value => !string.IsNullOrEmpty(value)
    );
}
