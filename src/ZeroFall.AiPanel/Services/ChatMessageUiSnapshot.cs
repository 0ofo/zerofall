using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZeroFall.AiPanel.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Services;

/// <summary>从 UI 上的 <see cref="ChatMessage"/> 摘取只读快照，供后台格式化/持久化/统计。</summary>
internal readonly record struct ChatMessageUiSnapshot(
    bool IsUser,
    bool HasToolCall,
    bool IsThinking,
    bool HasReasoning,
    bool IsStreaming,
    string Content,
    string ReasoningContent,
    string ToolName,
    string ToolDisplayName,
    string ToolArgumentsJson,
    string ToolOutput,
    bool IsToolRunning,
    int ToolExitCode)
{
    public static ChatMessageUiSnapshot From(ChatMessage message) =>
        new(
            message.IsUser,
            message.HasToolCall,
            message.IsThinking,
            message.HasReasoning,
            message.IsStreaming,
            message.Content,
            message.ReasoningContent,
            message.ToolName,
            message.ToolDisplayName,
            message.ToolArgumentsJson,
            message.ToolOutput,
            message.IsToolRunning,
            message.ToolExitCode);

    public static async Task<ChatMessageUiSnapshot[]?> CaptureArrayAsync(
        Func<IReadOnlyList<ChatMessage>> messageSource)
    {
        ChatMessage[]? refs;
        try
        {
            refs = await UiThreadBridge.InvokeAsync(() => messageSource().ToArray()).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (refs is null or { Length: 0 })
            return refs is { Length: 0 } ? [] : null;

        return await Task.Run(() =>
        {
            var rows = new ChatMessageUiSnapshot[refs.Length];
            for (var i = 0; i < refs.Length; i++)
                rows[i] = From(refs[i]);
            return rows;
        }).ConfigureAwait(false);
    }

    public static async Task<List<ChatMessage>?> CaptureMessageListAsync(
        Func<IReadOnlyList<ChatMessage>> messageSource)
    {
        ChatMessage[]? refs;
        try
        {
            refs = await UiThreadBridge.InvokeAsync(() => messageSource().ToArray()).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (refs is null)
            return null;

        return await Task.Run(() => new List<ChatMessage>(refs)).ConfigureAwait(false);
    }
}
