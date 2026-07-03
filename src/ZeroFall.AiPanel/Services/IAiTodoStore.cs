using System.Threading.Tasks;

namespace ZeroFall.AiPanel.Services;

/// <summary>AI 聊天待办（按会话隔离）持久化：单条 markdown 文本。</summary>
public interface IAiTodoStore
{
    /// <summary>读取指定会话的待办 markdown 文本；不存在返回空字符串。</summary>
    Task<string> GetMarkdownAsync(string sessionId);

    /// <summary>整体替换指定会话的待办 markdown 文本。</summary>
    Task SaveMarkdownAsync(string sessionId, string markdown);
}
