using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using System.Xml;

namespace ZeroFall.SqlEditor;

/// <summary>按主题提供 AvaloniaEdit 语法高亮；暗色模式使用 VS Code 风格配色，避免内置浅色规则在深色背景上不可读。</summary>
internal static class CodeSyntaxHighlighting
{
    private static readonly Dictionary<string, IHighlightingDefinition?> Cache = new(StringComparer.Ordinal);

    public static void InvalidateCache() => Cache.Clear();

    public static IHighlightingDefinition? GetForFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        var language = ext switch
        {
            "sql" => "SQL",
            "json" => "JSON",
            "xml" => "XML",
            "html" or "htm" => "HTML",
            "cs" => "C#",
            "js" => "JavaScript",
            "ts" => "TypeScript",
            "java" => "Java",
            "py" => "Python",
            "rb" => "Ruby",
            "php" => "PHP",
            "cpp" or "c" or "h" or "hpp" => "C++",
            "css" => "CSS",
            "markdown" or "md" => "Plain",
            "txt" or "log" or "ini" or "cfg" or "conf" or "yaml" or "yml" => "Plain",
            _ => "Plain"
        };

        return GetDefinition(language);
    }

    public static IHighlightingDefinition? GetDefinition(string? languageName)
    {
        if (string.IsNullOrEmpty(languageName))
            return null;

        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var key = $"{(isDark ? 'd' : 'l')}:{languageName}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        IHighlightingDefinition? def;
        if (isDark)
            def = LoadDarkDefinition(languageName);
        else
            def = HighlightingManager.Instance.GetDefinition(languageName);

        Cache[key] = def;
        return def;
    }

    private static IHighlightingDefinition? LoadDarkDefinition(string languageName)
    {
        var xshd = languageName switch
        {
            "SQL" => BuildSqlXshd(),
            "JSON" => BuildJsonXshd(),
            "XML" or "HTML" => BuildXmlHtmlXshd(),
            "JavaScript" or "TypeScript" => BuildJavaScriptXshd(),
            "C#" or "C++" or "Java" => BuildCppFamilyXshd(),
            "Python" => BuildPythonXshd(),
            "CSS" => BuildCssXshd(),
            _ => BuildPlainXshd()
        };

        using var reader = new XmlTextReader(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private const string UrlRule = """
            <Rule color="Url">\bhttps?://[^\s"'&lt;&gt;\]\)]+</Rule>
            """;

    private static string BuildSqlXshd() =>
        BuildXshd("SQL", $"""
            <Rule color="Keyword">\b(SELECT|FROM|WHERE|JOIN|LEFT|RIGHT|INNER|OUTER|ON|AS|AND|OR|NOT|IN|IS|NULL|ORDER|BY|GROUP|HAVING|LIMIT|OFFSET|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|INDEX|DROP|ALTER|PRIMARY|KEY|FOREIGN|REFERENCES|UNION|ALL|DISTINCT|CASE|WHEN|THEN|ELSE|END|EXISTS|BETWEEN|LIKE|ASC|DESC|WITH|PRAGMA|VACUUM|ATTACH|DETACH)\b</Rule>
            <Rule color="Type">\b(INTEGER|INT|TEXT|REAL|BLOB|BOOLEAN|BOOL|NUMERIC|VARCHAR|CHAR|DATE|DATETIME|TIMESTAMP|DOUBLE|FLOAT|DECIMAL)\b</Rule>
            <Rule color="Number">\b-?\d+(\.\d+)?\b</Rule>
            <Span color="String" begin="'" end="'"/>
            <Span color="String" begin="&quot;" end="&quot;"/>
            <Span color="Comment" begin="--" end="$"/>
            <Span color="Comment" begin="/*" end="*/"/>
            {UrlRule}
            """);

    private static string BuildJsonXshd() =>
        BuildXshd("JSON", """
            <Span color="String" begin="&quot;" end="&quot;"/>
            <Rule color="Number">\b-?\d+(\.\d+)?([eE][+-]?\d+)?\b</Rule>
            <Rule color="Keyword">\b(true|false|null)\b</Rule>
            <Rule color="Delimiter">[{}\[\]:,]</Rule>
            """ + UrlRule);

    private static string BuildXmlHtmlXshd() =>
        BuildXshd("XML", """
            <Span color="Comment" begin="&lt;!--" end="--&gt;"/>
            <Span color="Tag" begin="&lt;/?" end="/?&gt;">
              <RuleSet>
                <Rule color="Attribute">[A-Za-z_][\w\-]*(?==)</Rule>
                <Span color="String" begin="&quot;" end="&quot;"/>
                <Span color="String" begin="'" end="'"/>
              </RuleSet>
            </Span>
            """);

    private static string BuildJavaScriptXshd() =>
        BuildXshd("JavaScript", """
            <Span color="Comment" begin="//" end="$"/>
            <Span color="Comment" begin="/*" end="*/"/>
            <Span color="String" begin="&quot;" end="&quot;"/>
            <Span color="String" begin="'" end="'"/>
            <Rule color="Number">\b-?\d+(\.\d+)?([eE][+-]?\d+)?\b</Rule>
            <Rule color="Keyword">\b(const|let|var|function|return|if|else|for|while|do|class|new|this|async|await|try|catch|throw|import|export|from|of|in|typeof|instanceof|switch|case|break|continue|default|void|delete|yield|null|undefined|true|false)\b</Rule>
            <Rule color="Delimiter">[\{\}\[\]();,.]</Rule>
            """ + UrlRule);

    private static string BuildCppFamilyXshd() =>
        BuildXshd("CSharp", """
            <Span color="Comment" begin="//" end="$"/>
            <Span color="Comment" begin="/*" end="*/"/>
            <Span color="String" begin="&quot;" end="&quot;"/>
            <Rule color="Number">\b-?\d+(\.\d+)?\b</Rule>
            <Rule color="Keyword">\b(if|else|for|while|do|return|class|struct|enum|namespace|using|public|private|protected|static|void|int|long|double|float|bool|true|false|null|new|delete|this|switch|case|break|continue|try|catch|throw|const|virtual|override|async|await)\b</Rule>
            """);

    private static string BuildPythonXshd() =>
        BuildXshd("Python", """
            <Span color="Comment" begin="#" end="$"/>
            <Span color="String" begin="&quot;" end="&quot;"/>
            <Span color="String" begin="'" end="'"/>
            <Rule color="Number">\b-?\d+(\.\d+)?\b</Rule>
            <Rule color="Keyword">\b(def|class|if|elif|else|for|while|return|import|from|as|with|try|except|finally|raise|pass|break|continue|and|or|not|in|is|None|True|False|lambda|yield|async|await)\b</Rule>
            """);

    private static string BuildCssXshd() =>
        BuildXshd("CSS", """
            <Span color="Comment" begin="/*" end="*/"/>
            <Span color="String" begin="&quot;" end="&quot;"/>
            <Span color="String" begin="'" end="'"/>
            <Rule color="Property">[\w-]+(?=\s*:)</Rule>
            <Rule color="Number">\b-?\d+(\.\d+)?(px|em|rem|%|vh|vw|s|ms)?\b</Rule>
            <Rule color="Delimiter">[{}\[\];:]</Rule>
            """);

    private static string BuildPlainXshd() =>
        BuildXshd("Plain", $"""
            <Span color="String" begin="&quot;" end="&quot;"/>
            <Span color="String" begin="'" end="'"/>
            <Rule color="Number">\b-?\d+(\.\d+)?\b</Rule>
            {UrlRule}
            """);

    private static string BuildXshd(string name, string rules)
    {
        const string darkColors = """
              <Color name="Keyword" foreground="#7BB8F8"/>
              <Color name="Type" foreground="#4EC9B0"/>
              <Color name="String" foreground="#CE9178"/>
              <Color name="Number" foreground="#B5CEA8"/>
              <Color name="Comment" foreground="#6A9955"/>
              <Color name="Tag" foreground="#7BB8F8"/>
              <Color name="Attribute" foreground="#9CDCFE"/>
              <Color name="Property" foreground="#9CDCFE"/>
              <Color name="Url" foreground="#9CDCFE"/>
              <Color name="Delimiter" foreground="#808080"/>
              <Color name="Heading" foreground="#7BB8F8" fontWeight="bold"/>
              <Color name="Code" foreground="#CE9178"/>
            """;

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <SyntaxDefinition name="{name}-Dark" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
            {darkColors}
              <RuleSet>
            {rules}
              </RuleSet>
            </SyntaxDefinition>
            """;
    }
}
