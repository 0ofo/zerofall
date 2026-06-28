using System.Threading;

namespace ZeroFall.Platform.Services;

/// <summary>当前 AI 会话上下文。供工具服务读取当前会话 id（不依赖 ViewModel）。</summary>
public interface IAiChatSessionContext
{
    /// <summary>当前活动会话 id；无活动会话时为 null。</summary>
    string? CurrentSessionId { get; }
}

/// <summary>可写的会话上下文。由 AiPanelViewModel 设置。</summary>
public sealed class AiChatSessionContext : IAiChatSessionContext
{
    private string? _sessionId;

    public string? CurrentSessionId => _sessionId;

    public void SetSessionId(string? sessionId) => _sessionId = sessionId;
}
