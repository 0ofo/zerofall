using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZeroFall.Base.AiTools;

/// <summary>AI 工具统一 JSON 结果格式（供 LLM 与 WebView 表格渲染）。AOT 下使用 JsonObject/JsonArray，禁止反射序列化。</summary>
public static class ToolResultJson
{
    private static readonly JsonSerializerOptions WriteOptions = new(ToolResultJsonContext.Default.Options)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Error(string message, Action<JsonObject>? configure = null)
    {
        var obj = new JsonObject { ["ok"] = false, ["error"] = message };
        configure?.Invoke(obj);
        return obj.ToJsonString(WriteOptions);
    }

    public static string Ok(string? message = null)
    {
        if (string.IsNullOrEmpty(message))
            return "{}";

        return new JsonObject { ["message"] = message }.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// 网站树 JSON（见 doc/website-tree.md）：与左侧树同一套展示规则；重复 path 在 JSON 中仅输出叶子字符串。
    /// </summary>
    public static string WebsiteTree(string site, JsonArray children) =>
        new JsonObject { ["site"] = site, ["child"] = children }.ToJsonString(WriteOptions);

    public static string Data(JsonNode node) => node.ToJsonString(WriteOptions);

    public static string Data(Action<JsonObject> configure)
    {
        var obj = new JsonObject();
        configure(obj);
        return obj.ToJsonString(WriteOptions);
    }

    public static string DataArray(Action<JsonArray> configure)
    {
        var arr = new JsonArray();
        configure(arr);
        return arr.ToJsonString(WriteOptions);
    }

    public static string EmptyArray() => "[]";

    /// <summary>
    /// SQL 查询行集：0 行 → 单列 <c>{ 列: [] }</c> 或多列 <c>{ cols, rows: [] }</c>；
    /// 1 行 → <c>{ 列: 值 }</c>；单列多行 → <c>{ 列: [值, ...] }</c>；多列多行 → <c>{ cols, rows }</c> 矩阵。
    /// </summary>
    public static string QueryRows(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var rowList = rows as IReadOnlyList<IReadOnlyList<object?>> ?? rows.ToList();

        if (rowList.Count == 0)
        {
            if (columns.Count == 1)
            {
                return Data(o => o[columns[0]] = new JsonArray());
            }

            return Data(o =>
            {
                o["cols"] = BuildColumnArray(columns);
                o["rows"] = new JsonArray();
            });
        }

        if (rowList.Count == 1)
        {
            var obj = new JsonObject();
            var row = rowList[0];
            for (var i = 0; i < columns.Count; i++)
                obj[columns[i]] = CellToJsonNode(i < row.Count ? row[i] : null);
            return obj.ToJsonString(WriteOptions);
        }

        if (columns.Count == 1)
        {
            var values = new JsonArray();
            foreach (var row in rowList)
                values.Add(CellToJsonNode(row.Count > 0 ? row[0] : null));

            return Data(o => o[columns[0]] = values);
        }

        return Data(o =>
        {
            o["cols"] = BuildColumnArray(columns);
            var rowsArr = new JsonArray();
            foreach (var row in rowList)
            {
                var arr = new JsonArray();
                for (var i = 0; i < columns.Count; i++)
                    arr.Add(CellToJsonNode(i < row.Count ? row[i] : null));
                rowsArr.Add(arr);
            }

            o["rows"] = rowsArr;
        });
    }

    private static JsonArray BuildColumnArray(IReadOnlyList<string> columns)
    {
        var cols = new JsonArray();
        foreach (var column in columns)
            cols.Add(JsonValue.Create(column));
        return cols;
    }

    private static JsonNode? CellToJsonNode(object? value)
    {
        if (value is null)
            return null;

        return value switch
        {
            bool b => JsonValue.Create(b),
            byte or sbyte or short or ushort or int or uint or long or ulong
                => JsonValue.Create(Convert.ToInt64(value)),
            float or double or decimal => JsonValue.Create(Convert.ToDouble(value)),
            string s => JsonValue.Create(s),
            DateTime dt => JsonValue.Create(dt.ToString("O")),
            DateTimeOffset dto => JsonValue.Create(dto.ToString("O")),
            byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
            _ => JsonValue.Create(value.ToString())
        };
    }

    /// <summary>将工具结果文本解析为 JsonNode（供 WebView 嵌入，避免字符串二次转义）。非 JSON 时返回字符串节点。</summary>
    public static JsonNode? ParseToolPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return JsonValue.Create(string.Empty);

        try
        {
            var node = JsonNode.Parse(text);
            return UnwrapJsonStringNode(node) ?? node;
        }
        catch
        {
            return JsonValue.Create(text);
        }
    }

