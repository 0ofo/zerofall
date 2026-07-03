using System.Collections.Generic;
using System.Threading.Tasks;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Services;

/// <summary>AI 聊天会话 SQLite 持久化。</summary>
public interface IAiChatSessionStore
{
    /// <summary>列出所有会话摘要（按 sort_order 升序）。</summary>
    Task<IReadOnlyList<ChatSessionSummary>> ListSessionsAsync();

    /// <summary>加载会话完整消息。</summary>
    Task<ChatSessionData?> LoadSessionAsync(string sessionId);

    /// <summary>会话可见消息 shell（不含大段正文/工具 JSON）。</summary>
    Task<IReadOnlyList<ChatMessage>> LoadVisibleShellsAsync(string sessionId);

    /// <summary>按可见消息下标范围加载完整消息（skip/take 作用于 UI 可见序列）。</summary>
    Task<IReadOnlyList<ChatMessage>> LoadVisibleMessagesRangeAsync(string sessionId, int skipVisible, int takeVisible);

    /// <summary>可见 UI 消息总数（不含 hidden）。</summary>
    Task<int> GetVisibleMessageCountAsync(string sessionId);

    /// <summary>会话标题与 API 起始 message id / token 元数据（不加载消息正文）。</summary>
    Task<ChatSessionHeader?> LoadSessionHeaderAsync(string sessionId);

    /// <summary>创建空会话，返回新 id。</summary>
    Task<string> CreateSessionAsync(string? title);

    /// <summary>更新会话元数据（标题、api_start、token 用量）；不触碰消息行。</summary>
    Task UpdateSessionMetadataAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messagesForTitle,
        long apiStartMessageId,
        SessionTokenUsageState? tokenUsage);

    /// <summary>撤销：删除 session 内 id ≥ 指定值的已落库消息。</summary>
    Task TruncateSessionMessagesAsync(string sessionId, long fromMessageIdInclusive);

    /// <summary>追加已稳定消息（不 DELETE 旧行）；回写全局 id 并返回首条新 id。</summary>
    Task<AppendMessagesResult> AppendStableMessagesAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        long? apiStartMessageId = null,
        SessionTokenUsageState? tokenUsage = null);

    /// <summary>按全局 id 更新已落库消息正文（工具结果等）。</summary>
    Task UpdatePersistedMessageAsync(string sessionId, ChatMessage message);

    /// <summary>重命名会话。</summary>
    Task RenameSessionAsync(string sessionId, string title);

    /// <summary>删除会话及其消息。</summary>
    Task DeleteSessionAsync(string sessionId);
}

public sealed class ChatSessionHeader
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    /// <summary>API 载荷起始 message id（含）；0 表示未压缩，从首条或按 token 预算裁剪。</summary>
    public long ApiStartMessageId { get; init; }
    public SessionTokenUsageState? TokenUsage { get; init; }
}

public sealed class ChatSessionSummary
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string UpdatedAtUtc { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    /// <summary>子 Agent 只读会话（内存，非 SQLite）。</summary>
    public bool IsSubAgent { get; init; }
    /// <summary>子 Agent 是否仍在执行。</summary>
    public bool IsRunning { get; init; }
}

public sealed class ChatSessionData
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public List<ChatMessageDto> Messages { get; init; } = [];
    public long ApiStartMessageId { get; init; }
    public SessionTokenUsageState? TokenUsage { get; init; }
}

public sealed class AppendMessagesResult
{
    public long FirstMessageId { get; init; } = -1;
    public int MessageCount { get; init; }
    /// <summary>若追加内容含 tool 消息，为其 id；否则 -1。</summary>
    public long ToolMessageId { get; init; } = -1;
}
