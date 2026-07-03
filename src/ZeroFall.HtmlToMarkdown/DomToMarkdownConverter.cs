using System.Text;
using System.Text.Json;

namespace ZeroFall.HtmlToMarkdown;

/// <summary>
/// 将 CDP <c>DOM.getDocument</c> 返回的节点树直接转换为 Markdown 字符串。
/// <para>
/// 与 OfficeIMO 走 AngleSharp 解析 HTML 字符串不同，本类直接消费 <see cref="JsonElement"/>
/// 节点树。这样彻底避免 HTML 解析阶段反转义实体导致的标签注入
/// （例如百度把 CSS 藏在 <c>&lt;textarea&gt;</c> 文本节点里，HTML 解析器会把它当真标签）。
/// </para>
/// <para>
/// 架构借鉴 OfficeIMO.Html：
/// <list type="bullet">
///   <item>块/内联分离（partial 类，Blocks/Inlines 分文件）</item>
///   <item>选项对象 <see cref="HtmlToMarkdownOptions"/></item>
///   <item>URL 策略 <see cref="HtmlUrlPolicy"/>（禁 data:/javascript:）</item>
///   <item>隐藏元素检测 <see cref="HiddenElementDetector"/></item>
///   <item>文本工具 <see cref="TextUtilities"/></item>
/// </list>
/// </para>
/// <para>
/// 核心设计：HTML 渲染中，内联节点（文本、A、SPAN、STRONG 等）会在同一行流式排列，
/// 块级节点（P、DIV、H1-6、UL、TABLE 等）会换行。本转换器在处理容器子节点时，
/// 把连续的内联子节点分组成隐式段落，避免把内联内容碎片化成多个段落。
/// </para>
/// <para>AOT 友好：不使用反射，所有分支硬编码；使用 partial 类拆分文件便于维护。</para>
/// </summary>
public sealed partial class DomToMarkdownConverter
{
    private readonly HtmlToMarkdownOptions _options;
    private readonly StringBuilder _output = new();
    private readonly Stack<BlockContext> _contextStack = new();

