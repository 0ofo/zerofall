using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeroFall.AiPanel.Services;

/// <summary>Markdig 渲染引擎（仅用于后台队列，禁止在 UI 线程调用）。</summary>
internal static class MarkdownRenderEngine
{
    public static List<RenderedMarkdownBlock> RenderSnapshot(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var streamer = new MarkdigBlockStreamer();
        var blocks = streamer.CommitSnapshot(markdown).ToList();
        if (blocks.Count > 0)
            return blocks;

        return [PlainTextBlock(markdown)];
    }

    private static RenderedMarkdownBlock PlainTextBlock(string text)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var html = string.Join("<br/>", encoded.Split('\n'));
        return new RenderedMarkdownBlock("b0", $"<p>{html}</p>");
    }
}
