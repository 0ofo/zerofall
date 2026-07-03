using System.Text.Json;

namespace ZeroFall.Browser.Serialization;

internal static class BrowserJson
{
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, typeof(T), BrowserJsonContext.Default);

    public static string SerializeString(string value) =>
        JsonSerializer.Serialize(value, BrowserJsonContext.Default.String);
}
