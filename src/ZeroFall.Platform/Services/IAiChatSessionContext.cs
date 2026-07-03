using System;
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
    private readonly AsyncLocal<string?> _runSessionId = new();
    private string? _focusedSessionId;

    public string? CurrentSessionId => _runSessionId.Value ?? _focusedSessionId;

    public void SetSessionId(string? sessionId) => _focusedSessionId = sessionId;

    public IDisposable BeginSessionScope(string? sessionId)
    {
        var previous = _runSessionId.Value;
        _runSessionId.Value = sessionId;
        return new Scope(this, previous);
    }

    private sealed class Scope(AiChatSessionContext owner, string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner._runSessionId.Value = previous;
        }
    }
}
