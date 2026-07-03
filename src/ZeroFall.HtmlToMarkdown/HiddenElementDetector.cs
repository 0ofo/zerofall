using System.Globalization;

namespace ZeroFall.HtmlToMarkdown;

/// <summary>
/// 检测 HTML 元素是否在视觉上对用户不可见。
/// <para>因为 <c>DOM.getDocument</c> 不返回 boundingBox（要拿真实几何尺寸需为每个节点单独
/// 调 <c>DOM.getBoxModel</c>，对百度首页 200+ 节点来说性能爆炸），这里只能靠 style 属性、
/// HTML5 hidden 属性、aria-hidden 做近似检测。CSS 类里定义的隐藏样式抓不到，但这种一般
/// 会同时设 inline style，覆盖率够用。</para>
/// <para>规则（任一命中即视为隐藏，整棵子树跳过）：</para>
/// <list type="bullet">
///   <item><c>hidden</c> HTML5 属性</item>
///   <item><c>aria-hidden="true"</c></item>
///   <item><c>style="display:none"</c></item>
///   <item><c>style="visibility:hidden"</c></item>
///   <item><c>style="opacity:0"</c>（透明度 0，对用户不可见）</item>
///   <item><c>style="width:0"</c> 或 <c>style="height:0"</c>（任一维度为 0，面积为 0）</item>
/// </list>
/// </summary>
public static class HiddenElementDetector
{
    /// <summary>判断节点是否应视为不可见。</summary>
    public static bool IsHidden(DomNode node)
    {
        if (!node.IsElement) return false;

        // HTML5 hidden 属性
        if (node.HasAttr("hidden")) return true;

        // aria-hidden="true"
        var aria = node.GetAttr("aria-hidden");
        if (aria != null && aria.Equals("true", System.StringComparison.OrdinalIgnoreCase))
            return true;

        var style = node.GetAttr("style");
        if (string.IsNullOrEmpty(style)) return false;

        // 去空格并转小写，便于包含检测
        var s = NormalizeStyle(style);

        if (s.Contains("display:none")) return true;
        if (s.Contains("visibility:hidden")) return true;

        // opacity:0（注意要排除 opacity:0.5 之类，用正则边界）
        if (ContainsZeroOpacity(s)) return true;

        // width:0 或 height:0（任一即跳过，因为面积=0）
        if (IsZeroDimension(s, "width")) return true;
        if (IsZeroDimension(s, "height")) return true;

        return false;
    }

    /// <summary>折叠 style 字符串中的空格并转小写，去掉 " ! important" 之类的空格。</summary>
    private static string NormalizeStyle(string style)
    {
        // 简单去空格（不要用正则，AOT 友好且更快）
        var sb = new System.Text.StringBuilder(style.Length);
        foreach (var c in style)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>检测 opacity:0（精确匹配，排除 opacity:0.5 之类）。</summary>
    private static bool ContainsZeroOpacity(string normalized)
    {
        int idx = normalized.IndexOf("opacity:", System.StringComparison.Ordinal);
        while (idx >= 0)
        {
            int valueStart = idx + "opacity:".Length;
            // 找到值结束位置（下一个 ; 或字符串末尾）
            int valueEnd = normalized.IndexOf(';', valueStart);
            if (valueEnd < 0) valueEnd = normalized.Length;
            var valueStr = normalized.Substring(valueStart, valueEnd - valueStart);
            // 去掉 !important
            int bang = valueStr.IndexOf('!');
            if (bang >= 0) valueStr = valueStr.Substring(0, bang);
            // 解析数值
            if (double.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v == 0D)
                return true;
            idx = normalized.IndexOf("opacity:", valueEnd, System.StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>检测指定属性（width/height）是否为 0。匹配 "width:0" / "width:0px" / "width:0pt" 等。</summary>
    private static bool IsZeroDimension(string normalized, string property)
    {
        string prefix = property + ":";
        int idx = normalized.IndexOf(prefix, System.StringComparison.Ordinal);
        while (idx >= 0)
        {
            int valueStart = idx + prefix.Length;
            // 跳过前导空格已被消除
            int valueEnd = normalized.IndexOf(';', valueStart);
            if (valueEnd < 0) valueEnd = normalized.Length;
            var valueStr = normalized.Substring(valueStart, valueEnd - valueStart);
            int bang = valueStr.IndexOf('!');
            if (bang >= 0) valueStr = valueStr.Substring(0, bang);
            // 解析数值部分（去单位）
            // 取出开头的数字部分
            int numEnd = 0;
            while (numEnd < valueStr.Length && (char.IsDigit(valueStr[numEnd]) || valueStr[numEnd] == '.' || valueStr[numEnd] == '-'))
                numEnd++;
            if (numEnd == 0)
            {
                // 没数字，可能是 "auto"，不算 0
                idx = normalized.IndexOf(prefix, valueEnd, System.StringComparison.Ordinal);
                continue;
            }
            var numStr = valueStr.Substring(0, numEnd);
            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v == 0D)
                return true;
            idx = normalized.IndexOf(prefix, valueEnd, System.StringComparison.Ordinal);
        }
        return false;
    }
}
