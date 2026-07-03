using System;
using System.Collections.Generic;
using AvaloniaEdit;

namespace ZeroFall.Browser.Services;

public enum HttpRequestEditorMenuScope
{
    None,
    Replay,
    Intruder
}

public sealed class HttpRequestEditorMenuContext
{
    public required TextEditor Editor { get; init; }

    public HttpRequestEditorMenuScope Scope { get; init; }

    public bool IsReadOnly { get; init; }

    public required Action<string> SetRequestText { get; init; }

    public string RequestText => Editor.Text ?? string.Empty;

    public bool HasSelection
    {
        get
        {
            var (from, length) = GetSelectionRange(Editor);
            return length > 0;
        }
    }

    public string GetSelectedText()
    {
        var ta = Editor.TextArea;
        var text = ta.Selection.GetText();
        return string.IsNullOrEmpty(text) ? string.Empty : text;
    }

    public void ReplaceSelection(string newText)
    {
        var ta = Editor.TextArea;
        var (from, length) = GetSelectionRange(Editor);
        if (length <= 0)
            return;

        ta.Document.Replace(from, length, newText);
        SetRequestText(Editor.Text ?? string.Empty);
    }

    private static (int From, int Length) GetSelectionRange(TextEditor editor)
    {
        var ta = editor.TextArea;
        var doc = ta.Document;
        var start = ta.Selection.StartPosition;
        var end = ta.Selection.EndPosition;
        var startOffset = doc.GetOffset(start.Line, start.Column);
        var endOffset = doc.GetOffset(end.Line, end.Column);
        if (startOffset <= endOffset)
            return (startOffset, endOffset - startOffset);

        return (endOffset, startOffset - endOffset);
    }
}

public sealed class HttpRequestEditorMenuDescriptor
{
    public string? Header { get; init; }

    public bool IsSeparator => Header is null;

    public bool IsEnabled { get; init; } = true;

    public IReadOnlyList<HttpRequestEditorMenuDescriptor>? Children { get; init; }

    public Action<HttpRequestEditorMenuContext>? Execute { get; init; }
}

public interface IHttpRequestEditorMenuContributor
{
    void Contribute(HttpRequestEditorMenuContext context, IList<HttpRequestEditorMenuDescriptor> items);
}

public sealed class HttpRequestEditorMenuRegistry
{
    public static HttpRequestEditorMenuRegistry Instance { get; } = new();

    private readonly List<IHttpRequestEditorMenuContributor> _contributors = [];

    public void Register(IHttpRequestEditorMenuContributor contributor) =>
        _contributors.Add(contributor);

    public IReadOnlyList<HttpRequestEditorMenuDescriptor> GetItems(HttpRequestEditorMenuContext context)
    {
        var items = new List<HttpRequestEditorMenuDescriptor>();
        foreach (var contributor in _contributors)
            contributor.Contribute(context, items);

        return items;
    }
}
