using System.Collections.Generic;
using System.Linq;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Services;

/// <summary>会话级内存快照协调器，集中管理消息、API 起始 message id 与 token 状态的拷贝边界。</summary>
public sealed class ChatSessionCoordinator
{
    public ChatSessionSnapshot Capture(
        IEnumerable<ChatMessage> messages,
        long apiStartMessageId,
        SessionTokenUsageState? tokenUsage) =>
        new()
        {
            Messages = messages.ToList(),
            ApiStartMessageId = apiStartMessageId,
            TokenUsage = ChatContextUsageService.CloneTokenUsageState(tokenUsage)
        };

    public void Update(
        ChatSessionSnapshot snapshot,
        IEnumerable<ChatMessage> messages,
        long apiStartMessageId,
        SessionTokenUsageState? tokenUsage)
    {
        snapshot.Messages.Clear();
        snapshot.Messages.AddRange(messages);
        snapshot.ApiStartMessageId = apiStartMessageId;
        snapshot.TokenUsage = ChatContextUsageService.CloneTokenUsageState(tokenUsage);
    }
}

public sealed class ChatSessionSnapshot
{
    public List<ChatMessage> Messages { get; set; } = [];
    public long ApiStartMessageId { get; set; }
    public SessionTokenUsageState? TokenUsage { get; set; }
}
