namespace ZeroFall.HtmlToMarkdown;

/// <summary>
/// 决定 URL 是否允许写入 Markdown 输出。借鉴 OfficeIMO.Html.Policies.HtmlUrlPolicy 的设计，
/// 但不依赖 AngleSharp，直接对字符串工作，便于 AOT。
/// </summary>
public sealed class HtmlUrlPolicy
{
    /// <summary>
    /// 允许的 URL Scheme（小写，无 ":"）。空集合表示不限制 Scheme。
    /// 默认 http/https/mailto/tel/ftp。
    /// </summary>
    public HashSet<string> AllowedSchemes { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto", "tel", "ftp"
    };

    /// <summary>
    /// 是否拒绝 data: URL（base64 内嵌资源，体积大且对 markdown 无意义）。
    /// 默认 true。
    /// </summary>
    public bool DisallowDataUrls { get; set; } = true;

    /// <summary>
    /// 是否拒绝 javascript: URL（XSS 风险）。
    /// 默认 true。
    /// </summary>
    public bool DisallowJavascriptUrls { get; set; } = true;

    /// <summary>
    /// 是否拒绝纯锚点 URL（#xxx，对页面外 markdown 无意义）。
    /// 默认 true。
    /// </summary>
    public bool DisallowFragmentOnlyUrls { get; set; } = true;

    /// <summary>
    /// 评估 URL，返回是否允许保留。
    /// </summary>
    public bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();

        // 锚点
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
            return !DisallowFragmentOnlyUrls;

        // 提取 scheme
        int colon = trimmed.IndexOf(':');
        if (colon <= 0)
        {
            // 相对路径，没 scheme，允许
            return true;
        }

        // scheme 前面必须是合法字符（防止 "javascript:" 被前导空格绕过之类）
        // scheme 必须是字母数字+.-+，且第一个字符是字母
        bool validScheme = true;
        for (int i = 0; i < colon; i++)
        {
            char c = trimmed[i];
            if (i == 0)
            {
                if (!char.IsLetter(c)) { validScheme = false; break; }
            }
            else
            {
                if (!char.IsLetterOrDigit(c) && c != '+' && c != '-' && c != '.') { validScheme = false; break; }
            }
        }
        if (!validScheme)
        {
            // 不是合法 scheme，按相对路径处理，允许
            return true;
        }

        var scheme = trimmed.Substring(0, colon);

        if (scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
            return !DisallowDataUrls;
        if (scheme.Equals("javascript", StringComparison.OrdinalIgnoreCase))
            return !DisallowJavascriptUrls;

        if (AllowedSchemes.Count == 0) return true;
        return AllowedSchemes.Contains(scheme);
    }

    /// <summary>
    /// 仅允许 Web URL 的策略（http/https/mailto/tel/ftp，禁 data/javascript/锚点）。
    /// </summary>
    public static HtmlUrlPolicy WebOnly() => new();
}
