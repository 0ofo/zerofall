using System;
using System.Text;
using System.Text.RegularExpressions;
using ZeroFall.HtmlToMarkdown;

namespace ZeroFall.Browser.Services;

/// <summary>
/// <c>fetch</c> 响应 HTML 的快速 Markdown/文本转换，避免为每条出站请求再开隐藏浏览器标签。
/// </summary>
internal static partial class FetchHtmlMarkdownFallback
{
    [GeneratedRegex(@"<script\b[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<style\b[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleRegex();

    [GeneratedRegex(@"<noscript\b[^>]*>[\s\S]*?</noscript>", RegexOptions.IgnoreCase)]
    private static partial Regex NoScriptRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex(@"</(p|div|h[1-6]|li|tr|table|section|article|header|footer|blockquote|pre)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockCloseRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"<img\b[^>]*\bsrc\s*=\s*([""'])(?<u>.*?)\1", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcRegex();

    public static string ToMarkdown(string html, bool withImages)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = html;
        text = ScriptRegex().Replace(text, string.Empty);
        text = StyleRegex().Replace(text, string.Empty);
        text = NoScriptRegex().Replace(text, string.Empty);

        if (withImages)
        {
            text = ImgSrcRegex().Replace(text, m =>
            {
                var url = m.Groups["u"].Value;
                return string.IsNullOrWhiteSpace(url) ? string.Empty : $"\n![image]({url})\n";
            });
        }

        text = BrRegex().Replace(text, "\n");
        text = BlockCloseRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = TextUtilities.UnescapeHtmlEntities(text);
        text = TextUtilities.CollapseWhitespace(text);
        text = text.Replace(" \n", "\n", StringComparison.Ordinal).Replace("\n ", "\n", StringComparison.Ordinal);

        while (text.Contains("\n\n\n", StringComparison.Ordinal))
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);

        return text.Trim();
    }
}
