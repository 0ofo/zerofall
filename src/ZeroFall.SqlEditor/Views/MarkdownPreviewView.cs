using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using LiveMarkdown.Avalonia;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;

namespace ZeroFall.SqlEditor.Views;

/// <summary>Markdown 文件渲染预览（LiveMarkdown.Avalonia）。</summary>
public sealed class MarkdownPreviewView : UserControl
{
    private readonly MarkdownRenderer _renderer;
    private readonly ObservableStringBuilder _builder = new();
    private IEventBus? _eventBus;

    public MarkdownPreviewView()
    {
        _renderer = new MarkdownRenderer
        {
            MarkdownBuilder = _builder,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        _renderer.LinkClick += OnLinkClick;

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Border
            {
                Padding = new Thickness(12, 8, 16, 16),
                Child = _renderer
            }
        };
    }

    public void SetEventBus(IEventBus? eventBus) => _eventBus = eventBus;

    public void ReleaseTabResources()
    {
        _builder.Clear();
        _renderer.ImageBasePath = string.Empty;
    }

    public void ShowDocument(string markdown, string? title, string? sourceFilePath = null)
    {
        _ = title;

        var basePath = string.IsNullOrWhiteSpace(sourceFilePath)
            ? string.Empty
            : Path.GetDirectoryName(Path.GetFullPath(sourceFilePath)) ?? string.Empty;
        _renderer.ImageBasePath = basePath;

        _builder.Clear();
        _builder.Append(string.IsNullOrEmpty(markdown) ? "（空文档）" : markdown);
    }

    private void OnLinkClick(object? sender, LinkClickedEventArgs e)
    {
        var uri = e.HRef;
        if (uri is null)
            return;

        if (!uri.IsAbsoluteUri && !string.IsNullOrEmpty(_renderer.ImageBasePath))
        {
            try
            {
                uri = new Uri(new Uri(_renderer.ImageBasePath + Path.DirectorySeparatorChar), uri);
            }
            catch
            {
                return;
            }
        }

        if (!uri.IsAbsoluteUri)
            return;

        OpenExternalLink(uri);
    }

    private void OpenExternalLink(Uri uri)
    {
        var scheme = uri.Scheme;
        if (scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("tel", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            }
            catch
            {
            }

            return;
        }

        if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            _eventBus?.Publish(new OpenBrowserTabRequestedEvent(uri.ToString()));
            return;
        }

        if (scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.LocalPath) { UseShellExecute = true });
            }
            catch
            {
            }
        }
    }
}
