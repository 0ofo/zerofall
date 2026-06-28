using System.Text;

namespace ZeroFall.HtmlToMarkdown;

/// <summary>
/// 内联元素转换（partial 类的一部分）。
/// 处理 A/IMG/STRONG/EM/CODE/DEL/S/BR/SPAN/INPUT/BUTTON 等内联标签，
/// 以及文本节点的合并与空白折叠。
/// </summary>
public sealed partial class DomToMarkdownConverter
{
    /// <summary>把一组子节点作为内联序列写入当前输出。</summary>
    private void ConvertChildrenToInline(DomNode parent)
    {
        var sb = new StringBuilder();
        CollectInlineText(parent, sb);
        // 折叠多余空白
        var text = sb.ToString();
        _output.Append(text);
    }

    /// <summary>递归收集内联文本到指定 StringBuilder。</summary>
    private void CollectInlineText(DomNode node, StringBuilder sb)
    {
        foreach (var child in node.Children)
        {
            EmitInlineNode(child, sb);
        }
    }

    private void EmitInlineNode(DomNode node, StringBuilder sb)
    {
        // 文本节点
        if (node.IsText)
        {
            var text = node.TextValue ?? "";
            text = TextUtilities.UnescapeHtmlEntities(text);
            var ctx = _contextStack.Peek();
            if (ctx.InPre)
            {
                // PRE 内保留原始文本
                sb.Append(text);
            }
            else
            {
                text = TextUtilities.CollapseWhitespace(text);
                if (!string.IsNullOrEmpty(text)) sb.Append(text);
            }
            return;
        }

        // 注释 / doctype：跳过
        if (node.IsComment || node.IsDocumentType || node.IsDocument) return;
        if (!node.IsElement) return;

        // 整棵跳过的标签
        if (_options.SkipTags.Contains(node.NameUpper)) return;

        // 隐藏元素跳过
        if (_options.SkipHiddenElements && HiddenElementDetector.IsHidden(node)) return;

        switch (node.NameUpper)
        {
            case "A": EmitLinkInline(node, sb); return;
            case "IMG": EmitImageInline(node, sb); return;
            case "BR":
                sb.AppendLine();
                return;
            case "WBR": return; // 单词换行机会，markdown 无对应
            case "STRONG":
            case "B":
                sb.Append("**");
                CollectInlineText(node, sb);
                sb.Append("**");
                return;
            case "EM":
            case "I":
                sb.Append('*');
                CollectInlineText(node, sb);
                sb.Append('*');
                return;
            case "DEL":
            case "S":
                sb.Append("~~");
                CollectInlineText(node, sb);
                sb.Append("~~");
                return;
            case "U":
            case "INS":
                // markdown 无下划线原生语法，<u> 通常只是 inline 强调，输出文本即可
                CollectInlineText(node, sb);
                return;
            case "CODE":
                sb.Append('`');
                // CODE 内不递归 inline 元素（代码内容应为纯文本）
                var codeSb = new StringBuilder();
                CollectRawText(node, codeSb);
                var codeText = codeSb.ToString().Trim();
                // 处理反引号冲突
                if (codeText.Contains('`'))
                {
                    sb.Append(' ').Append(codeText).Append(' ');
                }
                else
                {
                    sb.Append(codeText);
                }
                sb.Append('`');
                return;
            case "MARK":
                // ==高亮==（部分 markdown 方言支持）
                sb.Append("==");
                CollectInlineText(node, sb);
                sb.Append("==");
                return;
            case "SUB":
                CollectInlineText(node, sb);
                sb.Append("₍sub₎");
                return;
            case "SUP":
                CollectInlineText(node, sb);
                sb.Append("₍sup₎");
                return;
            case "KBD":
                sb.Append("<kbd>");
                CollectInlineText(node, sb);
                sb.Append("</kbd>");
                return;
            case "ABBR":
                CollectInlineText(node, sb);
                var title = node.GetAttr("title");
                if (!string.IsNullOrEmpty(title))
                    sb.Append(" (").Append(title).Append(')');
                return;
            case "Q":
                sb.Append('"');
                CollectInlineText(node, sb);
                sb.Append('"');
                return;
            case "CITE":
            case "SMALL":
            case "SPAN":
            case "LABEL":
            case "FONT":
            case "TIME":
                CollectInlineText(node, sb);
                return;
            case "INPUT":
                EmitInputInline(node, sb);
                return;
            case "BUTTON":
                sb.Append('[');
                CollectInlineText(node, sb);
                sb.Append(']');
                return;
            case "SELECT":
                // 输出选中项的文本
                EmitSelectInline(node, sb);
                return;
            case "MATH":
                // 数学公式：输出 alttext 或文本
                var alt = node.GetAttr("alttext");
                if (!string.IsNullOrEmpty(alt)) sb.Append(alt);
                return;
            default:
                // 未知内联元素：递归子节点
                CollectInlineText(node, sb);
                return;
        }
    }

