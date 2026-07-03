using System;
using System.IO;

namespace ZeroFall.SqlEditor.Services;

/// <summary>Markdown 文件类型判定。</summary>
public static class MarkdownDocumentHtmlRenderer
{
    public static bool IsMarkdownFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }
}
