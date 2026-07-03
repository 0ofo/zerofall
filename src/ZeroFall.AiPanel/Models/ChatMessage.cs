using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LiveMarkdown.Avalonia;
using ZeroFall.AiPanel.Services;
using ZeroFall.Base.AiTools;

namespace ZeroFall.AiPanel.Models;

public enum ChatRole
{
    User,
    Assistant,
    System
}

public class ChatMessage : INotifyPropertyChanged
{
    /// <summary>SQLite 全局自增主键；0 表示尚未落库。</summary>
    public long Id { get; set; }

    /// <summary>落库前 WebView 临时键。</summary>
    internal string PendingUiKey { get; } = Guid.NewGuid().ToString("N");

    internal IChatTranscriptSink? TranscriptSink { get; set; }

    public ChatRole Role { get; init; }
    public bool IsUser => Role == ChatRole.User;
    public string RoleName => Role == ChatRole.User ? "我" : "AI";

    public bool ShowAssistantMarkdown => !IsUser && !HasToolCall && !string.IsNullOrWhiteSpace(_content);

    public bool HasVisibleAssistantShell =>
        HasToolCall || HasReasoning || _isStreaming || !string.IsNullOrWhiteSpace(_content);

    public int? Tag { get; set; }

    /// <summary>到当前消息为止，本会话实际上下文的 token 总量。</summary>
    public int ContextTokenCount { get; set; }

    /// <summary>UI 可见性；<see cref="ChatMessageVisual.Hidden"/> 不参与展示，仍进 API。</summary>
    public ChatMessageVisual Visual { get; set; } = ChatMessageVisual.Visible;

    /// <summary>仅 UI shell：正文/工具载荷按需从 DB hydrate。</summary>
    public bool IsArchiveShell { get; set; }

    /// <summary>关联的子 Agent 只读会话 id（spawn_agent 工具卡片内联预览用）。</summary>
    private string? _linkedSubAgentSessionId;
    public string? LinkedSubAgentSessionId
    {
        get => _linkedSubAgentSessionId;
        set
        {
            if (_linkedSubAgentSessionId == value)
                return;
            _linkedSubAgentSessionId = value;
            OnPropertyChanged();
            NotifyToolHeaderChanged();
        }
    }

