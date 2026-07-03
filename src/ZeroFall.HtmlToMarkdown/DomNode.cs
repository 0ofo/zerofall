using System.Collections.Generic;
using System.Text.Json;

namespace ZeroFall.HtmlToMarkdown;

/// <summary>
/// CDP <c>DOM.getDocument</c> 返回节点的只读适配器。
/// 避免直接对 <see cref="JsonElement"/> 反复 <c>TryGetProperty</c> 的样板代码，
/// 同时为转换器逻辑提供稳定的访问面（即使 CDP 字段名变更，只改这里一处）。
/// <para>CDP 节点结构：</para>
/// <list type="bullet">
///   <item><c>nodeName</c>：元素标签名（大写）/ "#text" / "#comment" / "#document" / "#documentType"</item>
///   <item><c>nodeValue</c>：文本/注释节点的值</item>
///   <item><c>attributes</c>：扁平数组 [name1, value1, name2, value2, ...]</item>
///   <item><c>children</c>：子节点数组</item>
///   <item><c>contentDocument</c>：iframe/shadow DOM 穿透后的内容（pierce=true 时）</item>
/// </list>
/// </summary>
public readonly struct DomNode
{
    private readonly JsonElement _element;

    public DomNode(JsonElement element)
    {
        _element = element;
    }

    /// <summary>原始 nodeName（保持 CDP 返回的原样，可能是 "DIV" 或 "svg"）。null 表示缺失。</summary>
    public string? Name
    {
        get
        {
            if (_element.TryGetProperty("nodeName", out var n))
                return n.GetString();
            return null;
        }
    }

    /// <summary>大写 nodeName，用于比较。null/empty 返回空字符串。</summary>
    public string NameUpper => (Name ?? string.Empty).ToUpperInvariant();

    /// <summary>是否为元素节点（非 #text/#comment/#document 等）。</summary>
    public bool IsElement
    {
        get
        {
            var n = Name;
            return !string.IsNullOrEmpty(n) && n[0] != '#';
        }
    }

    /// <summary>是否为文本节点（nodeName="#text"）。</summary>
    public bool IsText => string.Equals(Name, "#text", System.StringComparison.Ordinal);

    /// <summary>是否为注释节点。</summary>
    public bool IsComment => string.Equals(Name, "#comment", System.StringComparison.Ordinal);

    /// <summary>是否为文档根节点。</summary>
    public bool IsDocument => string.Equals(Name, "#document", System.StringComparison.Ordinal);

    /// <summary>是否为 doctype。</summary>
    public bool IsDocumentType => string.Equals(Name, "#documentType", System.StringComparison.Ordinal);

    /// <summary>文本节点的值（其他节点为 null）。</summary>
    public string? TextValue
    {
        get
        {
            if (_element.TryGetProperty("nodeValue", out var v))
                return v.GetString();
            return null;
        }
    }

    /// <summary>取属性值。不存在返回 null。</summary>
    public string? GetAttr(string name)
    {
        if (!_element.TryGetProperty("attributes", out var attrsEl) || attrsEl.ValueKind != JsonValueKind.Array)
            return null;
        var arr = attrsEl.EnumerateArray();
        using var en = arr.GetEnumerator();
        while (en.MoveNext())
        {
            var k = en.Current.GetString() ?? "";
            if (!en.MoveNext()) break;
            var v = en.Current.GetString() ?? "";
            if (string.Equals(k, name, System.StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return null;
    }

    /// <summary>是否存在某属性（不需要取值）。</summary>
    public bool HasAttr(string name)
    {
        if (!_element.TryGetProperty("attributes", out var attrsEl) || attrsEl.ValueKind != JsonValueKind.Array)
            return false;
        var arr = attrsEl.EnumerateArray();
        using var en = arr.GetEnumerator();
        while (en.MoveNext())
        {
            var k = en.Current.GetString() ?? "";
            if (string.Equals(k, name, System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (!en.MoveNext()) break;
        }
        return false;
    }

    /// <summary>枚举子节点。注意：CDP 在 pierce=true 时会把 iframe/shadow 内容放到
    /// <c>contentDocument</c> 字段，本方法也会一并枚举，方便调用方统一遍历。</summary>
    public IEnumerable<DomNode> Children
    {
        get
        {
            if (_element.TryGetProperty("children", out var childrenEl) && childrenEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in childrenEl.EnumerateArray())
                    yield return new DomNode(c);
            }
            if (_element.TryGetProperty("contentDocument", out var contentEl) && contentEl.ValueKind == JsonValueKind.Object)
            {
                yield return new DomNode(contentEl);
            }
        }
    }

    /// <summary>原始 JsonElement，供特殊情况直接访问。</summary>
    public JsonElement Raw => _element;
}
