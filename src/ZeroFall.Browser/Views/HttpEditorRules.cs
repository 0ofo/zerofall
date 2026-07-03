using System;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;

namespace ZeroFall.Browser.Views;

internal interface IHttpDocumentEditorRules
{
    IHighlightingDefinition? ResolveDefinition(string httpText);
    string ExtractContentType(string httpText);
}

internal sealed class HttpDocumentEditorRules : IHttpDocumentEditorRules
{
    public static readonly IHttpDocumentEditorRules Instance = new HttpDocumentEditorRules();
    public const int MaxHighlightLength = 16 * 1024;

    public IHighlightingDefinition? ResolveDefinition(string httpText)
    {
        var contentType = ExtractContentType(httpText);
        return httpText.Length > MaxHighlightLength
            ? null
            : HttpHighlighting.GetDefinition(contentType);
    }

    public string ExtractContentType(string httpText)
    {
        if (string.IsNullOrWhiteSpace(httpText))
            return string.Empty;

        var normalized = httpText.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
                break;

            if (!line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line["Content-Type:".Length..].Trim();
            var semi = value.IndexOf(';');
            return semi >= 0 ? value[..semi].Trim().ToLowerInvariant() : value.ToLowerInvariant();
        }

        return string.Empty;
    }
}

internal static class HttpEditorRules
{
    public static void ApplyHighlighting(TextEditor? editor, string httpText, IHttpDocumentEditorRules? rules = null)
    {
        if (editor is null)
            return;
        var impl = rules ?? HttpDocumentEditorRules.Instance;
        editor.SyntaxHighlighting = impl.ResolveDefinition(httpText);
    }
}
