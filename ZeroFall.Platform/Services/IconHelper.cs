using Avalonia;
using Avalonia.Media;
using ZeroFall.Platform.Models;

namespace ZeroFall.Platform.Services;

public static class IconHelper
{
    public static StreamGeometry? GetIcon(string key)
    {
        return Application.Current?.Resources.TryGetResource(key, null, out var res) == true
            ? res as StreamGeometry : null;
    }

    public static StreamGeometry? GetIconForDataSourceType(DataSourceType type)
    {
        var key = type switch
        {
            DataSourceType.MySql => "SemiIconServer",
            DataSourceType.Sqlite => "SemiIconServer",
            DataSourceType.Csv => "SemiIconListView",
            DataSourceType.Json => "SemiIconCode",
            DataSourceType.Excel => "SemiIconExcel",
            _ => "SemiIconFile"
        };
        return GetIcon(key);
    }
}
