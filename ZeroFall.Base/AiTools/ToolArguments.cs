using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ZeroFall.Base.AiTools;

public class ToolArguments
{
    private readonly string _rawArgumentsJson;
    private readonly Dictionary<string, JsonElement> _properties;

    public ToolArguments(string rawArgumentsJson, Dictionary<string, JsonElement> properties)
    {
        _rawArgumentsJson = rawArgumentsJson;
        _properties = properties;
    }

    /// <summary>构造请求时的原始 JSON 对象文本（不含外层引号），供 MCP 等需完整结构的桥接层使用。</summary>
    public string RawArgumentsJson => _rawArgumentsJson;

    public static ToolArguments FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            // JsonElement 指向 doc 的租用缓冲区；doc Dispose 后访问会抛「Cannot access a disposed object: JsonDocument」
            dict[prop.Name] = prop.Value.Clone();
        }

        return new ToolArguments(json, dict);
    }

    public bool TryGetString(string name, out string value)
    {
        value = "";
        if (!_properties.TryGetValue(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Null) return false;
        if (el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? "";
            return true;
        }

        // 部分模型会把单个 url 包成 ["https://..."]
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    value = item.GetString()!;
                    return true;
                }
            }
        }

        return false;
    }

    public bool TryGetStringList(string name, out List<string> value)
    {
        value = [];
        if (!_properties.TryGetValue(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Null) return false;

        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    value.Add(s.Trim());
            }

            return value.Count > 0;
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            var raw = el.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            if (raw.TrimStart().StartsWith('['))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize(raw, ListStringJsonContext.Default.ListString);
                    if (parsed is { Count: > 0 })
                    {
                        value = parsed;
                        return true;
                    }
                }
                catch
                {
                    // fall through to single URL
                }
            }

            value.Add(raw.Trim());
            return true;
        }

        return false;
    }

    public bool TryGetInt32(string name, out int value)
    {
        value = 0;
        if (!_properties.TryGetValue(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Null) return false;
        if (el.ValueKind == JsonValueKind.Number)
        {
            value = el.GetInt32();
            return true;
        }
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    public bool TryGetInt64(string name, out long value)
    {
        value = 0;
        if (!_properties.TryGetValue(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Null) return false;
        if (el.ValueKind == JsonValueKind.Number)
        {
            value = el.GetInt64();
            return true;
        }
        return false;
    }

    public bool TryGetDouble(string name, out double value)
    {
        value = 0;
        if (!_properties.TryGetValue(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Null) return false;
        if (el.ValueKind == JsonValueKind.Number)
        {
            value = el.GetDouble();
            return true;
        }
        return false;
    }

    public bool TryGetSingle(string name, out float value)
    {
        value = 0;
        if (!_properties.TryGetValue(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Null) return false;
        if (el.ValueKind == JsonValueKind.Number)
        {
            value = el.GetSingle();
            return true;
        }
        return false;
    }

    public bool TryGetDecimal(string name, out decimal value)
    {
        value = 0;
        if (!_properties.TryGetValue(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Null) return false;
        if (el.ValueKind == JsonValueKind.Number)
        {
            value = el.GetDecimal();
            return true;
        }
        return false;
    }

    public bool TryGetBoolean(string name, out bool value)
    {
        value = false;
        if (!_properties.TryGetValue(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Null) return false;
        if (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
        {
            value = el.GetBoolean();
            return true;
        }
        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }
}
