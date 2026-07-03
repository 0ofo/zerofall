using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ZeroFall.Platform.Models;

namespace ZeroFall.App.Converters;

public class TreeNodeIconConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return null;

        var nodeType = values[0] is TreeNodeType nt ? nt : TreeNodeType.DataSource;
        var dsType = values[1] is DataSourceType dst ? dst : DataSourceType.Other;

        var key = nodeType switch
        {
            TreeNodeType.Folder => "SemiIconFolder",
            TreeNodeType.Database => "SemiIconFolder",
            TreeNodeType.Table => "SemiIconGridView",
            TreeNodeType.DataSource => dsType switch
            {
                DataSourceType.MySql => "SemiIconServer",
                DataSourceType.Sqlite => "SemiIconServer",
                DataSourceType.Csv => "SemiIconListView",
                DataSourceType.Json => "SemiIconCode",
                DataSourceType.Excel => "SemiIconExcel",
                _ => "SemiIconFile"
            },
            _ => "SemiIconFile"
        };

        return Application.Current?.Resources.TryGetResource(key, null, out var res) == true
            ? res as StreamGeometry : null;
    }
}
