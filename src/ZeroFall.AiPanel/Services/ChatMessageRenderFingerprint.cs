using System.Collections.Generic;
using System.Text;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Services;

internal static class ChatMessageRenderFingerprint
{
    public static string ForMessages(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder(messages.Count * 24);
        foreach (var message in messages)
        {
            sb.Append(ChatMessageIds.UiId(message)).Append('|')
                .Append(message.Content.Length).Append('|')
                .Append(message.ReasoningContent.Length).Append('|')
                .Append(message.IsStreaming ? '1' : '0').Append('|')
                .Append(message.IsThinking ? '1' : '0').Append('|')
                .Append(message.IsToolRunning ? '1' : '0').Append(';');
        }

        return sb.ToString();
    }
}