    // 块级元素集合：这些元素会独占一行，前后需要空行分隔
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "P", "DIV", "SECTION", "ARTICLE", "MAIN", "HEADER", "FOOTER", "NAV", "ASIDE",
        "H1", "H2", "H3", "H4", "H5", "H6", "UL", "OL", "LI", "DL", "DT", "DD",
        "TABLE", "THEAD", "TBODY", "TFOOT", "TR", "TD", "TH", "CAPTION",
        "PRE", "BLOCKQUOTE", "HR", "FIGURE", "FIGCAPTION", "DETAILS", "SUMMARY",
        "FORM", "FIELDSET", "ADDRESS", "MENU", "HGROUP", "BODY", "HTML"
    };

    public DomToMarkdownConverter() : this(HtmlToMarkdownOptions.Default) { }

    public DomToMarkdownConverter(HtmlToMarkdownOptions options)
    {
        _options = options;
        _contextStack.Push(new BlockContext());
    }

    /// <summary>转换入口。传入 CDP <c>dom.getDocument</c> 返回的 <c>root</c> 节点。</summary>
    public string Convert(JsonElement root)
    {
        var node = new DomNode(root);
        ConvertChildrenMixed(node);
        return FinalizeOutput();
    }

    /// <summary>转换入口（重载，传 DomNode）。</summary>
    public string Convert(DomNode root)
    {
        ConvertChildrenMixed(root);
        return FinalizeOutput();
    }

    /// <summary>
    /// 转换为纯文本。复用噪音过滤和隐藏元素检测，但不输出 markdown 语法标记，
    /// 只把所有可见文本按出现顺序拼接（块级元素间换行）。
    /// </summary>
    public string ConvertToText(JsonElement root)
    {
        var node = new DomNode(root);
        var sb = new StringBuilder();
        CollectVisibleText(node, sb);
        var result = sb.ToString();
        // 合并多余空行
        while (result.Contains("\n\n\n"))
            result = result.Replace("\n\n\n", "\n\n");
        return result.Trim();
    }

    /// <summary>递归收集可见文本到 StringBuilder。</summary>
    private void CollectVisibleText(DomNode node, StringBuilder sb)
    {
        if (node.IsText)
        {
            var text = node.TextValue ?? "";
            text = TextUtilities.UnescapeHtmlEntities(text);
            var ctx = _contextStack.Peek();
            if (!ctx.InPre)
                text = TextUtilities.CollapseWhitespace(text);
            if (!string.IsNullOrWhiteSpace(text))
                sb.Append(text);
            return;
        }
        if (node.IsComment || node.IsDocumentType) return;
        if (node.IsDocument)
        {
            foreach (var c in node.Children)
                CollectVisibleText(c, sb);
            return;
        }
        if (!node.IsElement) return;
        if (_options.SkipTags.Contains(node.NameUpper)) return;
        if (_options.SkipHiddenElements && HiddenElementDetector.IsHidden(node)) return;

        // 块级元素前加换行
        if (BlockElements.Contains(node.NameUpper) && sb.Length > 0)
        {
            if (sb[sb.Length - 1] != '\n') sb.AppendLine();
        }
        // PRE 内保留原始文本
        if (node.NameUpper == "PRE")
        {
            var preCtx = new BlockContext(_contextStack.Peek()) { InPre = true };
            _contextStack.Push(preCtx);
            var raw = new StringBuilder();
            CollectRawText(node, raw);
            _contextStack.Pop();
            sb.Append(raw.ToString().TrimEnd('\n', '\r'));
            sb.AppendLine();
            return;
        }
        // BR 换行
        if (node.NameUpper == "BR")
        {
            sb.AppendLine();
            return;
        }
        foreach (var c in node.Children)
            CollectVisibleText(c, sb);
        // 块级元素后加换行
        if (BlockElements.Contains(node.NameUpper) && sb.Length > 0 && sb[sb.Length - 1] != '\n')
            sb.AppendLine();
    }

    /// <summary>供 partial 类其它文件使用的内部转换入口。</summary>
    internal string ConvertInternal(DomNode root)
    {
        ConvertChildrenMixed(root);
        return FinalizeOutput();
    }

    private string FinalizeOutput()
    {
        var result = _output.ToString();
        while (result.Contains("\n\n\n"))
            result = result.Replace("\n\n\n", "\n\n");
        return result.Trim();
    }

    /// <summary>
    /// 处理容器子节点，把连续的内联子节点分组成隐式段落，块级子节点独立输出。
    /// 这是 HTML 渲染模型的核心：内联元素流式排列，块级元素换行。
    /// </summary>
    private void ConvertChildrenMixed(DomNode parent)
    {
        StringBuilder? inlineBuffer = null;

        foreach (var child in parent.Children)
        {
            if (IsInlineNode(child))
            {
                // 累积内联节点到 buffer
                inlineBuffer ??= new StringBuilder();
                EmitInlineNode(child, inlineBuffer);
            }
            else
            {
                // 遇到块级节点：先 flush 内联 buffer 为段落
                if (inlineBuffer != null)
                {
                    FlushInlineBuffer(inlineBuffer);
                    inlineBuffer = null;
                }
                ConvertNodeToBlock(child);
            }
        }
        // 收尾：flush 剩余内联
        if (inlineBuffer != null)
            FlushInlineBuffer(inlineBuffer);
    }

    /// <summary>把累积的内联 buffer 作为段落输出。</summary>
    private void FlushInlineBuffer(StringBuilder buffer)
    {
        var text = buffer.ToString();
        var ctx = _contextStack.Peek();
        if (ctx.InPre)
        {
            // PRE 内不折叠空白，直接输出
            _output.Append(text);
            return;
        }
        text = TextUtilities.CollapseWhitespace(text).Trim();
        if (string.IsNullOrEmpty(text)) return;
        EnsureBlankLine();
        _output.Append(text);
        EnsureBlankLine();
    }

    /// <summary>判断节点是否为内联节点（文本/注释/内联元素）。</summary>
    private bool IsInlineNode(DomNode node)
    {
        // 文本节点：内联
        if (node.IsText) return true;
        // 注释/doctype/document：跳过，当内联处理（不会输出）
        if (node.IsComment || node.IsDocumentType || node.IsDocument) return true;
        if (!node.IsElement) return true;
        // BR 是内联的（行内换行）
        if (node.NameUpper == "BR" || node.NameUpper == "WBR") return true;
        // IMG 是内联的（行内图片）
        if (node.NameUpper == "IMG") return true;
        // 块级元素：不是内联
        if (BlockElements.Contains(node.NameUpper)) return false;
        // 其它元素默认当内联（SPAN/A/STRONG/EM/CODE/DEL/S/U/MARK/SUB/SUP/KBD/ABBR/Q/CITE/SMALL/LABEL/FONT/TIME/INPUT/BUTTON/SELECT 等）
        return true;
    }

    /// <summary>把节点当作块级元素转换。</summary>
    private void ConvertNodeToBlock(DomNode node)
    {
        // 文档根节点：不输出标签，递归子节点
        if (node.IsDocument)
        {
            ConvertChildrenMixed(node);
            return;
        }

        // 注释 / doctype：跳过
        if (node.IsComment || node.IsDocumentType) return;

        // 文本节点：不应直接到这里（应由 ConvertChildrenMixed 处理），
        // 但兜底：当块级调用
        if (node.IsText)
        {
            var text = node.TextValue ?? "";
            text = TextUtilities.UnescapeHtmlEntities(text);
            text = TextUtilities.CollapseWhitespace(text);
            if (string.IsNullOrWhiteSpace(text)) return;
            EnsureBlankLine();
            _output.Append(text.Trim());
            EnsureBlankLine();
            return;
        }

        if (!node.IsElement) return;

        // 整棵跳过的标签
        if (_options.SkipTags.Contains(node.NameUpper)) return;

        // 隐藏元素跳过
        if (_options.SkipHiddenElements && HiddenElementDetector.IsHidden(node)) return;

        switch (node.NameUpper)
        {
            case "H1": EmitHeading(node, 1); return;
            case "H2": EmitHeading(node, 2); return;
            case "H3": EmitHeading(node, 3); return;
            case "H4": EmitHeading(node, 4); return;
            case "H5": EmitHeading(node, 5); return;
            case "H6": EmitHeading(node, 6); return;
            case "P": EmitParagraph(node); return;
            case "HR":
                EnsureBlankLine();
                _output.AppendLine("---");
                EnsureBlankLine();
                return;
            case "UL": EmitList(node, ordered: false); return;
            case "OL": EmitList(node, ordered: true); return;
            case "LI":
                // 孤立 LI：用子转换器处理其子节点
                EmitOrphanListItem(node);
                return;
            case "PRE": EmitPre(node); return;
            case "BLOCKQUOTE": EmitBlockquote(node); return;
            case "TABLE": EmitTable(node); return;
            case "DL": EmitDefinitionList(node); return;
            case "DETAILS": EmitDetails(node); return;
            case "FIGURE": EmitFigure(node); return;
            case "FORM":
            case "FIELDSET":
            case "DIV":
            case "SECTION":
            case "ARTICLE":
            case "MAIN":
            case "HEADER":
            case "FOOTER":
            case "NAV":
            case "ASIDE":
            case "ADDRESS":
            case "HGROUP":
            case "MENU":
            case "BODY":
            case "HTML":
                // 容器：递归处理子节点（块/内联分组）
                ConvertChildrenMixed(node);
                return;
            case "CAPTION":
                // 表格标题：作为居中文本
                EnsureBlankLine();
                _output.Append("*");
                CollectInlineText(node, _output);
                _output.AppendLine("*");
                EnsureBlankLine();
                return;
            case "DT":
                // 定义列表项（孤立）
                EnsureBlankLine();
                _output.Append("**");
                CollectInlineText(node, _output);
                _output.AppendLine("**");
                return;
            case "DD":
                EnsureBlankLine();
                _output.Append(": ");
                CollectInlineText(node, _output);
                EnsureBlankLine();
                return;
            default:
                // 未知块级元素：递归子节点
                ConvertChildrenMixed(node);
                return;
        }
    }

    private void EmitHeading(DomNode node, int level)
    {
        EnsureBlankLine();
        _output.Append(new string('#', level)).Append(' ');
        var inlineCtx = new BlockContext(_contextStack.Peek()) { InHeading = true };
        _contextStack.Push(inlineCtx);
        var sb = new StringBuilder();
        CollectInlineText(node, sb);
        _contextStack.Pop();
        var text = TextUtilities.CollapseWhitespace(sb.ToString()).Trim();
        _output.Append(text);
        EnsureBlankLine();
    }

    private void EmitParagraph(DomNode node)
    {
        EnsureBlankLine();
        var sb = new StringBuilder();
        CollectInlineText(node, sb);
        var text = TextUtilities.CollapseWhitespace(sb.ToString()).Trim();
        if (text.Length > 0)
        {
            _output.Append(text);
            EnsureBlankLine();
        }
    }

    private void EmitPre(DomNode node)
    {
        EnsureBlankLine();
        _output.Append("```");
        var lang = TryGetCodeLanguage(node);
        if (!string.IsNullOrEmpty(lang))
            _output.Append(lang);
        _output.AppendLine();
        var preCtx = new BlockContext(_contextStack.Peek()) { InPre = true };
        _contextStack.Push(preCtx);
        var sb = new StringBuilder();
        CollectRawText(node, sb);
        _contextStack.Pop();
        _output.Append(sb.ToString().TrimEnd('\n', '\r'));
        _output.AppendLine();
        _output.Append("```");
        EnsureBlankLine();
    }

    private void EmitBlockquote(DomNode node)
    {
        EnsureBlankLine();
        // 用子转换器处理 blockquote 内容，避免污染主输出
        var subConverter = new DomToMarkdownConverter(_options);
        var inner = subConverter.ConvertInternal(node);
        foreach (var line in inner.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrEmpty(trimmed))
                _output.AppendLine(">");
            else
                _output.Append("> ").AppendLine(trimmed);
        }
        EnsureBlankLine();
    }

    private void EmitList(DomNode node, bool ordered)
    {
        EnsureBlankLine();
        var listCtx = new BlockContext(_contextStack.Peek())
        {
            InListItem = true,
            ListOrdered = ordered,
            ListIndex = 0
        };
        _contextStack.Push(listCtx);
        foreach (var child in node.Children)
        {
            if (!child.IsElement) continue;
            if (!string.Equals(child.NameUpper, "LI", System.StringComparison.OrdinalIgnoreCase))
            {
                // 嵌套 OL/UL 在 LI 外出现（异常结构）
                if (child.NameUpper == "UL" || child.NameUpper == "OL")
                {
                    EmitList(child, ordered: child.NameUpper == "OL");
                }
                continue;
            }
            listCtx.ListIndex++;
            var prefix = ordered ? $"{listCtx.ListIndex}. " : "- ";
            EmitListItem(child, prefix);
        }
        _contextStack.Pop();
        EnsureBlankLine();
    }

    /// <summary>
    /// 输出一个列表项。LI 的内联子节点作为一行，嵌套的 UL/OL 缩进输出。
    /// 用子转换器收集 LI 内容，避免污染主输出缓冲区。
    /// </summary>
    private void EmitListItem(DomNode li, string prefix)
    {
        _output.Append(prefix);
        // 分离 LI 的内联子节点和嵌套列表
        var inlineSb = new StringBuilder();
        var nestedLists = new System.Collections.Generic.List<DomNode>();
        foreach (var child in li.Children)
        {
            if (child.IsElement && (child.NameUpper == "UL" || child.NameUpper == "OL"))
                nestedLists.Add(child);
            else if (IsInlineNode(child))
                EmitInlineNode(child, inlineSb);
            else
            {
                // 块级子节点（如 P）：先 flush 内联，再作为子块处理
                if (inlineSb.Length > 0)
                {
                    var text = TextUtilities.CollapseWhitespace(inlineSb.ToString()).Trim();
                    if (text.Length > 0) _output.Append(text);
                    inlineSb.Clear();
                }
                // 用子转换器处理块级子节点
                var sub = new DomToMarkdownConverter(_options);
                var subResult = sub.ConvertInternal(child);
                // 缩进输出
                foreach (var line in subResult.Split('\n'))
                {
                    var trimmed = line.TrimEnd('\r');
                    if (!string.IsNullOrEmpty(trimmed))
                        _output.Append("  ").AppendLine(trimmed);
                }
            }
        }
        // 输出收集的内联文本
        if (inlineSb.Length > 0)
        {
            var text = TextUtilities.CollapseWhitespace(inlineSb.ToString()).Trim();
            _output.Append(text);
        }
        _output.AppendLine();
        // 输出嵌套列表（缩进）
        foreach (var nested in nestedLists)
        {
            EmitList(nested, ordered: nested.NameUpper == "OL");
        }
    }

    /// <summary>孤立 LI（无父 UL/OL）：当作无序列表项输出。</summary>
    private void EmitOrphanListItem(DomNode node)
    {
        EnsureBlankLine();
        EmitListItem(node, "- ");
        EnsureBlankLine();
    }

    private void EmitTable(DomNode node)
    {
        var rows = new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
        CollectTableRows(node, rows);
        if (rows.Count == 0) return;
        EnsureBlankLine();
        int maxCols = _options.MaxTableColumns;
        int maxRows = _options.MaxTableRows;
        if (maxRows > 0 && rows.Count > maxRows) rows.RemoveRange(maxRows, rows.Count - maxRows);
        // 第一行作为表头
        var header = rows[0];
        int colCount = maxCols > 0 ? System.Math.Min(header.Count, maxCols) : header.Count;
        foreach (var cell in header.GetRange(0, colCount))
            _output.Append("| ").Append(cell.Trim()).Append(' ');
        _output.AppendLine("|");
        for (int i = 0; i < colCount; i++)
            _output.Append("| --- ");
        _output.AppendLine("|");
        for (int i = 1; i < rows.Count; i++)
        {
            for (int j = 0; j < colCount; j++)
            {
                var cell = j < rows[i].Count ? rows[i][j] : "";
                _output.Append("| ").Append(cell.Trim()).Append(' ');
            }
            _output.AppendLine("|");
        }
        EnsureBlankLine();
    }

    private void CollectTableRows(DomNode node, System.Collections.Generic.List<System.Collections.Generic.List<string>> rows)
    {
        foreach (var child in node.Children)
        {
            if (!child.IsElement) continue;
            switch (child.NameUpper)
            {
                case "TR":
                    var row = new System.Collections.Generic.List<string>();
                    CollectTableCells(child, row);
                    if (row.Count > 0) rows.Add(row);
                    break;
                case "THEAD":
                case "TBODY":
                case "TFOOT":
                    CollectTableRows(child, rows);
                    break;
            }
        }
    }

    private void CollectTableCells(DomNode tr, System.Collections.Generic.List<string> cells)
    {
        foreach (var child in tr.Children)
        {
            if (!child.IsElement) continue;
            if (child.NameUpper == "TD" || child.NameUpper == "TH")
            {
                var sb = new StringBuilder();
                var cellCtx = new BlockContext(_contextStack.Peek()) { InTableCell = true };
                _contextStack.Push(cellCtx);
                CollectInlineText(child, sb);
                _contextStack.Pop();
                cells.Add(sb.ToString().Trim().Replace("\n", " "));
            }
        }
    }

    private void EmitDefinitionList(DomNode node)
    {
        EnsureBlankLine();
        var pendingTerms = new System.Collections.Generic.List<string>();
        foreach (var child in node.Children)
        {
            if (!child.IsElement) continue;
            if (child.NameUpper == "DT")
            {
                var sb = new StringBuilder();
                CollectInlineText(child, sb);
                pendingTerms.Add(sb.ToString().Trim());
            }
            else if (child.NameUpper == "DD" && pendingTerms.Count > 0)
            {
                foreach (var t in pendingTerms)
                {
                    _output.Append("**").Append(t).AppendLine("**");
                }
                pendingTerms.Clear();
                var ddSb = new StringBuilder();
                CollectInlineText(child, ddSb);
                _output.Append(": ").AppendLine(ddSb.ToString().Trim());
                _output.AppendLine();
            }
        }
        EnsureBlankLine();
    }

    private void EmitDetails(DomNode node)
    {
        EnsureBlankLine();
        foreach (var child in node.Children)
        {
            if (!child.IsElement) continue;
            if (child.NameUpper == "SUMMARY")
            {
                var summarySb = new StringBuilder();
                CollectInlineText(child, summarySb);
                _output.Append("<summary>").Append(summarySb.ToString().Trim()).AppendLine("</summary>");
            }
            else
            {
                var sub = new DomToMarkdownConverter(_options);
                var subResult = sub.ConvertInternal(child);
                _output.AppendLine();
                _output.Append(subResult);
            }
        }
        EnsureBlankLine();
    }

    private void EmitFigure(DomNode node)
    {
        EnsureBlankLine();
        foreach (var child in node.Children)
        {
            if (!child.IsElement) continue;
            if (child.NameUpper == "IMG")
            {
                EmitImageInline(child);
                _output.AppendLine();
            }
            else if (child.NameUpper == "FIGCAPTION")
            {
                _output.Append('*');
                CollectInlineText(child, _output);
                _output.AppendLine("*");
            }
            else
            {
                ConvertNodeToBlock(child);
            }
        }
        EnsureBlankLine();
    }

    /// <summary>收集节点的原始文本（用于 PRE 块）。</summary>
    private void CollectRawText(DomNode node, StringBuilder sb)
    {
        if (node.IsText)
        {
            sb.Append(node.TextValue ?? "");
            return;
        }
        if (!node.IsElement) return;
        foreach (var child in node.Children)
            CollectRawText(child, sb);
    }

    private string? TryGetCodeLanguage(DomNode pre)
    {
        // <pre><code class="language-xxx">
        foreach (var child in pre.Children)
        {
            if (!child.IsElement) continue;
            if (child.NameUpper == "CODE")
            {
                var cls = child.GetAttr("class") ?? "";
                var tokens = cls.Split(' ');
                foreach (var t in tokens)
                {
                    if (t.StartsWith("language-", System.StringComparison.OrdinalIgnoreCase))
                        return t.Substring("language-".Length);
                }
            }
        }
        return null;
    }

    private void EnsureBlankLine()
    {
        if (_output.Length == 0) return;
        int i = _output.Length - 1;
        int newlines = 0;
        while (i >= 0 && (_output[i] == '\n' || _output[i] == '\r'))
        {
            if (_output[i] == '\n') newlines++;
            i--;
        }
        if (newlines == 0) _output.AppendLine().AppendLine();
        else if (newlines == 1) _output.AppendLine();
    }
}

/// <summary>块转换过程中的上下文（是否在列表项、表格单元、PRE 内等）。</summary>
internal sealed class BlockContext
{
    public bool InListItem;
    public bool InTableCell;
    public bool InPre;
    public bool InHeading;
    public bool InBlockquote;
    public bool ListOrdered;
    public int ListIndex;

    public BlockContext() { }
    public BlockContext(BlockContext other)
    {
        InListItem = other.InListItem;
        InTableCell = other.InTableCell;
        InPre = other.InPre;
        InHeading = other.InHeading;
        InBlockquote = other.InBlockquote;
        ListOrdered = other.ListOrdered;
        ListIndex = other.ListIndex;
    }
}
