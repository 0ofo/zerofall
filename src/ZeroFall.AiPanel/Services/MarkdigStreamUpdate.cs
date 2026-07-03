using System.Collections.Generic;

namespace ZeroFall.AiPanel.Services;

public sealed class MarkdigStreamUpdate
{
    public static MarkdigStreamUpdate Empty { get; } = new([], string.Empty);

    public MarkdigStreamUpdate(IReadOnlyList<RenderedMarkdownBlock> appendBlocks, string tailMarkdown)
    {
        AppendBlocks = appendBlocks;
        TailMarkdown = tailMarkdown;
    }

    public IReadOnlyList<RenderedMarkdownBlock> AppendBlocks { get; }
    public string TailMarkdown { get; }
}

public sealed class MarkdigStreamFinishResult
{
    public MarkdigStreamFinishResult(IReadOnlyList<RenderedMarkdownBlock> appendBlocks)
    {
        AppendBlocks = appendBlocks;
    }

    public IReadOnlyList<RenderedMarkdownBlock> AppendBlocks { get; }
}
