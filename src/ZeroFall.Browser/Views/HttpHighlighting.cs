using Avalonia;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace ZeroFall.Browser.Views;

internal static class HttpHighlighting
{
    private static readonly Dictionary<string, IHighlightingDefinition?> _cache = new();

    public static IHighlightingDefinition? GetDefinition(string? contentType)
    {
        var key = $"{Application.Current?.ActualThemeVariant}:{contentType}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var def = BuildComposite(isDark, contentType);
        _cache[key] = def;
        return def;
    }

    public static void InvalidateCache() => _cache.Clear();

    private static IHighlightingDefinition? BuildComposite(bool dark, string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return BuildFromXshd(dark, BodyMode.Plain);

        if (contentType.Contains("json"))
            return BuildFromXshd(dark, BodyMode.Json);

        if (contentType.Contains("html"))
            return BuildFromXshd(dark, BodyMode.Html);

        if (contentType.Contains("xml") || contentType.Contains("svg"))
            return BuildFromXshd(dark, BodyMode.Xml);

        if (contentType.Contains("javascript"))
            return BuildFromXshd(dark, BodyMode.JavaScript);

        if (contentType.Contains("css"))
            return BuildFromXshd(dark, BodyMode.Css);

        if (contentType.Contains("x-www-form-urlencoded"))
            return BuildFromXshd(dark, BodyMode.FormUrlEncoded);

        return BuildFromXshd(dark, BodyMode.Plain);
    }

    private enum BodyMode { Plain, Json, Html, Xml, JavaScript, Css, FormUrlEncoded }

