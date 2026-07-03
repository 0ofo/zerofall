using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Markdig;
using Markdig.Syntax;

namespace ZeroFall.AiPanel.Services;

/// <summary>流式/全量 Markdown → HTML 块（SSE 增量 Append，结束 Finish/CommitSnapshot）。</summary>
public sealed class MarkdigBlockStreamer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly StringBuilder _buffer = new();
    private int _nextBlockId;

    public string CurrentTailMarkdown => _buffer.ToString();

    public void Reset()
    {
        _buffer.Clear();
        _nextBlockId = 0;
    }

    public MarkdigStreamUpdate Append(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return new MarkdigStreamUpdate([], CurrentTailMarkdown);

        _buffer.Append(delta);
        if (!ContainsLineBreak(delta))
            return new MarkdigStreamUpdate([], CurrentTailMarkdown);

        var blocks = RenderAvailableBlocks(final: false);
        if (blocks.Count == 0)
            return new MarkdigStreamUpdate([], CurrentTailMarkdown);

        return new MarkdigStreamUpdate(blocks, CurrentTailMarkdown);
    }

    public MarkdigStreamFinishResult Finish()
    {
        if (_buffer.Length == 0)
            return new MarkdigStreamFinishResult([]);

        return new MarkdigStreamFinishResult(RenderAvailableBlocks(final: true));
    }

    public IReadOnlyList<RenderedMarkdownBlock> CommitSnapshot(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        Reset();
        _buffer.Append(markdown);
        return RenderAvailableBlocks(final: true);
    }

    private List<RenderedMarkdownBlock> RenderAvailableBlocks(bool final)
    {
        var rendered = new List<RenderedMarkdownBlock>();
        if (_buffer.Length == 0)
            return rendered;

        var markdown = _buffer.ToString();
        if (final && string.IsNullOrWhiteSpace(markdown))
        {
            _buffer.Clear();
            return rendered;
        }

        var consumedCharCount = 0;
        var document = Markdown.Parse(markdown, Pipeline);
        var topBlocks = document.ToList();
        for (var i = 0; i < topBlocks.Count; i++)
        {
            var block = topBlocks[i];
            var blockEnd = GetSourceEnd(markdown, block);
            if (blockEnd <= consumedCharCount)
                continue;

            var isLastBlock = i == topBlocks.Count - 1;
            if (!final && isLastBlock)
                break;

            var sourceEnd = final
                ? (isLastBlock ? markdown.Length : blockEnd)
                : IncludeTrailingLineEndings(markdown, blockEnd);
            if (sourceEnd <= consumedCharCount)
                continue;

            var segment = markdown[consumedCharCount..sourceEnd];
            var html = Markdown.ToHtml(segment, Pipeline);
            consumedCharCount = sourceEnd;

            if (string.IsNullOrWhiteSpace(html))
                continue;

            rendered.Add(new RenderedMarkdownBlock($"b{_nextBlockId++}", html));
        }

        if (consumedCharCount > 0)
            _buffer.Remove(0, consumedCharCount);

        return rendered;
    }

    private static bool ContainsLineBreak(string text) =>
        text.AsSpan().IndexOfAny('\r', '\n') >= 0;

    private static int GetSourceEnd(string markdown, Block block)
    {
        if (block.Span.End < 0)
            return 0;

        return Math.Clamp(block.Span.End + 1, 0, markdown.Length);
    }

    private static int IncludeTrailingLineEndings(string markdown, int index)
    {
        while (index < markdown.Length)
        {
            var ch = markdown[index];
            if (ch is ' ' or '\t')
            {
                var probe = index;
                while (probe < markdown.Length && markdown[probe] is ' ' or '\t')
                    probe++;
                if (probe < markdown.Length && markdown[probe] is '\r' or '\n')
                {
                    index = probe;
                    continue;
                }

                break;
            }

            if (ch == '\r')
            {
                index++;
                if (index < markdown.Length && markdown[index] == '\n')
                    index++;
                continue;
            }

            if (ch == '\n')
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }
}