    private string _content = string.Empty;
    public string Content
    {
        get => _content;
        set
        {
            if (_content == value)
                return;

            _content = value ?? string.Empty;
            ResetBuilder(_contentBuilder, _content);
            InvalidateApiTokenCache();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowAssistantMarkdown));
            OnPropertyChanged(nameof(HasVisibleAssistantShell));
            TranscriptSink?.MarkDirty();
        }
    }

    private string _reasoningContent = string.Empty;
    public string ReasoningContent
    {
        get => _reasoningContent;
        set
        {
            if (_reasoningContent == value)
                return;

            _reasoningContent = value ?? string.Empty;
            ResetBuilder(_reasoningBuilder, _reasoningContent);
            InvalidateApiTokenCache();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReasoning));
            OnPropertyChanged(nameof(ShowReasoningSection));
            OnPropertyChanged(nameof(ThinkingLabel));
            TranscriptSink?.MarkDirty();
        }
    }

    public bool HasReasoning => !string.IsNullOrEmpty(_reasoningContent);

    /// <summary>思考折叠区是否展示（有思考内容或正在思考）。</summary>
    public bool ShowReasoningSection => HasReasoning || _isThinking;

    // LiveMarkdown 流式渲染：ObservableStringBuilder 只支持 Append/Clear，
    // 因此按需惰性创建（首次访问时用当前全文种子），流式增量走 AppendStreamingText 追加。
    private ObservableStringBuilder? _contentBuilder;
    public ObservableStringBuilder ContentBuilder => _contentBuilder ??= CreateBuilder(_content);

    private ObservableStringBuilder? _reasoningBuilder;
    public ObservableStringBuilder ReasoningContentBuilder => _reasoningBuilder ??= CreateBuilder(_reasoningContent);

    private static ObservableStringBuilder CreateBuilder(string seed)
    {
        var builder = new ObservableStringBuilder();
        if (!string.IsNullOrEmpty(seed))
            builder.Append(seed);
        return builder;
    }

    private static void ResetBuilder(ObservableStringBuilder? builder, string value)
    {
        if (builder is null)
            return;
        builder.Clear();
        if (!string.IsNullOrEmpty(value))
            builder.Append(value);
    }

    public string ThinkingLabel => "思考";

    public bool ShowThinkingBulb => !_isThinking;

    private bool _isThinking;
    public bool IsThinking
    {
        get => _isThinking;
        set
        {
            if (_isThinking == value)
                return;

            _isThinking = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThinkingLabel));
            OnPropertyChanged(nameof(ShowThinkingBulb));
            OnPropertyChanged(nameof(ShowReasoningSection));
            TranscriptSink?.MarkDirty();
        }
    }

    private bool _isReasoningExpanded;
    public bool IsReasoningExpanded
    {
        get => _isReasoningExpanded;
        set
        {
            if (_isReasoningExpanded == value)
                return;

            _isReasoningExpanded = value;
            OnPropertyChanged();
        }
    }

    private bool _isStreaming;
    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (_isStreaming == value)
                return;

            _isStreaming = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasVisibleAssistantShell));
            TranscriptSink?.MarkDirty();
        }
    }

    public void AppendStreamingText(string newText)
    {
        if (string.IsNullOrEmpty(newText))
            return;

        _content += newText;
        _contentBuilder?.Append(newText);
        InvalidateApiTokenCache();
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(ShowAssistantMarkdown));
        OnPropertyChanged(nameof(HasVisibleAssistantShell));
        TranscriptSink?.MarkDirty();
    }

    public void AppendReasoningText(string newText)
    {
        if (string.IsNullOrEmpty(newText))
            return;

        var hadReasoning = HasReasoning;
        _reasoningContent += newText;
        _reasoningBuilder?.Append(newText);
        if (!hadReasoning)
        {
            OnPropertyChanged(nameof(HasReasoning));
            OnPropertyChanged(nameof(ShowReasoningSection));
        }

        InvalidateApiTokenCache();
        OnPropertyChanged(nameof(ReasoningContent));
        TranscriptSink?.MarkDirty();
    }

    public void FlushStreamingMarkdown()
    {
        TranscriptSink?.MarkDirty();
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(ReasoningContent));
    }

    private string _toolName = string.Empty;
    public string ToolName
    {
        get => _toolName;
        set
        {
            if (_toolName == value)
                return;

            _toolName = value;
            InvalidateApiTokenCache();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasToolCall));
            OnPropertyChanged(nameof(ShowAssistantMarkdown));
            NotifyToolHeaderChanged();
            TranscriptSink?.MarkDirty();
        }
    }

    private string _toolCallId = string.Empty;

    public string ToolCallId
    {
        get => _toolCallId;
        set
        {
            if (_toolCallId == value)
                return;

            _toolCallId = value;
            OnPropertyChanged();
        }
    }

    private string _toolArgumentsJson = string.Empty;

    public string ToolArgumentsJson
    {
        get => _toolArgumentsJson;
        set
        {
            if (_toolArgumentsJson == value)
                return;

            _toolArgumentsJson = value ?? string.Empty;
            InvalidateApiTokenCache();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToolArgumentsDisplay));
            if (!_isToolRunning)
                TranscriptSink?.MarkDirty();
        }
    }

    /// <summary>工具参数的可读展示。</summary>
    public string ToolArgumentsDisplay => ToolResultJson.FormatForDisplay(_toolArgumentsJson);

    private string _toolCommand = string.Empty;
    public string ToolCommand
    {
        get => _toolCommand;
        set
        {
            if (_toolCommand == value)
                return;

            _toolCommand = value;
            InvalidateApiTokenCache();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasToolCall));
        }
    }

    private string _toolOutput = string.Empty;
    public string ToolOutput
    {
        get => _toolOutput;
        set
        {
            if (_toolOutput == value)
                return;

            _toolOutput = value ?? string.Empty;
            InvalidateApiTokenCache();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasToolOutput));
            OnPropertyChanged(nameof(ToolOutputDisplay));
            NotifyToolHeaderChanged();
            TranscriptSink?.MarkDirty();
        }
    }

    /// <summary>工具返回结果的可读展示（JSON pretty-print，CJK 不转义）。</summary>
    public string ToolOutputDisplay => ToolResultJson.FormatForDisplay(_toolOutput);

    private bool _isToolExpanded;
    public bool IsToolExpanded
    {
        get => _isToolExpanded;
        set
        {
            if (_isToolExpanded == value)
                return;

            _isToolExpanded = value;
            OnPropertyChanged();
        }
    }

    private bool _isToolRunning;
    public bool IsToolRunning
    {
        get => _isToolRunning;
        set
        {
            if (_isToolRunning == value)
                return;

            _isToolRunning = value;
            InvalidateApiTokenCache();
            OnPropertyChanged();
            NotifyToolHeaderChanged();
            TranscriptSink?.MarkDirty();
        }
    }

    private int _toolExitCode;
    public int ToolExitCode
    {
        get => _toolExitCode;
        set
        {
            if (_toolExitCode == value)
                return;

            _toolExitCode = value;
            OnPropertyChanged();
            NotifyToolHeaderChanged();
            TranscriptSink?.MarkDirty();
        }
    }

    public bool HasToolCall => !string.IsNullOrEmpty(_toolName);
    public bool HasToolOutput => !string.IsNullOrEmpty(_toolOutput);

    private string _toolDisplayName = string.Empty;

    public string ToolDisplayName
    {
        get => string.IsNullOrEmpty(_toolDisplayName) ? _toolName : _toolDisplayName;
        set
        {
            if (_toolDisplayName == value)
                return;

            _toolDisplayName = value;
            OnPropertyChanged();
            NotifyToolHeaderChanged();
        }
    }

    /// <summary>工具卡片标题（不含状态徽标）。</summary>
    public string ToolCallHeaderText
    {
        get
        {
            var label = ToolDisplayName;
            if (_isToolRunning)
            {
                if (!string.IsNullOrEmpty(_linkedSubAgentSessionId))
                    return $"{label} · 执行中… · 展开预览";
                return $"{label} · 执行中…";
            }
            if (HasToolOutput)
            {
                if (!string.IsNullOrEmpty(_linkedSubAgentSessionId))
                    return $"{label} · 子 Agent 预览";
                return label;
            }

            if (!string.IsNullOrEmpty(_linkedSubAgentSessionId))
                return $"{label} · 子 Agent 预览";

            return label;
        }
    }

    /// <summary>WebView 等仍绑定此属性；与 <see cref="ToolCallHeaderText"/> 一致。</summary>
    public string ToolCallLabel => ToolCallHeaderText;

    public bool ToolStatusLoading => _isToolRunning;

    public bool ToolStatusSuccess =>
        !_isToolRunning && HasToolOutput && _toolExitCode == 0;

    public bool ToolStatusFailed =>
        !_isToolRunning && HasToolOutput && _toolExitCode != 0;

    private void NotifyToolHeaderChanged()
    {
        OnPropertyChanged(nameof(ToolCallHeaderText));
        OnPropertyChanged(nameof(ToolCallLabel));
        OnPropertyChanged(nameof(ToolStatusLoading));
        OnPropertyChanged(nameof(ToolStatusSuccess));
        OnPropertyChanged(nameof(ToolStatusFailed));
    }

    /// <summary>将 DB 按需加载的完整消息合并进 archive shell（原地更新，保持 UI 绑定引用）。</summary>
    internal void ApplyArchiveHydration(ChatMessage source)
    {
        if (!IsArchiveShell || source.IsArchiveShell)
            return;

        Id = source.Id;
        Visual = source.Visual;
        ContextTokenCount = source.ContextTokenCount;
        Content = source.Content;
        ReasoningContent = source.ReasoningContent;
        ToolName = source.ToolName;
        ToolCommand = source.ToolCommand;
        ToolArgumentsJson = source.ToolArgumentsJson;
        ToolOutput = source.ToolOutput;
        ToolExitCode = source.ToolExitCode;
        ToolCallId = source.ToolCallId;
        ToolDisplayName = source.ToolDisplayName;
        LinkedSubAgentSessionId = source.LinkedSubAgentSessionId;
        IsArchiveShell = false;
        InvalidateApiTokenCache();
    }

    public string ContentHtml { get; private set; } = string.Empty;

    public string ReasoningHtml { get; private set; } = string.Empty;

    internal void ApplyRenderedHtml(bool reasoning, string html)
    {
        if (reasoning)
            ReasoningHtml = html ?? string.Empty;
        else
            ContentHtml = html ?? string.Empty;
    }

    internal void RestoreRenderedHtml(string html) => ContentHtml = html ?? string.Empty;

    private int _cachedApiTokens = -1;
    private string? _cachedApiTokensModelId;

    internal void InvalidateApiTokenCache()
    {
        _cachedApiTokens = -1;
        _cachedApiTokensModelId = null;
    }

    internal bool TryGetCachedApiTokens(string modelId, out int tokens)
    {
        if (_cachedApiTokens >= 0
            && string.Equals(_cachedApiTokensModelId, modelId, StringComparison.OrdinalIgnoreCase))
        {
            tokens = _cachedApiTokens;
            return true;
        }

        tokens = 0;
        return false;
    }

    internal void SetCachedApiTokens(string modelId, int tokens)
    {
        _cachedApiTokens = tokens;
        _cachedApiTokensModelId = modelId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