    private void EmitLinkInline(DomNode node, StringBuilder sb)
    {
        var href = node.GetAttr("href") ?? "";
        var textSb = new StringBuilder();
        CollectInlineText(node, textSb);
        var linkText = textSb.ToString().Trim();
        if (string.IsNullOrEmpty(linkText)) linkText = href;

        // URL 策略
        var policy = _options.UrlPolicy;
        if (policy != null && !policy.IsAllowed(href))
        {
            // 链接不可用：只输出文本
            sb.Append(linkText);
            return;
        }
        if (string.IsNullOrEmpty(href))
        {
            sb.Append(linkText);
            return;
        }
        sb.Append('[').Append(linkText).Append("](")
          .Append(TextUtilities.EscapeLinkTarget(href)).Append(')');
    }

    private void EmitImageInline(DomNode node, StringBuilder? targetSb = null)
    {
        var sb = targetSb ?? _output;
        // 无图模式：跳过图片，alt 非空时只输出 alt 文本
        if (_options.SkipImages)
        {
            var altOnly = node.GetAttr("alt") ?? "";
            if (!string.IsNullOrEmpty(altOnly)) sb.Append(altOnly);
            return;
        }
        var src = node.GetAttr("src") ?? "";
        var alt = node.GetAttr("alt") ?? "";
        var title = node.GetAttr("title");

        // URL 策略：禁 data: URL
        var policy = _options.UrlPolicy;
        if (policy != null && !policy.IsAllowed(src))
        {
            // 图片不可用：输出 alt 文本（若有）
            if (!string.IsNullOrEmpty(alt)) sb.Append(alt);
            return;
        }
        if (string.IsNullOrEmpty(src))
        {
            if (!string.IsNullOrEmpty(alt)) sb.Append(alt);
            return;
        }
        sb.Append("![").Append(alt).Append("](")
          .Append(TextUtilities.EscapeLinkTarget(src));
        if (!string.IsNullOrEmpty(title))
            sb.Append(" \"").Append(title.Replace("\"", "\\\"")).Append('"');
        sb.Append(')');
    }

    private void EmitInputInline(DomNode node, StringBuilder sb)
    {
        var type = (node.GetAttr("type") ?? "text").ToLowerInvariant();
        var value = node.GetAttr("value") ?? "";
        var placeholder = node.GetAttr("placeholder") ?? "";
        switch (type)
        {
            case "hidden":
                return;
            case "submit":
            case "button":
            case "reset":
                sb.Append('[').Append(string.IsNullOrEmpty(value) ? "按钮" : value).Append(']');
                return;
            case "checkbox":
                var checked_ = node.HasAttr("checked");
                sb.Append(checked_ ? "[x] " : "[ ] ");
                return;
            case "radio":
                sb.Append(node.HasAttr("checked") ? "(•) " : "( ) ");
                return;
            default:
                sb.Append('[').Append(type).Append(':')
                  .Append(string.IsNullOrEmpty(value) ? placeholder : value).Append(']');
                return;
        }
    }

    private void EmitSelectInline(DomNode node, StringBuilder sb)
    {
        // 简化：输出第一个 selected option 的文本
        string? selected = null;
        foreach (var child in node.Children)
        {
            if (!child.IsElement) continue;
            if (child.NameUpper == "OPTION")
            {
                if (child.HasAttr("selected"))
                {
                    var s = new StringBuilder();
                    CollectInlineText(child, s);
                    selected = s.ToString().Trim();
                    break;
                }
                if (selected == null)
                {
                    var s = new StringBuilder();
                    CollectInlineText(child, s);
                    selected = s.ToString().Trim();
                }
            }
        }
        if (!string.IsNullOrEmpty(selected))
            sb.Append('[').Append(selected).Append(']');
    }
}
