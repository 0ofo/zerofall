using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Services;

public enum SubAgentRunStatus
{
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>子 Agent 工具气泡补丁（须在 UI 线程写入 ChatMessage）。</summary>
public sealed class SubAgentMessagePatch
{
    public required ChatMessage Message { get; init; }
    public bool? IsToolRunning { get; init; }
    public string? ToolOutput { get; init; }
    public int? ToolExitCode { get; init; }
}

/// <summary>内存中的子 Agent 只读会话（不进 SQLite）。</summary>
public sealed class SubAgentLiveSession
{
    internal SubAgentLiveSession(string id, string task, string? parentSessionId)
    {
        Id = id;
        Task = task;
        ParentSessionId = parentSessionId;
        Title = BuildTitle(task, SubAgentRunStatus.Running, null);
    }

    public string Id { get; }
    public string Task { get; }
    public string? ParentSessionId { get; }
    public string Title { get; internal set; }
    public SubAgentRunStatus Status { get; internal set; } = SubAgentRunStatus.Running;
    public List<ChatMessage> Messages { get; } = [];
    internal object Sync { get; } = new();
    internal ChatMessage? StreamingAssistant { get; set; }
    private readonly Dictionary<string, ChatMessage> _toolMessages = new(StringComparer.Ordinal);

    internal bool TryGetToolMessage(string toolCallId, out ChatMessage? message) =>
        _toolMessages.TryGetValue(toolCallId, out message);

    internal void TrackToolMessage(string toolCallId, ChatMessage message)
    {
        if (!string.IsNullOrEmpty(toolCallId))
            _toolMessages[toolCallId] = message;
    }

    internal static string BuildTitle(string task, SubAgentRunStatus status, string? roundHint)
    {
        var core = TruncateTask(task);
        var prefix = status switch
        {
            SubAgentRunStatus.Running => "子 Agent",
            SubAgentRunStatus.Completed => "子 Agent ✓",
            SubAgentRunStatus.Failed => "子 Agent ✗",
            SubAgentRunStatus.Cancelled => "子 Agent · 已取消",
            _ => "子 Agent"
        };
        if (!string.IsNullOrWhiteSpace(roundHint))
            return $"{prefix} · {roundHint} · {core}";
        return $"{prefix} · {core}";
    }

    private static string TruncateTask(string task)
    {
        var t = (task ?? string.Empty).Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return t.Length <= 36 ? t : t[..36] + "…";
    }
}

/// <summary>子 Agent 实时会话注册表；供工具层写入、AiPanelViewModel 展示。</summary>
public sealed class SubAgentSessionHub
{
    public const string IdPrefix = "sub:";

    private readonly ConcurrentDictionary<string, SubAgentLiveSession> _sessions = new(StringComparer.Ordinal);

    public event Action<SubAgentLiveSession>? SessionStarted;
    public event Action<SubAgentLiveSession>? SessionUpdated;
    /// <summary>新一轮 API 请求开始、等待首包时触发（用于骨架屏）。</summary>
    public event Action<SubAgentLiveSession>? RoundWaiting;
    public event Action<SubAgentLiveSession, ChatMessage>? MessageAppended;
    public event Action<SubAgentLiveSession, SubAgentMessagePatch>? MessagePatched;

    public static bool IsSubAgentSessionId(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId)
        && sessionId.StartsWith(IdPrefix, StringComparison.Ordinal);

    public SubAgentLiveSession BeginSession(string task, string? parentSessionId)
    {
        var id = IdPrefix + Guid.NewGuid().ToString("N");
        var session = new SubAgentLiveSession(id, task, parentSessionId);
        lock (session.Sync)
        {
            session.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = task.Trim() });
        }

