using System;
using System.Collections.Generic;

namespace ZeroFall.AiPanel.Services;

public sealed class ChatMarkdownRenderRequest
{
    public required string MessageUiId { get; init; }
    public required bool Reasoning { get; init; }
    public required string Markdown { get; init; }
    public required Action<ChatMarkdownRenderResult> OnCompleted { get; init; }
}

public sealed class ChatMarkdownRenderResult
{
    public required string MessageUiId { get; init; }
    public required bool Reasoning { get; init; }
    public required IReadOnlyList<RenderedMarkdownBlock> Blocks { get; init; }
    public required string Html { get; init; }
}

public interface IChatMarkdownRenderQueue
{
    void Enqueue(ChatMarkdownRenderRequest request);
}