    /// <summary>工具 output 落盘：已是 JSON 则嵌套对象，否则保留字符串。</summary>
    public static JsonNode? ToPersistedOutput(string? text) => ParseToolPayload(text);

    /// <summary>从落盘 JsonElement 还原为发给 LLM / UI 的文本。</summary>
    public static string FromPersistedOutput(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => element.GetRawText()
        };

    /// <summary>从落盘 JsonNode（ChatToolPayloadDto.Output）还原工具结果文本；禁止裸 <c>ToJsonString()</c>（会 \u 转义 CJK）。</summary>
    public static string FromPersistedNode(JsonNode? node)
    {
        if (node is null)
            return string.Empty;

        if (node is JsonValue val && val.TryGetValue<string>(out var s))
            return s;

        return node.ToJsonString(WriteOptions);
    }

    /// <summary>原生 UI / 复制：解析 JSON 并 pretty-print（CJK 不转义）。</summary>
    public static string FormatForDisplay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var node = ParseToolPayload(text);
        if (node is null)
            return text;

        var options = new JsonSerializerOptions(WriteOptions) { WriteIndented = true };
        return node.ToJsonString(options);
    }

    /// <summary>将工具原始输出规范为 JSON（已是 object/array 则原样返回；JSON 字符串则解包一层）。</summary>
    public static string Normalize(string? text, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Error(exitCode == 0 ? "工具未返回任何内容" : "空结果");

        if (text.Trim() == "{}" && exitCode == 0)
            return Error("工具返回空对象 {}");

        var trimmed = text.Trim();
        if (TryGetJsonRootKind(trimmed, out var kind))
        {
            if (kind is JsonValueKind.Object or JsonValueKind.Array)
                return trimmed;

            if (kind == JsonValueKind.String
                && TryUnwrapJsonString(trimmed, out var inner))
                return inner;
        }

        if (exitCode != 0)
            return Error(trimmed);

        return trimmed;
    }

    public static bool IsErrorJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (doc.RootElement.TryGetProperty("ok", out var okEl)
                && okEl.ValueKind == JsonValueKind.False)
                return true;

            if (doc.RootElement.TryGetProperty("error", out var errEl)
                && errEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(errEl.GetString()))
                return true;

            return false;
        }
        catch
        {
            return LooksLikePlainTextError(text);
        }
    }

    private static bool LooksLikePlainTextError(string text)
    {
        ReadOnlySpan<string> prefixes =
        [
            "错误", "失败", "拒绝", "不支持", "不存在", "不能为空", "未知工具",
            "MCP 工具调用失败", "解析参数失败", "工具执行失败", "用户取消了", "查询错误", "执行失败", "写入失败", "移动失败"
        ];

        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryGetJsonRootKind(string text, out JsonValueKind kind)
    {
        kind = default;
        try
        {
            using var doc = JsonDocument.Parse(text);
            kind = doc.RootElement.ValueKind;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryUnwrapJsonString(string jsonText, out string innerJson)
    {
        innerJson = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind != JsonValueKind.String)
                return false;

            var inner = doc.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(inner))
                return false;

            var trimmed = inner.Trim();
            if (!TryGetJsonRootKind(trimmed, out var kind)
                || kind is not (JsonValueKind.Object or JsonValueKind.Array))
                return false;

            innerJson = trimmed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static JsonNode? UnwrapJsonStringNode(JsonNode? node, int depth = 0)
    {
        if (node is null || depth > 2)
            return node;

        if (node is not JsonValue val || !val.TryGetValue<string>(out var s))
            return node;

        var trimmed = s.Trim();
        if (!LooksLikeJsonObjectOrArray(trimmed))
            return node;

        try
        {
            var inner = JsonNode.Parse(trimmed);
            return UnwrapJsonStringNode(inner, depth + 1) ?? inner;
        }
        catch
        {
            return node;
        }
    }

    private static bool LooksLikeJsonObjectOrArray(string s) =>
        s.Length >= 2
        && ((s[0] == '{' && s[^1] == '}') || (s[0] == '[' && s[^1] == ']'));
}