    private static IHighlightingDefinition BuildFromXshd(bool dark, BodyMode mode)
    {
        var xshd = GenerateXshd(dark, mode);
        using var reader = new XmlTextReader(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static string GenerateXshd(bool dark, BodyMode mode)
    {
        string method     = dark ? "#7BB8F8" : "#0000FF";
        string version    = dark ? "#6A9955" : "#008000";
        string statusCode = dark ? "#DCDCAA" : "#C7A025";
        string headerName = dark ? "#4EC9B0" : "#267F99";
        string headerVal  = dark ? "#CE9178" : "#A31515";
        string postKey    = dark ? "#9CDCFE" : "#001080";
        string postVal    = dark ? "#CE9178" : "#A31515";
        string str        = dark ? "#CE9178" : "#A31515";
        string num        = dark ? "#B5CEA8" : "#098658";
        string kw         = dark ? "#7BB8F8" : "#0000FF";
        string comment    = dark ? "#6A9955" : "#008000";
        string tag        = dark ? "#7BB8F8" : "#800000";
        string attr       = dark ? "#9CDCFE" : "#FF0000";
        string cssProp    = dark ? "#9CDCFE" : "#FF0000";
        string delimiter  = dark ? "#808080" : "#800000";
        string xmlDelim   = dark ? "#808080" : "#0000FF";
        string url        = dark ? "#9CDCFE" : "#0969DA";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine($"<SyntaxDefinition name=\"HTTP+{mode}\" xmlns=\"http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008\">");

        sb.AppendLine($"  <Color name=\"RequestMethod\"  foreground=\"{method}\"     fontWeight=\"bold\"/>");
        sb.AppendLine($"  <Color name=\"HttpVersion\"    foreground=\"{version}\"/>");
        sb.AppendLine($"  <Color name=\"StatusCode\"     foreground=\"{statusCode}\" fontWeight=\"bold\"/>");
        sb.AppendLine($"  <Color name=\"HeaderName\"     foreground=\"{headerName}\"/>");
        sb.AppendLine($"  <Color name=\"HeaderValue\"    foreground=\"{headerVal}\"/>");
        sb.AppendLine($"  <Color name=\"PostKey\"        foreground=\"{postKey}\"/>");
        sb.AppendLine($"  <Color name=\"PostValue\"      foreground=\"{postVal}\"/>");
        sb.AppendLine($"  <Color name=\"String\"         foreground=\"{str}\"/>");
        sb.AppendLine($"  <Color name=\"Number\"         foreground=\"{num}\"/>");
        sb.AppendLine($"  <Color name=\"Keyword\"        foreground=\"{kw}\"/>");
        sb.AppendLine($"  <Color name=\"Comment\"        foreground=\"{comment}\"/>");
        sb.AppendLine($"  <Color name=\"HtmlTag\"        foreground=\"{tag}\"/>");
        sb.AppendLine($"  <Color name=\"HtmlAttr\"       foreground=\"{attr}\"/>");
        sb.AppendLine($"  <Color name=\"CssProperty\"    foreground=\"{cssProp}\"/>");
        sb.AppendLine($"  <Color name=\"Delimiter\"      foreground=\"{delimiter}\"/>");
        sb.AppendLine($"  <Color name=\"XmlDelimiter\"   foreground=\"{xmlDelim}\"/>");
        sb.AppendLine($"  <Color name=\"Url\"            foreground=\"{url}\"/>");

        sb.AppendLine("  <RuleSet>");

        sb.AppendLine("    <Rule color=\"RequestMethod\">");
        sb.AppendLine("      ^(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS|CONNECT|TRACE)\\b");
        sb.AppendLine("    </Rule>");

        sb.AppendLine("    <Rule color=\"HttpVersion\">");
        sb.AppendLine("      HTTP/[\\d.]+");
        sb.AppendLine("    </Rule>");

        sb.AppendLine("    <Rule color=\"StatusCode\">");
        sb.AppendLine("      (?&lt;=HTTP/[\\d.]+\\s)\\d{3}");
        sb.AppendLine("    </Rule>");

        sb.AppendLine("    <Rule color=\"HeaderName\">");
        sb.AppendLine("      ^[A-Za-z][A-Za-z0-9\\-]*(?=:)");
        sb.AppendLine("    </Rule>");

        sb.AppendLine("    <Rule color=\"HeaderValue\">");
        sb.AppendLine("      (?&lt;=:\\s).+$");
        sb.AppendLine("    </Rule>");

        if (mode == BodyMode.FormUrlEncoded)
        {
            sb.AppendLine("    <Rule color=\"PostKey\">");
            sb.AppendLine("      (?:^|&amp;)([A-Za-z_][\\w\\-\\.]*)(?==)");
            sb.AppendLine("    </Rule>");
            sb.AppendLine("    <Rule color=\"PostValue\">");
            sb.AppendLine("      (?&lt;==)([A-Za-z0-9_\\-\\.\\!~\\*\\'\\(\\)%]+)(?=&amp;|$)");
            sb.AppendLine("    </Rule>");
        }

        if (mode is BodyMode.Json or BodyMode.JavaScript or BodyMode.FormUrlEncoded or BodyMode.Plain)
        {
            sb.AppendLine("    <Span color=\"String\" begin=\"&quot;\" end=\"&quot;\">");
            sb.AppendLine("      <RuleSet>");
            sb.AppendLine("        <Span begin=\"\\\\\" end=\".\"/>");
            sb.AppendLine("      </RuleSet>");
            sb.AppendLine("    </Span>");
        }

        if (mode is BodyMode.Json or BodyMode.JavaScript)
        {
            sb.AppendLine("    <Span color=\"String\" begin=\"'\" end=\"'\">");
            sb.AppendLine("      <RuleSet>");
            sb.AppendLine("        <Span begin=\"\\\\\" end=\".\"/>");
            sb.AppendLine("      </RuleSet>");
            sb.AppendLine("    </Span>");

            sb.AppendLine("    <Rule color=\"Number\">");
            sb.AppendLine("      \\b-?\\d+(\\.\\d+)?([eE][+-]?\\d+)?\\b");
            sb.AppendLine("    </Rule>");

            sb.AppendLine("    <Rule color=\"Keyword\">");
            sb.AppendLine("      \\b(true|false|null|undefined)\\b");
            sb.AppendLine("    </Rule>");

            sb.AppendLine("    <Rule color=\"Delimiter\">");
            sb.AppendLine("      [{}\\[\\]]");
            sb.AppendLine("    </Rule>");
        }

        if (mode is BodyMode.JavaScript or BodyMode.Json or BodyMode.Css)
        {
            sb.AppendLine("    <Span color=\"Comment\" begin=\"//\" end=\"$\"/>");
            sb.AppendLine("    <Span color=\"Comment\" begin=\"/\\*\" end=\"\\*/\"/>");
        }

        if (mode is BodyMode.Html or BodyMode.Xml)
        {
            sb.AppendLine("    <Span color=\"Comment\" begin=\"&lt;!--\" end=\"--&gt;\"/>");
        }

        if (mode is BodyMode.Html or BodyMode.Xml)
        {
            sb.AppendLine("    <Span color=\"HtmlTag\" begin=\"&lt;/?\" end=\"/?&gt;\">");
            sb.AppendLine("      <RuleSet>");
            sb.AppendLine("        <Rule color=\"HtmlAttr\">");
            sb.AppendLine("          [A-Za-z_][\\w\\-]*(?==)");
            sb.AppendLine("        </Rule>");
            sb.AppendLine("        <Span color=\"String\" begin=\"&quot;\" end=\"&quot;\"/>");
            sb.AppendLine("        <Span color=\"String\" begin=\"'\" end=\"'\"/>");
            sb.AppendLine("      </RuleSet>");
            sb.AppendLine("    </Span>");
        }

        if (mode == BodyMode.Xml)
        {
            sb.AppendLine("    <Rule color=\"XmlDelimiter\">");
            sb.AppendLine("      [&lt;&gt;/=]");
            sb.AppendLine("    </Rule>");
        }

        if (mode == BodyMode.JavaScript)
        {
            sb.AppendLine("    <Rule color=\"Keyword\">");
            sb.AppendLine("      \\b(var|let|const|function|if|else|for|while|do|return|class|new|this|async|await|try|catch|throw|import|export|from|of|in|typeof|instanceof|switch|case|break|continue|default|void|delete|yield)\\b");
            sb.AppendLine("    </Rule>");
        }

        if (mode == BodyMode.Css)
        {
            sb.AppendLine("    <Rule color=\"CssProperty\">");
            sb.AppendLine("      [\\w-]+(?=\\s*:)");
            sb.AppendLine("    </Rule>");

            sb.AppendLine("    <Span color=\"String\" begin=\"&quot;\" end=\"&quot;\"/>");
            sb.AppendLine("    <Span color=\"String\" begin=\"'\" end=\"'\"/>");

            sb.AppendLine("    <Rule color=\"Number\">");
            sb.AppendLine("      \\b-?\\d+(\\.\\d+)?(px|em|rem|%|vh|vw|s|ms)?\\b");
            sb.AppendLine("    </Rule>");

            sb.AppendLine("    <Rule color=\"Delimiter\">");
            sb.AppendLine("      [{};]");
            sb.AppendLine("    </Rule>");
        }

        sb.AppendLine("    <Rule color=\"Url\">");
        sb.AppendLine("      \\bhttps?://[^\\s\"'&lt;&gt;\\]\\)]+");
        sb.AppendLine("    </Rule>");

        sb.AppendLine("  </RuleSet>");
        sb.AppendLine("</SyntaxDefinition>");

        return sb.ToString();
    }
}