        _sessions[id] = session;
        SessionStarted?.Invoke(session);
        return session;
    }

    public bool TryGetSession(string sessionId, out SubAgentLiveSession? session) =>
        _sessions.TryGetValue(sessionId, out session);

    public void Clear() => _sessions.Clear();

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public void SetRoundHint(string sessionId, string roundHint)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        lock (session.Sync)
        {
            session.Title = SubAgentLiveSession.BuildTitle(session.Task, session.Status, roundHint);
        }

        SessionUpdated?.Invoke(session);
        SignalRoundWaiting(sessionId);
    }

    public void SignalRoundWaiting(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            RoundWaiting?.Invoke(session);
    }

    public void EnsureAssistantStreaming(string sessionId, bool isThinking)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        ChatMessage? msg;
        lock (session.Sync)
        {
            if (session.StreamingAssistant is not null)
                return;

            msg = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                IsStreaming = true,
                IsThinking = isThinking
            };
            session.Messages.Add(msg);
            session.StreamingAssistant = msg;
        }

        var captured = msg;
        PostUi(() => MessageAppended?.Invoke(session, captured));
    }

    public void AppendAssistantStreamDelta(string sessionId, string? contentDelta, string? reasoningDelta)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        ChatMessage? msg;
        lock (session.Sync)
            msg = session.StreamingAssistant;

        if (msg is null)
            return;

        var content = contentDelta;
        var reasoning = reasoningDelta;
        PostUi(() =>
        {
            if (!string.IsNullOrEmpty(reasoning))
                msg.AppendReasoningText(reasoning!);
            if (!string.IsNullOrEmpty(content))
            {
                if (msg.IsThinking)
                    msg.IsThinking = false;
                msg.AppendStreamingText(content!);
            }

            MessagePatched?.Invoke(session, new SubAgentMessagePatch { Message = msg });
        });
    }

    public void EndAssistantStreaming(string sessionId, bool removeIfEmpty)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        ChatMessage? msg;
        lock (session.Sync)
        {
            msg = session.StreamingAssistant;
            session.StreamingAssistant = null;
        }

        if (msg is null)
            return;

        PostUi(() =>
        {
            msg.IsThinking = false;
            msg.IsStreaming = false;

            if (removeIfEmpty
                && string.IsNullOrWhiteSpace(msg.Content)
                && string.IsNullOrWhiteSpace(msg.ReasoningContent))
            {
                lock (session.Sync)
                    session.Messages.Remove(msg);
            }

            MessagePatched?.Invoke(session, new SubAgentMessagePatch { Message = msg });
        });
    }

    public bool TryBeginStreamingToolCall(string sessionId, string toolCallId, string toolName, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        lock (session.Sync)
        {
            if (!string.IsNullOrEmpty(toolCallId) && session.TryGetToolMessage(toolCallId, out _))
                return false;
        }

        BeginToolCall(sessionId, toolCallId, toolName, argumentsJson);
        return true;
    }

    private static void PostUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void SetStatus(string sessionId, SubAgentRunStatus status)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        List<SubAgentMessagePatch> patches;
        lock (session.Sync)
        {
            session.Status = status;
            session.Title = SubAgentLiveSession.BuildTitle(session.Task, status, null);
            patches = [];
            foreach (var m in session.Messages)
            {
                if (!m.IsToolRunning)
                    continue;

                patches.Add(new SubAgentMessagePatch
                {
                    Message = m,
                    IsToolRunning = false,
                    ToolOutput = string.IsNullOrEmpty(m.ToolOutput)
                        ? status == SubAgentRunStatus.Cancelled ? "已取消" : "已结束"
                        : m.ToolOutput
                });
            }
        }

        foreach (var patch in patches)
            MessagePatched?.Invoke(session, patch);

        SessionUpdated?.Invoke(session);
    }

    public void AppendAssistant(string sessionId, string content, string? reasoning)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(reasoning))
            return;

        ChatMessage msg;
        lock (session.Sync)
        {
            msg = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = content ?? string.Empty,
                ReasoningContent = reasoning ?? string.Empty
            };
            session.Messages.Add(msg);
        }

        MessageAppended?.Invoke(session, msg);
    }

    public void BeginToolCall(string sessionId, string toolCallId, string toolName, string argumentsJson)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        var summary = ToolCallDisplayHelper.FormatCommandSummary(toolName, argumentsJson);
        ChatMessage msg;
        lock (session.Sync)
        {
            msg = new ChatMessage
            {
                Role = ChatRole.Assistant,
                ToolName = toolName,
                ToolCallId = toolCallId,
                ToolArgumentsJson = argumentsJson,
                ToolDisplayName = ToolCallDisplayHelper.GetDisplayName(toolName),
                ToolCommand = string.IsNullOrEmpty(summary) ? argumentsJson : summary,
                IsToolRunning = true
            };
            session.Messages.Add(msg);
            session.TrackToolMessage(toolCallId, msg);
        }

        MessageAppended?.Invoke(session, msg);
    }

    public void CompleteToolCall(string sessionId, string toolCallId, string resultText, int exitCode)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        ChatMessage? msg = null;
        lock (session.Sync)
        {
            if (!string.IsNullOrEmpty(toolCallId))
                session.TryGetToolMessage(toolCallId, out msg);
            if (msg is null)
            {
                for (var i = session.Messages.Count - 1; i >= 0; i--)
                {
                    var candidate = session.Messages[i];
                    if (candidate.IsToolRunning)
                    {
                        msg = candidate;
                        break;
                    }
                }
            }
        }

        if (msg is null)
            return;

        MessagePatched?.Invoke(session, new SubAgentMessagePatch
        {
            Message = msg,
            IsToolRunning = false,
            ToolOutput = resultText ?? string.Empty,
            ToolExitCode = exitCode
        });
    }

    public void AppendFailureNotice(string sessionId, string error)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        ChatMessage msg;
        lock (session.Sync)
        {
            msg = new ChatMessage { Role = ChatRole.Assistant, Content = error };
            session.Messages.Add(msg);
        }

        MessageAppended?.Invoke(session, msg);
    }

    public void AppendUserMessage(string sessionId, string content)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        ChatMessage msg;
        lock (session.Sync)
        {
            msg = new ChatMessage { Role = ChatRole.User, Content = content };
            session.Messages.Add(msg);
        }

        MessageAppended?.Invoke(session, msg);
    }
}
