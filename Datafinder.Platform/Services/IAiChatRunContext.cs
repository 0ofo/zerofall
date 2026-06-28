using System;
using System.Threading;

namespace Datafinder.Platform.Services;

/// <summary>当前 AI 对话轮次的取消令牌与请求参数（用户点「停止」时传播到工具与子 Agent）。</summary>
public interface IAiChatRunContext
{
    CancellationToken CancellationToken { get; }

    /// <summary>与主会话 UI 一致：是否请求 reasoning_content。</summary>
    bool EnableThinking { get; }

    /// <summary>与主会话 UI 一致：当前选中的模型 id。</summary>
    string? ModelOverride { get; }

    IDisposable BeginScope(CancellationToken cancellationToken, bool enableThinking = false, string? modelOverride = null);
}
