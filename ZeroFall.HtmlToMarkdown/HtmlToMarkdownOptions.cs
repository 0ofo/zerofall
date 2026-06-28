namespace ZeroFall.HtmlToMarkdown;

/// <summary>
/// DOM → Markdown 转换器选项。借鉴 OfficeIMO 的 HtmlToMarkdownOptions，但只保留与
/// CDP 节点树场景相关的部分（去掉 AngleSharp 特有的 quote-style/list-style 等细节）。
/// </summary>
public sealed class HtmlToMarkdownOptions
{
    /// <summary>
    /// URL 策略。null 表示不做 URL 过滤（默认 WebOnly）。
    /// </summary>
    public HtmlUrlPolicy? UrlPolicy { get; set; } = HtmlUrlPolicy.WebOnly();

    /// <summary>
    /// 整棵子树跳过的标签名（大小写不敏感）。默认包含 script/style/noscript/template/svg/head 等。
    /// </summary>
    public HashSet<string> SkipTags { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCRIPT", "STYLE", "NOSCRIPT", "TEMPLATE", "SVG", "HEAD", "META", "LINK", "BASE",
        // textarea/select 等表单控件内容对 markdown 输出意义不大
        "TEXTAREA", "SELECT", "OPTION", "OPTGROUP"
    };

    /// <summary>
    /// 在 markdown 中保留为容器（递归子节点，但不输出自身标签）的标签名。
    /// </summary>
    public HashSet<string> ContainerTags { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "HTML", "BODY", "DIV", "SECTION", "ARTICLE", "MAIN", "HEADER", "FOOTER", "NAV",
        "ASIDE", "FIGURE", "FIGCAPTION", "SPAN", "FORM", "FIELDSET"
    };

    /// <summary>
    /// 是否检测隐藏元素并跳过。默认 true。
    /// 检测项：display:none / visibility:hidden / opacity:0 / width:0 / height:0 /
    /// hidden 属性 / aria-hidden="true"。
    /// </summary>
    public bool SkipHiddenElements { get; set; } = true;

    /// <summary>
    /// 输出最大字符数。超过则截断并追加 [已截断] 标记。0 或负数表示不限制。
    /// </summary>
    public int MaxOutputCharacters { get; set; } = 100_000;

    /// <summary>
    /// 表格最大列数（防止巨型表格爆输出）。默认 32。
    /// </summary>
    public int MaxTableColumns { get; set; } = 32;

    /// <summary>
    /// 表格最大行数（同上）。默认 500。
    /// </summary>
    public int MaxTableRows { get; set; } = 500;

    /// <summary>
    /// 是否跳过所有图片（不输出 ![](url)）。默认 false。
    /// </summary>
    public bool SkipImages { get; set; }

    /// <summary>
    /// 默认选项。
    /// </summary>
    public static HtmlToMarkdownOptions Default => new();
}
