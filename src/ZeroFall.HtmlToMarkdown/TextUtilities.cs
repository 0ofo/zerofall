using System.Text;

namespace ZeroFall.HtmlToMarkdown;

/// <summary>
/// 文本处理工具集合。借鉴 OfficeIMO 的 NormalizeInlineSequence / CollapseWhitespace /
/// EscapeInlineText 等理念，但简化为针对 CDP 文本节点（已解码的字符串）工作。
/// </summary>
public static class TextUtilities
{
    /// <summary>
    /// 折叠文本中的空白：换行/制表符转空格，连续空格合并为一个。
    /// 用于非 pre 上下文的文本节点（HTML 渲染规则）。
    /// </summary>
    public static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        bool prevSpace = false;
        foreach (var c in text)
        {
            if (c == '\n' || c == '\r' || c == '\t' || c == ' ')
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 反转义 HTML 实体。CDP <c>DOM.getDocument</c> 返回的 <c>nodeValue</c> 通常已解码，
    /// 但有些情况下（如外层 attribute）会保留实体；这里做兜底反转义。
    /// 支持：&amp; &lt; &gt; &quot; &apos; &nbsp; &#NN; &#xNN;
    /// </summary>
    public static string UnescapeHtmlEntities(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('&') < 0) return text;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '&')
            {
                sb.Append(text[i]);
                i++;
                continue;
            }
            int semi = text.IndexOf(';', i + 1, 32);
            if (semi < 0 || semi - i > 12)
            {
                sb.Append('&');
                i++;
                continue;
            }
            var entity = text.Substring(i + 1, semi - i - 1);
            if (TryDecodeEntity(entity, out var ch))
            {
                sb.Append(ch);
                i = semi + 1;
            }
            else
            {
                sb.Append('&');
                i++;
            }
        }
        return sb.ToString();
    }

    private static bool TryDecodeEntity(string entity, out string ch)
    {
        ch = "";
        if (entity.Length == 0) return false;
        if (entity[0] == '#')
        {
            // 数字实体
            int code;
            if (entity.Length > 1 && (entity[1] == 'x' || entity[1] == 'X'))
            {
                if (!int.TryParse(entity.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code))
                    return false;
            }
            else
            {
                if (!int.TryParse(entity.AsSpan(1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out code))
                    return false;
            }
            ch = char.ConvertFromUtf32(code);
            return true;
        }
        switch (entity)
        {
            case "amp": ch = "&"; return true;
            case "lt": ch = "<"; return true;
            case "gt": ch = ">"; return true;
            case "quot": ch = "\""; return true;
            case "apos": ch = "'"; return true;
            case "nbsp": ch = " "; return true;
            case "copy": ch = "\u00A9"; return true;
            case "reg": ch = "\u00AE"; return true;
            case "trade": ch = "\u2122"; return true;
            case "mdash": ch = "\u2014"; return true;
            case "ndash": ch = "\u2013"; return true;
            case "hellip": ch = "\u2026"; return true;
            case "ldquo": ch = "\u201C"; return true;
            case "rdquo": ch = "\u201D"; return true;
            case "lsquo": ch = "\u2018"; return true;
            case "rsquo": ch = "\u2019"; return true;
            default: return false;
        }
    }

    /// <summary>
    /// 对 markdown 特殊字符做转义，使其在 markdown 输出中按字面显示。
    /// 转义：\ ` * _ { } [ ] ( ) # + - . ! | ~ &gt;
    /// </summary>
    public static string EscapeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                case '`':
                case '*':
                case '_':
                case '{':
                case '}':
                case '[':
                case ']':
                case '(':
                case ')':
                case '#':
                case '+':
                case '-':
                case '.':
                case '!':
                case '|':
                case '~':
                case '>':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 转义 markdown 链接目标 URL（用于 [text](url) 的 url 部分）。
    /// 只转义 ) 和反斜杠，避免破坏 URL 编码。
    /// </summary>
    public static string EscapeLinkTarget(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var sb = new StringBuilder(url.Length);
        foreach (var c in url)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case ')': sb.Append("%29"); break;
                case ' ': sb.Append("%20"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
