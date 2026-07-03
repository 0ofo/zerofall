using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.AiPanel.Models;
using ZeroFall.AiPanel.Services;
using ZeroFall.Base.AiTools;
using ZeroFall.Base.Diagnostics;
using ZeroFall.AiPanel.Tools.Builtin;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.ViewModels;

public sealed class ChatMessagesTruncatedEventArgs(string fromMessageUiId) : EventArgs
{
    public string FromMessageUiId { get; } = fromMessageUiId;
}

public sealed class ChatMessageUiIdRemappedEventArgs(string fromMessageUiId, string toMessageUiId) : EventArgs
{
    public string FromMessageUiId { get; } = fromMessageUiId;
    public string ToMessageUiId { get; } = toMessageUiId;
}

public partial class AiPanelViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isPanelVisible = true;

    [ObservableProperty]
    private bool _isConfigured;

    [ObservableProperty]
    private string _configuredModel = string.Empty;

    [ObservableProperty]
    private string _configuredBaseUrl = string.Empty;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private bool _isCompressingContext;

    [ObservableProperty]
    private double _contextTokenUsagePercent;

    [ObservableProperty]
    private string _contextTokenUsageDetails = string.Empty;

    public bool IsNotSending => !IsSending;

    public bool CanUseInput => IsConfigured && !IsReadOnlySession;

    public bool CanRevertMessages => !IsReadOnlySession;

    public string SendOrStopToolTip =>
        IsCompressingContext ? "正在压缩上下文…" : IsSending ? "停止" : "发送";

    public bool IsStopEnabled => IsSending && !IsCompressingContext;

    /// <summary>当前这轮 HTTP 请求尚未收到首条 SSE（每轮 API 往返显示一次骨架屏）。</summary>
    private bool _awaitingFirstSseLine;

    /// <summary>已出现助手正文或 tool_calls 流，但工具气泡尚未挂上（等待工具名/参数分片）。</summary>
    private bool _awaitingToolCallUi;

    /// <summary>本轮 SSE 是否已流过助手正文（用于正文后、tool_calls 前的空窗）。</summary>
    private bool _sawAssistantContentThisStream;

    /// <summary>最近一次 chat/completions 响应中 API 返回的上下文 token 总量。</summary>
    private int? _lastApiPromptTokens;

    /// <summary>最近一次响应中的 prompt_cache_hit_tokens（DeepSeek 等）。</summary>
    private int? _lastApiCachedPromptTokens;

    private int? _lastApiCompletionTokens;

    /// <summary>上一轮 chat/completions HTTP 是否已完成（用于区分「未发请求」与「成功但未返回 usage」）。</summary>
    private bool _lastApiRoundCompleted;

    /// <summary>上一轮 HTTP 请求是否带了 stream_options.include_usage。</summary>
    private bool _lastRequestIncludedUsage;

    private readonly Dictionary<int, ChatMessage> _streamingToolByIndex = new();
    private readonly Dictionary<string, ChatMessage> _streamingToolByCallId = new(StringComparer.Ordinal);

    /// <summary>首包等待或工具调用尚未展示占位气泡时显示骨架屏。</summary>
    public bool IsWaitingForReply =>
        (!IsCompressingContext && IsSending && (_awaitingFirstSseLine || _awaitingToolCallUi))
        || _subAgentWaitingForReply;

    private bool _subAgentWaitingForReply;

    public ObservableCollection<string> AvailableModels { get; } = [];

    [ObservableProperty]
    private string _selectedModel = string.Empty;

    /// <summary>是否向模型请求并展示思考过程（reasoning_content）。</summary>
    [ObservableProperty]
    private bool _enableThinking;

    [ObservableProperty]
    private int _selectedTabIndex;

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public ObservableCollection<ChatRoundBlock> SurfaceRounds { get; } = [];

    public event EventHandler? ChatSurfaceRestored;
    public event EventHandler<ChatMessagesTruncatedEventArgs>? ChatMessagesTruncated;
    public event EventHandler<ChatMessageUiIdRemappedEventArgs>? ChatMessageUiIdRemapped;

    private readonly ChatTranscriptTailDisplay _transcriptDisplay = new();
    private readonly IChatMarkdownRenderQueue _markdownRenderQueue;

    [ObservableProperty]
    private string _transcriptDisplayText = string.Empty;

    private readonly HttpClient _httpClient;

    private readonly IEventBus _eventBus;
    private readonly ISettingsService _settingsService;
    private readonly AiToolRegistry _toolRegistry;
    private readonly AskToolService _askToolService;
    private readonly IAiChatSessionStore _sessionStore;
    private readonly IChatSessionSurfaceManager _surfaceManager;
    private readonly LookService _lookService;
    private readonly McpAiToolBridge _mcpToolBridge;
    private readonly IAiChatRunContext _runContext;
    private readonly AiChatSessionContext _sessionContext;
    private readonly ChatContextUsageService _contextUsageService;
    private readonly ChatSendOrchestrator _chatSendOrchestrator;
    private readonly ChatSessionApiPayloadBuilder _apiPayloadBuilder;
    private readonly SubAgentSessionHub _subAgentSessionHub;
    private readonly IAiToolResultRuntimeStore _toolResultRuntimeStore;
    private readonly AiSessionListState _sessionListState;
    private readonly bool _isCoordinatorInstance;
    private long _apiStartMessageId;

    private SessionTokenUsageState? _sessionTokenUsage;
    private CancellationTokenSource? _sendCts;
    private bool _isLoadingHistory;
    private bool _suppressCancelMessageForRevert;

    private const int InitialSurfaceRoundCount = 48;
    private const int SurfaceRoundExpandBatch = 24;
    private const int HotTailKeepCount = 32;

    private List<ChatRoundBlock>? _allSurfaceRounds;
    private int _surfaceRoundStartIndex;
    private bool _isExpandingSurfaceRounds;

    /// <summary>正在批量装入历史消息。</summary>
    public bool IsLoadingHistory => _isLoadingHistory;

    /// <summary>聊天区仍可向上展开更早的轮次。</summary>
    public bool CanExpandSurfaceRounds =>
        !_isLoadingHistory
        && !_isExpandingSurfaceRounds
        && _allSurfaceRounds is not null
        && _surfaceRoundStartIndex > 0;

    /// <summary>当前会话 id（null 表示未加载任何会话）。</summary>
    private string? _currentSessionId;

    /// <summary>当前 SSE 发送归属的会话 id。</summary>
    private string? _streamingOwnerSessionId;

    /// <summary>用户切走但发送仍在进行时，仅保留这一份在途消息（不缓存其它会话）。</summary>
    private StreamingOwnerState? _streamingOwnerState;

    /// <summary>压缩轮次走同一 SSE 路径，但不把该次 API usage 当作会话上下文占用。</summary>
    private bool _compressionRoundActive;

    /// <summary>当前发送轮次已 append 到 SQLite 的消息 id（未落库前用 PendingUiKey）。</summary>
    private readonly HashSet<long> _dbAppendedIds = [];
    private readonly HashSet<string> _dbAppendedPendingKeys = new(StringComparer.Ordinal);

    private CompressionPrepResult? _activeCompressionPrep;

    private sealed class StreamingOwnerState
    {
        public required string SessionId { get; init; }
        public List<ChatMessage> Messages { get; } = [];
        public long ApiStartMessageId { get; set; }
        public SessionTokenUsageState? TokenUsage { get; set; }
    }

    [ObservableProperty]
    private bool _isReadOnlySession;

    /// <summary>会话列表（供 Dock 协调器和各会话 VM 共享）。</summary>
    public ObservableCollection<ChatSessionSummary> Sessions => _sessionListState.Sessions;

    /// <summary>当前会话 id。set 时触发切换。</summary>
    public string? CurrentSessionId
    {
        get => _currentSessionId;
        set => SetProperty(ref _currentSessionId, value);
    }

    /// <summary>当前会话标题（供 UI 显示）。</summary>
    [ObservableProperty]
    private string _currentSessionTitle = "新会话";

    public AskToolService AskToolService => _askToolService;

    public IChatMarkdownRenderQueue MarkdownRenderQueue => _markdownRenderQueue;

    public void NotifyMarkdownRenderCompleted(ChatMessage message) => _ = PersistRenderedMessageAsync(message);

    private int _transcriptPumpGeneration;
    private int _tokenRefreshGeneration;

    public AiPanelViewModel(
        IEventBus eventBus,
        ISettingsService settingsService,
        AiToolRegistry toolRegistry,
        AskToolService askToolService,
        IAiChatSessionStore sessionStore,
        IChatSessionSurfaceManager surfaceManager,
        LookService lookService,
        McpAiToolBridge mcpToolBridge,
        IOutboundHttpClientFactory httpClientFactory,
        IAiChatRunContext runContext,
        AiChatSessionContext sessionContext,
        ChatContextUsageService contextUsageService,
        ChatSendOrchestrator chatSendOrchestrator,
        ChatSessionApiPayloadBuilder apiPayloadBuilder,
        SubAgentSessionHub subAgentSessionHub,
        IChatMarkdownRenderQueue markdownRenderQueue,
        IAiToolResultRuntimeStore toolResultRuntimeStore,
        AiSessionListState sessionListState,
        AiPanelViewModelLifetime lifetime)
    {
        _eventBus = eventBus;
        _settingsService = settingsService;
        _toolRegistry = toolRegistry;
        _askToolService = askToolService;
        _sessionStore = sessionStore;
        _surfaceManager = surfaceManager;
        _lookService = lookService;
        _mcpToolBridge = mcpToolBridge;
        _runContext = runContext;
        _sessionContext = sessionContext;
        _contextUsageService = contextUsageService;
        _chatSendOrchestrator = chatSendOrchestrator;
        _apiPayloadBuilder = apiPayloadBuilder;
        _subAgentSessionHub = subAgentSessionHub;
        _markdownRenderQueue = markdownRenderQueue;
        _toolResultRuntimeStore = toolResultRuntimeStore;
        _sessionListState = sessionListState;
        _isCoordinatorInstance = lifetime.IsCoordinatorInstance;
        _subAgentSessionHub.SessionStarted += OnSubAgentSessionStarted;
        _subAgentSessionHub.SessionUpdated += OnSubAgentSessionUpdated;
        _subAgentSessionHub.RoundWaiting += OnSubAgentRoundWaiting;
        _subAgentSessionHub.MessageAppended += OnSubAgentMessageAppended;
        _subAgentSessionHub.MessagePatched += OnSubAgentMessagePatched;
        _httpClient = httpClientFactory.CreateClient("ai-panel-chat", TimeSpan.FromSeconds(120));
        ChatSystemPrompt.SetWorkspaceDirectory(_lookService.WorkspaceDirectory);
        RefreshConfig();
        SubscribeEvent<AppSettingsSavedEvent>(_eventBus, _ => OnAppSettingsSaved());
        if (_isCoordinatorInstance)
        {
            SubscribeEvent<ProjectOpenedEvent>(_eventBus, e => OnProjectOpened(e));
            SubscribeEvent<AiNewConversationRequestedEvent>(_eventBus, _event =>
            {
                var ignored = CreateAndSwitchSessionAsync();
            });
        }
        Messages.CollectionChanged += OnMessagesCollectionChanged;
        _transcriptDisplay.Bind(GetVisibleMessagesSnapshot, text =>
        {
            if (string.Equals(text, TranscriptDisplayText, StringComparison.Ordinal))
                return;

            TranscriptDisplayText = text;
        });
        RefreshTranscriptDisplay();
        if (_isCoordinatorInstance)
            StartupPerformance.RunAfterDelay(() => _ = RefreshMcpToolsAsync(), delayMs: 3800);
    }

    private void OnProjectOpened(ProjectOpenedEvent e) => _ = OnProjectOpenedAsync(e);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _subAgentSessionHub.SessionStarted -= OnSubAgentSessionStarted;
            _subAgentSessionHub.SessionUpdated -= OnSubAgentSessionUpdated;
            _subAgentSessionHub.RoundWaiting -= OnSubAgentRoundWaiting;
            _subAgentSessionHub.MessageAppended -= OnSubAgentMessageAppended;
            _subAgentSessionHub.MessagePatched -= OnSubAgentMessagePatched;
            Messages.CollectionChanged -= OnMessagesCollectionChanged;
            _transcriptDisplay.CancelPending();
            _sendCts?.Cancel();
            _sendCts?.Dispose();
            _sendCts = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>项目打开：加载会话列表 → 选中首个或新建。</summary>
    private async Task OnProjectOpenedAsync(ProjectOpenedEvent e)
    {
        ChatSystemPrompt.SetWorkspaceDirectory(e.DirectoryPath);
        _subAgentSessionHub.Clear();
        _streamingOwnerState = null;

        await LoadSessionsAsync().ConfigureAwait(false);
    }

    /// <summary>从 store 加载会话列表，选中第一个；无会话则新建一个。</summary>
    private async Task LoadSessionsAsync()
    {
        List<ChatSessionSummary> list;
        try { list = (await _sessionStore.ListSessionsAsync().ConfigureAwait(false)).ToList(); }
        catch { return; }

        await UiThreadBridge.InvokeAsync(() =>
        {
            Sessions.Clear();
            foreach (var s in list)
                Sessions.Add(s);

            if (Sessions.Count == 0)
            {
                _ = CreateAndSwitchSessionAsync();
                return;
            }

            var target = _currentSessionId is not null && Sessions.Any(s => s.Id == _currentSessionId)
                ? _currentSessionId
                : Sessions[0].Id;
            _ = SwitchSessionAsync(target);
        }).ConfigureAwait(false);
    }

    /// <summary>切换到指定会话：与主会话相同流程——清空 Messages、装入该会话消息、重建 WebView。</summary>
    public Task SwitchSessionAsync(string sessionId) =>
        SwitchSessionAsync(sessionId, force: false);

    /// <summary>打开子 Agent 只读会话（已完成的历史子会话）。</summary>
    public Task OpenSubAgentSessionAsync(string? sessionId) =>
        string.IsNullOrEmpty(sessionId)
            ? Task.CompletedTask
            : SwitchSessionAsync(sessionId, force: false);

    /// <summary>
    /// 独立会话 Tab 已经自行持有消息列表；这里仅同步全局 Dock 协调器的焦点会话，
    /// 避免后续标题/列表刷新时用过期 CurrentSessionId 把 UI 切回旧 Tab。
    /// </summary>
    public void FocusSessionTab(string sessionId)
    {
        if (!_isCoordinatorInstance || string.IsNullOrWhiteSpace(sessionId))
            return;
        if (!Sessions.Any(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal)))
            return;

        var summary = Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
        if (!string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
        {
            _currentSessionId = sessionId;
            OnPropertyChanged(nameof(CurrentSessionId));
        }

        if (summary is not null)
            CurrentSessionTitle = string.IsNullOrWhiteSpace(summary.Title) ? "新会话" : summary.Title;
        _sessionContext.SetSessionId(sessionId);
    }

    private async Task SwitchSessionAsync(string sessionId, bool force)
    {
        if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
            return;

        if (_isCoordinatorInstance)
        {
            await UiThreadBridge.InvokeAsync(() =>
            {
                _currentSessionId = sessionId;
                OnPropertyChanged(nameof(CurrentSessionId));
                var summary = Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
                CurrentSessionTitle = string.IsNullOrWhiteSpace(summary?.Title) ? "新会话" : summary!.Title;
                _sessionContext.SetSessionId(sessionId);
            }).ConfigureAwait(false);
            return;
        }

        var isTargetSubAgent = SubAgentSessionHub.IsSubAgentSessionId(sessionId);

        if (isTargetSubAgent)
        {
            if (!_subAgentSessionHub.TryGetSession(sessionId, out var sub) || sub is null)
                return;

            if (!string.IsNullOrEmpty(_currentSessionId)
                && !SubAgentSessionHub.IsSubAgentSessionId(_currentSessionId))
            {
                try { await PrepareLeaveCurrentSessionAsync().ConfigureAwait(false); }
                catch { /* 切换前落盘失败不阻断 */ }
            }

            List<ChatMessage> items;
            string title;
            lock (sub.Sync)
            {
                items = sub.Messages.ToList();
                title = sub.Title;
            }

            await UiThreadBridge.InvokeAsync(() =>
            {
                LoadSessionMessages(sessionId, title, isReadOnly: true, items);
                _apiStartMessageId = 0;
                ApplySessionTokenUsage(null);
                if (sub.Status == SubAgentRunStatus.Running)
                    SetSubAgentWaitingForReply(true);
                else
                    SetSubAgentWaitingForReply(false);
            }).ConfigureAwait(false);

            _ = RefreshContextTokenUsageAsync();
            return;
        }

        ChatSessionHeader? header;
        try { header = await _sessionStore.LoadSessionHeaderAsync(sessionId).ConfigureAwait(false); }
        catch { return; }
        if (header is null)
            return;

        if (!string.IsNullOrEmpty(_currentSessionId)
            && !string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal)
            && !SubAgentSessionHub.IsSubAgentSessionId(_currentSessionId))
        {
            try { await PrepareLeaveCurrentSessionAsync().ConfigureAwait(false); }
            catch { /* 切换前落盘失败不阻断 */ }
        }

        await UiThreadBridge.InvokeAsync(() => SetLoadingHistory(true)).ConfigureAwait(false);

        List<ChatMessage> hotMessages;
        List<ChatRoundBlock>? prebuiltRounds = null;
        long cachedApiStartMessageId;
        SessionTokenUsageState? cachedTokenUsage;

        var sending = await UiThreadBridge.InvokeAsync(() => IsSending).ConfigureAwait(false);

        if (_streamingOwnerState is not null
            && sending
            && string.Equals(_streamingOwnerState.SessionId, sessionId, StringComparison.Ordinal))
        {
            hotMessages = _streamingOwnerState.Messages.ToList();
            cachedApiStartMessageId = _streamingOwnerState.ApiStartMessageId > 0
                ? _streamingOwnerState.ApiStartMessageId
                : header.ApiStartMessageId;
            cachedTokenUsage = _streamingOwnerState.TokenUsage ?? header.TokenUsage;
            prebuiltRounds = ChatSurfaceGrouping.GroupIntoRounds(
                hotMessages.Where(IsVisibleSurfaceMessage).ToList());
        }
        else
        {
            hotMessages = [];
            await _surfaceManager.InitializeAsync(sessionId, hotMessages).ConfigureAwait(false);
            cachedApiStartMessageId = header.ApiStartMessageId;
            cachedTokenUsage = header.TokenUsage;
            prebuiltRounds = ChatSurfaceGrouping.GroupIntoRounds(
                _surfaceManager.BuildVisibleSurface(hotMessages).ToList());
        }

        await UiThreadBridge.InvokeAsync(() =>
        {
            if (_streamingOwnerState is not null
                && sending
                && string.Equals(_streamingOwnerState.SessionId, sessionId, StringComparison.Ordinal))
            {
                _streamingOwnerState = null;
            }

            LoadSessionMessages(
                sessionId,
                header.Title,
                isReadOnly: false,
                hotMessages,
                prebuiltRounds);
            _apiStartMessageId = cachedApiStartMessageId;
            ApplySessionTokenUsage(cachedTokenUsage);
            return 0;
        }).ConfigureAwait(false);

        _ = RefreshContextTokenUsageAsync();
    }

    private async Task PrepareLeaveCurrentSessionAsync()
    {
        if (string.IsNullOrEmpty(_currentSessionId))
            return;

        if (SubAgentSessionHub.IsSubAgentSessionId(_currentSessionId))
            return;

        var sending = await UiThreadBridge.InvokeAsync(() => IsSending).ConfigureAwait(false);
        if (sending
            && !string.IsNullOrEmpty(_streamingOwnerSessionId)
            && string.Equals(_streamingOwnerSessionId, _currentSessionId, StringComparison.Ordinal))
        {
            await PersistStreamingOwnerSessionAsync(_currentSessionId).ConfigureAwait(false);
            await UiThreadBridge.InvokeAsync(() =>
            {
                _streamingOwnerState = new StreamingOwnerState
                {
                    SessionId = _currentSessionId!,
                    ApiStartMessageId = _apiStartMessageId,
                    TokenUsage = CloneTokenUsageState(_sessionTokenUsage)
                };
                foreach (var message in Messages)
                    _streamingOwnerState.Messages.Add(message);
            }).ConfigureAwait(false);
            return;
        }

        var snapshot = await UiThreadBridge.InvokeAsync(() => Messages.ToList()).ConfigureAwait(false);
        await AppendPendingStableMessagesAsync(_currentSessionId!, snapshot).ConfigureAwait(false);
        await PersistSessionMetadataAsync(_currentSessionId!).ConfigureAwait(false);
    }

    private static SessionTokenUsageState? CloneTokenUsageState(SessionTokenUsageState? source)
        => ChatContextUsageService.CloneTokenUsageState(source);

    private void SeedDbAppendTracking(IEnumerable<ChatMessage> messages)
    {
        _dbAppendedIds.Clear();
        _dbAppendedPendingKeys.Clear();
        foreach (var message in messages)
        {
            if (message.Id > 0)
                _dbAppendedIds.Add(message.Id);
        }
    }

    private bool TrySkipAlreadyPersistedAppend(ChatMessage message)
    {
        if (message.Id <= 0)
            return false;

        if (!IsDbAppended(message))
            MarkDbAppended(message);

        return true;
    }

    private bool IsDbAppended(ChatMessage message) =>
        message.Id > 0
            ? _dbAppendedIds.Contains(message.Id)
            : _dbAppendedPendingKeys.Contains(message.PendingUiKey);

    private void MarkDbAppended(ChatMessage message)
    {
        if (message.Id > 0)
        {
            _dbAppendedPendingKeys.Remove(message.PendingUiKey);
            _dbAppendedIds.Add(message.Id);
        }
        else
        {
            _dbAppendedPendingKeys.Add(message.PendingUiKey);
        }
    }

    private void PruneDbAppendTrackingFrom(long fromMessageIdInclusive)
    {
        if (fromMessageIdInclusive <= 0)
            return;

        _dbAppendedIds.RemoveWhere(id => id >= fromMessageIdInclusive);
    }

    private async Task<AppendMessagesResult> AppendStableMessageToDbAsync(
        string sessionId,
        ChatMessage message,
        long? apiStartMessageId = null,
        SessionTokenUsageState? tokenUsage = null)
    {
        if (string.IsNullOrEmpty(sessionId)
            || SubAgentSessionHub.IsSubAgentSessionId(sessionId)
            || TrySkipAlreadyPersistedAppend(message))
            return new AppendMessagesResult();

        var priorUiId = message.Id > 0 ? null : ChatMessageIds.UiId(message);

        var result = await _sessionStore
            .AppendStableMessagesAsync(sessionId, [message], apiStartMessageId, tokenUsage)
            .ConfigureAwait(false);
        if (result.FirstMessageId >= 0)
        {
            MarkDbAppended(message);
            if (priorUiId is not null)
            {
                var newUiId = ChatMessageIds.UiId(message);
                if (!string.Equals(priorUiId, newUiId, StringComparison.Ordinal))
                    ChatMessageUiIdRemapped?.Invoke(this, new ChatMessageUiIdRemappedEventArgs(priorUiId, newUiId));
            }
        }

        return result;
    }

    private async Task AppendNewStableTailToDbAsync(string sessionId, IReadOnlyList<ChatMessage> ownerMessages)
    {
        if (string.IsNullOrEmpty(sessionId) || SubAgentSessionHub.IsSubAgentSessionId(sessionId))
            return;

        var batch = new List<ChatMessage>();
        foreach (var message in ownerMessages)
        {
            if (TrySkipAlreadyPersistedAppend(message))
                continue;
            if (!ChatHistoryMapper.TryGetPersistableDtos(message, out _))
                continue;
            batch.Add(message);
        }

        if (batch.Count == 0)
            return;

        var result = await _sessionStore.AppendStableMessagesAsync(sessionId, batch).ConfigureAwait(false);
        if (result.FirstMessageId < 0)
            return;

        foreach (var message in batch)
            MarkDbAppended(message);
    }

    private async Task UpdatePersistedMessageInDbAsync(string sessionId, ChatMessage message)
    {
        if (string.IsNullOrEmpty(sessionId) || SubAgentSessionHub.IsSubAgentSessionId(sessionId))
            return;

        await _sessionStore.UpdatePersistedMessageAsync(sessionId, message).ConfigureAwait(false);
        if (message.Id > 0)
            _toolResultRuntimeStore.Remove(message.Id);
    }

    private async Task<IReadOnlyList<ChatMessage>> GetOwnerMessagesForDbAsync()
    {
        return await UiThreadBridge.InvokeAsync(() =>
        {
            if (!string.IsNullOrEmpty(_streamingOwnerSessionId)
                && !string.Equals(_currentSessionId, _streamingOwnerSessionId, StringComparison.Ordinal)
                && _streamingOwnerState is not null
                && string.Equals(_streamingOwnerState.SessionId, _streamingOwnerSessionId, StringComparison.Ordinal))
                return (IReadOnlyList<ChatMessage>)_streamingOwnerState.Messages.ToArray();

            return (IReadOnlyList<ChatMessage>)Messages.ToArray();
        }).ConfigureAwait(false);
    }

    private async Task FinalizeStreamingFlagsForPersistAsync(IReadOnlyList<ChatMessage> messages)
    {
        await UiThreadBridge.InvokeAsync(() =>
        {
            foreach (var message in messages)
            {
                if (message.IsStreaming)
                    message.IsStreaming = false;
                if (message.IsThinking)
                    message.IsThinking = false;
            }
        }).ConfigureAwait(false);
    }

    private Task<SessionTokenUsageState?> ResolveSessionTokenUsageAsync(string sessionId) =>
        UiThreadBridge.InvokeAsync(() =>
        {
            if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
                return CloneTokenUsageState(_sessionTokenUsage);
            if (_streamingOwnerState is not null
                && string.Equals(_streamingOwnerState.SessionId, sessionId, StringComparison.Ordinal))
                return CloneTokenUsageState(_streamingOwnerState.TokenUsage);
            return CloneTokenUsageState(_sessionTokenUsage);
        });

    private void ApplySessionTokenUsage(SessionTokenUsageState? usage)
    {
        _sessionTokenUsage = CloneTokenUsageState(usage);
        if (_sessionTokenUsage is { IsApiMeasured: true }
            && _sessionTokenUsage.TryGetUsablePromptTokens(_sessionTokenUsage.ModelId, out _))
        {
            _lastApiPromptTokens = _sessionTokenUsage.PromptTokens;
            _lastApiCachedPromptTokens = _sessionTokenUsage.CachedPromptTokens;
            _lastApiCompletionTokens = _sessionTokenUsage.CompletionTokens;
            _lastApiRoundCompleted = true;
        }
        else
        {
            _lastApiPromptTokens = null;
            _lastApiCachedPromptTokens = null;
            _lastApiCompletionTokens = null;
            _lastApiRoundCompleted = false;
        }
    }

    private void CommitSessionTokenUsageFromLastApi(string modelId)
    {
        if (_lastApiPromptTokens is not int apiTokens || apiTokens <= 0)
            return;

        CommitSessionTokenUsageSnapshot(
            _streamingOwnerSessionId ?? _currentSessionId,
            modelId,
            apiTokens,
            isApiMeasured: true,
            cachedPromptTokens: _lastApiCachedPromptTokens,
            completionTokens: _lastApiCompletionTokens);
    }

    private void CommitSessionTokenUsageSnapshot(
        string? ownerId,
        string modelId,
        int tokenCount,
        bool isApiMeasured,
        int? cachedPromptTokens = null,
        int? completionTokens = null)
    {
        if (string.IsNullOrEmpty(ownerId) || tokenCount <= 0)
            return;

        var messageCount = string.Equals(_currentSessionId, ownerId, StringComparison.Ordinal)
            ? Messages.Count
            : _streamingOwnerState?.Messages.Count ?? Messages.Count;

        var usage = new SessionTokenUsageState
        {
            PromptTokens = tokenCount,
            IsApiMeasured = isApiMeasured,
            CachedPromptTokens = cachedPromptTokens,
            CompletionTokens = completionTokens,
            MessageCount = messageCount,
            ModelId = modelId,
            CapturedAtUtc = DateTime.UtcNow.ToString("O")
        };

        if (string.Equals(_currentSessionId, ownerId, StringComparison.Ordinal))
            _sessionTokenUsage = usage;

        if (_streamingOwnerState is not null
            && string.Equals(_streamingOwnerState.SessionId, ownerId, StringComparison.Ordinal))
            _streamingOwnerState.TokenUsage = CloneTokenUsageState(usage);
    }

    private void ResetStaleApiTokenUsageForLocalEstimate()
    {
        _lastApiPromptTokens = null;
        _lastApiCachedPromptTokens = null;
        _lastApiCompletionTokens = null;
        _lastApiRoundCompleted = false;
    }

    private void UpdateLatestRoundContextTokenCounts(
        string? sessionId,
        string modelId,
        string systemPrompt,
        bool allowApiUsage)
    {
        if (string.IsNullOrEmpty(sessionId)
            || string.IsNullOrWhiteSpace(modelId)
            || !TryGetWritableMessages(sessionId, out var messages, out _)
            || messages.Count == 0)
            return;

        var roundStart = ChatContextCompressionService.FindLastRoundStartIndex(messages as IReadOnlyList<ChatMessage> ?? messages.ToList());
        if (roundStart < 0 || roundStart >= messages.Count)
            return;

        var running = FindPreviousContextTokenCount(messages, roundStart);
        if (running <= 0)
            running = ChatMessageTokenEstimator.GetOrComputeSystemApiTokens(systemPrompt, modelId);

        for (var i = roundStart; i < messages.Count; i++)
        {
            var message = messages[i];
            if (ChatApiErrorHelper.IsUiOnlyAssistantMessage(message))
                continue;

            running += ChatMessageTokenEstimator.GetOrComputeMessageApiTokens(message, modelId);
            message.ContextTokenCount = running;
        }

        var apiTokenCount = allowApiUsage
                            && _lastApiPromptTokens is int apiTokens
                            && apiTokens > 0
            ? apiTokens
            : 0;
        var finalTokenCount = apiTokenCount > 0 ? apiTokenCount : running;
        messages[^1].ContextTokenCount = finalTokenCount;
        CommitSessionTokenUsageSnapshot(
            sessionId,
            modelId,
            finalTokenCount,
            isApiMeasured: apiTokenCount > 0,
            cachedPromptTokens: apiTokenCount > 0 ? _lastApiCachedPromptTokens : null,
            completionTokens: apiTokenCount > 0 ? _lastApiCompletionTokens : null);
    }

    private void UpdateCompressedContextTokenCounts(
        string sessionId,
        int startIndex,
        string modelId,
        string systemPrompt)
    {
        if (string.IsNullOrEmpty(sessionId)
            || string.IsNullOrWhiteSpace(modelId)
            || !TryGetWritableMessages(sessionId, out var messages, out _)
            || messages.Count == 0)
            return;

        var start = Math.Clamp(startIndex, 0, messages.Count - 1);
        var running = ChatMessageTokenEstimator.GetOrComputeSystemApiTokens(systemPrompt, modelId);
        for (var i = start; i < messages.Count; i++)
        {
            var message = messages[i];
            if (ChatApiErrorHelper.IsUiOnlyAssistantMessage(message))
                continue;

            running += ChatMessageTokenEstimator.GetOrComputeMessageApiTokens(message, modelId);
            message.ContextTokenCount = running;
        }

        messages[^1].ContextTokenCount = running;
        CommitSessionTokenUsageSnapshot(sessionId, modelId, running, isApiMeasured: false);
    }

    private static int FindPreviousContextTokenCount(IList<ChatMessage> messages, int beforeIndex)
    {
        for (var i = Math.Min(beforeIndex - 1, messages.Count - 1); i >= 0; i--)
        {
            if (messages[i].ContextTokenCount > 0)
                return messages[i].ContextTokenCount;
        }

        return 0;
    }

    /// <summary>统一的会话装入：主会话与子 Agent 会话走同一条路。</summary>
    private void LoadSessionMessages(
        string sessionId,
        string title,
        bool isReadOnly,
        IReadOnlyList<ChatMessage> incoming,
        IReadOnlyList<ChatRoundBlock>? prebuiltRounds = null)
    {
        _transcriptDisplay.CancelPending();
        SetLoadingHistory(true);
        try
        {
            _currentSessionId = sessionId;
            OnPropertyChanged(nameof(CurrentSessionId));
            CurrentSessionTitle = title;
            IsReadOnlySession = isReadOnly;

            if (!isReadOnly && _isCoordinatorInstance)
                _sessionContext.SetSessionId(sessionId);

            if (isReadOnly)
                _surfaceManager.Clear();

            _allSurfaceRounds = null;
            _surfaceRoundStartIndex = 0;

            Messages.Clear();
            foreach (var msg in incoming)
            {
                msg.TranscriptSink = _transcriptDisplay;
                Messages.Add(msg);
            }

            _transcriptDisplay.RequestRefresh();
            RebuildTailSurface(prebuiltRounds: prebuiltRounds);
            SeedDbAppendTracking(incoming);
        }
        finally
        {
            SetLoadingHistory(false);
        }

        Dispatcher.UIThread.Post(NotifyChatSurfaceRestored, DispatcherPriority.Background);
    }

    private void SetLoadingHistory(bool loading)
    {
        if (_isLoadingHistory == loading)
            return;

        _isLoadingHistory = loading;
        OnPropertyChanged(nameof(IsLoadingHistory));
        OnPropertyChanged(nameof(CanExpandSurfaceRounds));
    }

    /// <summary>新建会话并切换。</summary>
    private async Task CreateAndSwitchSessionAsync()
    {
        string id;
        try { id = await _sessionStore.CreateSessionAsync(null).ConfigureAwait(false); }
        catch { return; }
        if (string.IsNullOrEmpty(id))
            return;

        await UiThreadBridge.InvokeAsync(() =>
        {
            Sessions.Add(new ChatSessionSummary { Id = id, Title = "新会话" });
        }).ConfigureAwait(false);

        await SwitchSessionAsync(id).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task NewConversationAsync()
    {
        if (!_isCoordinatorInstance)
        {
            _eventBus.Publish(new AiNewConversationRequestedEvent());
            return;
        }

        if (IsSending)
            return;
        await CreateAndSwitchSessionAsync();
    }

    /// <summary>关闭会话 Tab 前二次确认文案。</summary>
    public string BuildCloseSessionConfirmMessage(string sessionId)
    {
        if (IsSending && string.Equals(_streamingOwnerSessionId, sessionId, StringComparison.Ordinal))
        {
            if (IsWaitingForReply)
            {
                return "AI 正在等待回复或输出中，确定关闭此会话？\n"
                    + "关闭将停止当前请求；会话记录将被删除，此操作不可恢复。";
            }

            return "AI 正在输出中，确定关闭此会话？\n"
                + "关闭将停止当前请求；会话记录将被删除，此操作不可恢复。";
        }

        if (IsCompressingContext && string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
        {
            return "正在压缩上下文，确定关闭此会话？\n"
                + "关闭将中断压缩；会话记录将被删除，此操作不可恢复。";
        }

        return "确定关闭此会话？\n此操作不可恢复。";
    }

    public Task CloseSessionAsync(string sessionId) => DeleteConversationAsync(sessionId);

    [RelayCommand]
    private async Task DeleteConversationAsync(string? sessionId)
    {
        var id = sessionId;
        if (string.IsNullOrEmpty(id))
            id = _currentSessionId;
        if (string.IsNullOrEmpty(id))
            return;

        if (SubAgentSessionHub.IsSubAgentSessionId(id))
        {
            if (IsSending && string.Equals(_streamingOwnerSessionId, id, StringComparison.Ordinal))
                CancelSending();

            _subAgentSessionHub.Remove(id);

            var switchTarget = string.Empty;
            await UiThreadBridge.InvokeAsync(() =>
            {
                for (var i = 0; i < Sessions.Count; i++)
                {
                    if (Sessions[i].Id == id)
                    {
                        Sessions.RemoveAt(i);
                        break;
                    }
                }

                switchTarget = Sessions.Count > 0 ? Sessions[0].Id : string.Empty;
            }).ConfigureAwait(false);

            if (string.Equals(_currentSessionId, id, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(switchTarget))
                    await SwitchSessionAsync(switchTarget).ConfigureAwait(false);
                else
                    await CreateAndSwitchSessionAsync().ConfigureAwait(false);
            }

            return;
        }

        if (IsSending && string.Equals(_streamingOwnerSessionId, id, StringComparison.Ordinal))
            CancelSending();

        try { await _sessionStore.DeleteSessionAsync(id).ConfigureAwait(false); }
        catch { return; }

        var normalSwitchTarget = string.Empty;
        await UiThreadBridge.InvokeAsync(() =>
        {
            for (var i = 0; i < Sessions.Count; i++)
            {
                if (Sessions[i].Id == id)
                {
                    Sessions.RemoveAt(i);
                    break;
                }
            }
            normalSwitchTarget = Sessions.Count > 0 ? Sessions[0].Id : string.Empty;
        }).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(normalSwitchTarget))
            await SwitchSessionAsync(normalSwitchTarget).ConfigureAwait(false);
        else
            await CreateAndSwitchSessionAsync().ConfigureAwait(false);
    }

    private void OnAppSettingsSaved()
    {
        UiThreadBridge.Post(() => _ = RefreshConfigAsync());
        if (!_isCoordinatorInstance)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _mcpToolBridge.RefreshAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        });
    }

    private async Task RefreshMcpToolsAsync()
    {
        try
        {
            await _mcpToolBridge.RefreshAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static bool IsVisibleSurfaceMessage(ChatMessage message) => message.Visual.IsVisibleInUi();

    /// <summary>WebView 可见消息：Messages 列表顺序，过滤 hidden 并按 id 去重。</summary>
    public IReadOnlyList<ChatMessage> GetVisibleMessagesForWeb() =>
        ChatHistoryMapper.DeduplicateByMessageId(Messages.Where(IsVisibleSurfaceMessage));

    public async Task<IReadOnlyList<ChatMessage>> HydrateVisibleRangeForWebAsync(int fromVisible, int toVisible)
    {
        if (!_surfaceManager.IsActive || string.IsNullOrWhiteSpace(_currentSessionId))
            return GetVisibleMessagesForWeb();

        _surfaceManager.TrimHydratedCache(fromVisible, toVisible);
        return await _surfaceManager
            .HydrateVisibleRangeAsync(fromVisible, toVisible, Messages)
            .ConfigureAwait(false);
    }

    private List<ChatMessage> GetVisibleMessagesSnapshot()
    {
        if (_surfaceManager.IsActive
            && !string.IsNullOrEmpty(_currentSessionId)
            && string.Equals(_surfaceManager.SessionId, _currentSessionId, StringComparison.Ordinal))
        {
            return _surfaceManager.BuildVisibleSurface(Messages.ToList()).ToList();
        }

        return GetVisibleMessagesForWeb().ToList();
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isLoadingHistory)
            return;

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            if (_surfaceManager.IsActive)
                _surfaceManager.SyncHotTailIndex(Messages);

            foreach (ChatMessage item in e.NewItems)
            {
                if (!IsVisibleSurfaceMessage(item))
                    continue;

                if (item.IsUser)
                {
                    if (!TryAppendTailUserRound(item))
                        RebuildTailSurface();
                    continue;
                }

                if (SurfaceRounds.Count == 0)
                {
                    RebuildTailSurface();
                    continue;
                }

                var last = SurfaceRounds[^1];
                if (!last.Following.Contains(item))
                    last.Following.Add(item);
            }

            if (_surfaceManager.IsActive)
                TrimMessagesToHotTail();
        }
        else if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset)
        {
            RefreshTranscriptDisplay();
        }

    }

    private bool TryAppendTailUserRound(ChatMessage userMessage)
    {
        if (_allSurfaceRounds is not null)
        {
            foreach (var round in _allSurfaceRounds)
            {
                if (ReferenceEquals(round.UserMessage, userMessage))
                    return true;
            }
        }
        else if (SurfaceRounds.Count > 0 && ReferenceEquals(SurfaceRounds[^1].UserMessage, userMessage))
        {
            return true;
        }

        var block = new ChatRoundBlock { UserMessage = userMessage };
        SurfaceRounds.Add(block);
        _allSurfaceRounds?.Add(block);
        return true;
    }

    private void TrimMessagesToHotTail()
    {
        if (!_surfaceManager.IsActive)
            return;

        while (Messages.Count > HotTailKeepCount)
            Messages.RemoveAt(0);

        _surfaceManager.SyncHotTailIndex(Messages);
    }

    private void RebuildTailSurface(bool notifyWebView = false, IReadOnlyList<ChatRoundBlock>? prebuiltRounds = null)
    {
        if (prebuiltRounds is null)
        {
            var visible = _surfaceManager.IsActive
                          && !string.IsNullOrEmpty(_currentSessionId)
                          && string.Equals(_surfaceManager.SessionId, _currentSessionId, StringComparison.Ordinal)
                ? _surfaceManager.BuildVisibleSurface(Messages.ToList()).ToList()
                : GetVisibleMessagesSnapshot();
            _allSurfaceRounds = ChatSurfaceGrouping.GroupIntoRounds(visible);
        }
        else
        {
            _allSurfaceRounds = prebuiltRounds.ToList();
        }

        _surfaceRoundStartIndex = Math.Max(0, _allSurfaceRounds.Count - InitialSurfaceRoundCount);
        ApplySurfaceRoundsWindow();

        if (notifyWebView && !_isLoadingHistory)
            NotifyChatSurfaceRestored();

        OnPropertyChanged(nameof(CanExpandSurfaceRounds));
    }

    private void ApplySurfaceRoundsWindow()
    {
        SurfaceRounds.Clear();
        if (_allSurfaceRounds is null || _allSurfaceRounds.Count == 0)
            return;

        for (var i = _surfaceRoundStartIndex; i < _allSurfaceRounds.Count; i++)
            SurfaceRounds.Add(_allSurfaceRounds[i]);
    }

    /// <summary>用户向上滚动接近顶部时，分批挂载更早的聊天轮次。</summary>
    public bool TryExpandSurfaceRoundsUpward(int batchSize, out int expandedRoundCount)
    {
        expandedRoundCount = 0;
        if (!CanExpandSurfaceRounds || _allSurfaceRounds is null)
            return false;

        var take = Math.Min(batchSize, _surfaceRoundStartIndex);
        if (take <= 0)
            return false;

        _isExpandingSurfaceRounds = true;
        try
        {
            var newStart = _surfaceRoundStartIndex - take;
            _surfaceRoundStartIndex = newStart;
            for (var i = 0; i < take; i++)
                SurfaceRounds.Insert(0, _allSurfaceRounds[_surfaceRoundStartIndex + i]);

            expandedRoundCount = take;
            OnPropertyChanged(nameof(CanExpandSurfaceRounds));

            var visibleFrom = CountVisibleMessagesInRounds(_allSurfaceRounds, 0, newStart);
            var visibleTo = CountVisibleMessagesInRounds(_allSurfaceRounds, 0, newStart + take) - 1;
            if (visibleTo >= visibleFrom)
                _ = HydrateSurfaceRangeAsync(visibleFrom, visibleTo);

            return true;
        }
        finally
        {
            _isExpandingSurfaceRounds = false;
        }
    }

    public bool TryExpandSurfaceRoundsUpward(out int expandedRoundCount) =>
        TryExpandSurfaceRoundsUpward(SurfaceRoundExpandBatch, out expandedRoundCount);

    private async Task HydrateSurfaceRangeAsync(int fromVisible, int toVisible)
    {
        if (!_surfaceManager.IsActive || string.IsNullOrWhiteSpace(_currentSessionId))
            return;

        _surfaceManager.TrimHydratedCache(fromVisible, toVisible);
        var loaded = await _surfaceManager
            .HydrateVisibleRangeAsync(fromVisible, toVisible, Messages)
            .ConfigureAwait(false);
        if (loaded.Count == 0)
            return;

        await UiThreadBridge.InvokeAsync(() =>
        {
            foreach (var hydrated in loaded)
                ApplyArchiveHydrationToSurface(hydrated);
        }).ConfigureAwait(false);
    }

    private void ApplyArchiveHydrationToSurface(ChatMessage hydrated)
    {
        if (_allSurfaceRounds is null)
            return;

        foreach (var round in _allSurfaceRounds)
        {
            if (round.UserMessage is { IsArchiveShell: true } user
                && ChatMessageIds.MatchesUiId(user, ChatMessageIds.UiId(hydrated)))
            {
                user.ApplyArchiveHydration(hydrated);
            }

            foreach (var following in round.Following)
            {
                if (following.IsArchiveShell
                    && ChatMessageIds.MatchesUiId(following, ChatMessageIds.UiId(hydrated)))
                {
                    following.ApplyArchiveHydration(hydrated);
                }
            }
        }

        foreach (var message in Messages)
        {
            if (message.IsArchiveShell
                && ChatMessageIds.MatchesUiId(message, ChatMessageIds.UiId(hydrated)))
            {
                message.ApplyArchiveHydration(hydrated);
            }
        }
    }

    private static int CountVisibleMessagesInRounds(
        IReadOnlyList<ChatRoundBlock>? rounds,
        int startRound,
        int endRoundExclusive)
    {
        if (rounds is null || startRound >= endRoundExclusive)
            return 0;

        endRoundExclusive = Math.Min(endRoundExclusive, rounds.Count);
        var count = 0;
        for (var r = startRound; r < endRoundExclusive; r++)
        {
            if (rounds[r].UserMessage is not null)
                count++;
            count += rounds[r].Following.Count;
        }

        return count;
    }

    /// <summary>WebView 晚于会话加载就绪、或 Tab 再次激活时，显式触发全量 surface 同步。</summary>
    public void RequestChatSurfaceResync() => NotifyChatSurfaceRestored();

    private void NotifyChatSurfaceRestored() => ChatSurfaceRestored?.Invoke(this, EventArgs.Empty);

    private void RefreshTranscriptDisplay() => _transcriptDisplay.RefreshNow();

    private void StartTranscriptPump()
    {
        _transcriptDisplay.BeginStreaming();
        var gen = Interlocked.Increment(ref _transcriptPumpGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                while (gen == Volatile.Read(ref _transcriptPumpGeneration))
                {
                    await Task.Delay(600).ConfigureAwait(false);
                    if (gen != Volatile.Read(ref _transcriptPumpGeneration))
                        break;

                    _transcriptDisplay.RequestRefresh();
                }
            }
            catch
            {
                // 流式刷新失败不阻断发送
            }
        });
    }

    private void StopTranscriptPump()
    {
        Interlocked.Increment(ref _transcriptPumpGeneration);
        _transcriptDisplay.EndStreaming();
    }

    partial void OnIsSendingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotSending));
        OnPropertyChanged(nameof(SendOrStopToolTip));
        OnPropertyChanged(nameof(IsStopEnabled));
        SendOrStopCommand.NotifyCanExecuteChanged();
        RefreshWaitingForReplyState();
    }

    partial void OnIsCompressingContextChanged(bool value)
    {
        OnPropertyChanged(nameof(SendOrStopToolTip));
        OnPropertyChanged(nameof(IsStopEnabled));
        SendOrStopCommand.NotifyCanExecuteChanged();
        RefreshWaitingForReplyState();
    }

    partial void OnInputTextChanged(string value) => SendOrStopCommand.NotifyCanExecuteChanged();

    partial void OnSelectedModelChanged(string value) => RefreshContextTokenUsage();

    partial void OnEnableThinkingChanged(bool value)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var settings = _settingsService.Load();
                if (settings.Ai.EnableThinking == value)
                    return;

                settings.Ai.EnableThinking = value;
                _settingsService.Save(settings);
            }
            catch
            {
                // 持久化失败不影响当前会话切换
            }
        });
    }

    private void RefreshWaitingForReplyState() => OnPropertyChanged(nameof(IsWaitingForReply));

    private void BeginAwaitingFirstSseLine()
    {
        UiThreadBridge.PostBackground(() =>
        {
            _awaitingFirstSseLine = true;
            RefreshWaitingForReplyState();
        });
    }

    private void MarkFirstSseLineReceived()
    {
        if (!_awaitingFirstSseLine)
            return;

        _awaitingFirstSseLine = false;
        UiThreadBridge.PostBackground(RefreshWaitingForReplyState);
    }

    private void BeginAwaitingToolCallUi()
    {
        if (_awaitingToolCallUi)
            return;

        UiThreadBridge.PostBackground(() =>
        {
            _awaitingToolCallUi = true;
            RefreshWaitingForReplyState();
        });
    }

    private void EndAwaitingToolCallUi()
    {
        if (!_awaitingToolCallUi)
            return;

        UiThreadBridge.PostBackground(() =>
        {
            _awaitingToolCallUi = false;
            RefreshWaitingForReplyState();
        });
    }

    private void ResetStreamingToolPlaceholders()
    {
        _streamingToolByIndex.Clear();
        _streamingToolByCallId.Clear();
        _sawAssistantContentThisStream = false;
    }

    private void EnsureStreamingToolPlaceholder(int index, ToolCallBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Name))
            return;

        var callId = string.IsNullOrWhiteSpace(builder.Id) ? $"call_pending_{index}" : builder.Id.Trim();
        var args = builder.Arguments ?? "";
        var summary = ToolCallDisplayHelper.FormatCommandSummary(builder.Name, args);
        var displayCommand = string.IsNullOrEmpty(summary) ? args : summary;

        if (_streamingToolByIndex.TryGetValue(index, out var existing))
        {
            UiThreadBridge.Post(() =>
            {
                existing.ToolName = builder.Name;
                existing.ToolArgumentsJson = args;
                existing.ToolCommand = displayCommand;
            });
            return;
        }

        var toolMsg = CreateToolCallMessage(builder.Name, callId, args);
        toolMsg.ToolCommand = displayCommand;
        _streamingToolByIndex[index] = toolMsg;
        _streamingToolByCallId[callId] = toolMsg;
        EndAwaitingToolCallUi();

        UiThreadBridge.Post(() => AddToStreamingSession(toolMsg));
    }

    private void SyncStreamingToolPlaceholdersFromBuilders(IReadOnlyDictionary<int, ToolCallBuilder> builders)
    {
        foreach (var kv in builders.OrderBy(x => x.Key))
            EnsureStreamingToolPlaceholder(kv.Key, kv.Value);
    }

    private bool TryResolveStreamingToolMessage(ToolCallInfo tc, int index, out ChatMessage toolMsg)
    {
        if (!string.IsNullOrWhiteSpace(tc.Id) && _streamingToolByCallId.TryGetValue(tc.Id.Trim(), out toolMsg!))
            return true;

        if (_streamingToolByIndex.TryGetValue(index, out toolMsg!))
        {
            if (!string.IsNullOrWhiteSpace(tc.Id) && !string.Equals(toolMsg.ToolCallId, tc.Id, StringComparison.Ordinal))
            {
                _streamingToolByCallId.Remove(toolMsg.ToolCallId);
                toolMsg.ToolCallId = tc.Id.Trim();
                _streamingToolByCallId[toolMsg.ToolCallId] = toolMsg;
            }

            return true;
        }

        var pendingId = $"call_pending_{index}";
        if (_streamingToolByCallId.TryGetValue(pendingId, out toolMsg!))
        {
            if (!string.IsNullOrWhiteSpace(tc.Id))
            {
                _streamingToolByCallId.Remove(pendingId);
                toolMsg.ToolCallId = tc.Id.Trim();
                _streamingToolByCallId[toolMsg.ToolCallId] = toolMsg;
            }

            _streamingToolByIndex[index] = toolMsg;
            return true;
        }

        toolMsg = null!;
        return false;
    }

    private string ResolveToolCallArguments(ToolCallInfo tc, int index)
    {
        var args = tc.Arguments ?? string.Empty;
        if (TryResolveStreamingToolMessage(tc, index, out var placeholder))
        {
            var fromUi = placeholder.ToolArgumentsJson ?? string.Empty;
            if (fromUi.Length > args.Length)
                args = fromUi;
        }

        return args;
    }

    private List<ChatMessage> GetStreamingOwnerMessages()
    {
        if (string.IsNullOrEmpty(_streamingOwnerSessionId))
            throw new InvalidOperationException("当前没有进行中的发送归属会话。");

        if (_streamingOwnerState is not null
            && string.Equals(_streamingOwnerState.SessionId, _streamingOwnerSessionId, StringComparison.Ordinal))
            return _streamingOwnerState.Messages;

        _streamingOwnerState = new StreamingOwnerState
        {
            SessionId = _streamingOwnerSessionId,
            ApiStartMessageId = _apiStartMessageId,
            TokenUsage = CloneTokenUsageState(_sessionTokenUsage)
        };
        return _streamingOwnerState.Messages;
    }

    private void ApplyCompressionHiddenFlags(ChatMessage message)
    {
        if (!_compressionRoundActive)
            return;

        message.Visual = ChatMessageVisual.Hidden;
    }

    private void AddToStreamingSession(ChatMessage message)
    {
        void Apply()
        {
            ApplyCompressionHiddenFlags(message);
            message.TranscriptSink = _transcriptDisplay;

            if (!string.IsNullOrEmpty(_streamingOwnerSessionId)
                && !string.Equals(_currentSessionId, _streamingOwnerSessionId, StringComparison.Ordinal))
            {
                var list = GetStreamingOwnerMessages();
                if (!list.Contains(message))
                    InsertStreamingMessage(list, message);
                return;
            }

            InsertStreamingMessage(Messages, message);
            _transcriptDisplay.RequestRefresh();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            UiThreadBridge.Post(Apply);
    }

    private void InsertStreamingMessage(IList<ChatMessage> messages, ChatMessage message)
    {
        if (!messages.Contains(message))
            messages.Add(message);
    }

    private void RemoveFromStreamingSession(ChatMessage message)
    {
        UiThreadBridge.Post(() => RemoveFromStreamingSessionNow(message));
    }

    private void RemoveFromStreamingSessionNow(ChatMessage message)
    {
        if (!string.IsNullOrEmpty(_streamingOwnerSessionId)
            && !string.Equals(_currentSessionId, _streamingOwnerSessionId, StringComparison.Ordinal))
        {
            GetStreamingOwnerMessages().Remove(message);
        }
        else
        {
            Messages.Remove(message);
        }
    }

    [RelayCommand]
    private void ClosePanel()
    {
        _eventBus.Publish(new AiPanelVisibilityChangedEvent(false));
    }

    /// <summary>应用退出或窗口关闭前：追加未落库稳定消息并刷新会话元数据。</summary>
    public async Task FlushPersistAsync()
    {
        var ownerId = await UiThreadBridge.InvokeAsync(() =>
            _streamingOwnerSessionId ?? _currentSessionId).ConfigureAwait(false);

        if (string.IsNullOrEmpty(ownerId))
            return;

        await PersistStreamingOwnerSessionAsync(ownerId).ConfigureAwait(false);

        var currentId = await UiThreadBridge.InvokeAsync(() => _currentSessionId).ConfigureAwait(false);
        if (string.IsNullOrEmpty(currentId)
            || SubAgentSessionHub.IsSubAgentSessionId(currentId)
            || string.Equals(currentId, ownerId, StringComparison.Ordinal))
            return;

        var snapshot = await UiThreadBridge.InvokeAsync(() => Messages.ToList()).ConfigureAwait(false);
        await AppendPendingStableMessagesAsync(currentId, snapshot).ConfigureAwait(false);
        await PersistSessionMetadataAsync(currentId).ConfigureAwait(false);
    }

    private async Task PersistRenderedMessageAsync(ChatMessage message)
    {
        if (message.Id <= 0)
            return;

        try
        {
            var sessionId = await UiThreadBridge.InvokeAsync(() => _currentSessionId).ConfigureAwait(false);
            if (string.IsNullOrEmpty(sessionId) || SubAgentSessionHub.IsSubAgentSessionId(sessionId))
                return;

            await UpdatePersistedMessageInDbAsync(sessionId, message).ConfigureAwait(false);
        }
        catch
        {
            // content_html 落盘失败不阻断 UI
        }
    }

    private async Task AppendPendingStableMessagesAsync(string sessionId, IReadOnlyList<ChatMessage> messages)
    {
        if (string.IsNullOrEmpty(sessionId) || SubAgentSessionHub.IsSubAgentSessionId(sessionId))
            return;

        await FinalizeStreamingFlagsForPersistAsync(messages).ConfigureAwait(false);
        await AppendNewStableTailToDbAsync(sessionId, messages).ConfigureAwait(false);
    }

    private async Task PersistRoundContextUpdatesAsync(string sessionId, IReadOnlyList<ChatMessage> messages)
    {
        if (string.IsNullOrEmpty(sessionId) || messages.Count == 0)
            return;

        var roundStart = ChatContextCompressionService.FindLastRoundStartIndex(messages);
        if (roundStart < 0)
            return;

        for (var i = roundStart; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Id <= 0 || !ChatHistoryMapper.TryGetPersistableDtos(message, out _))
                continue;

            await UpdatePersistedMessageInDbAsync(sessionId, message).ConfigureAwait(false);
        }
    }

    private async Task PersistSessionMetadataAsync(
        string sessionId,
        long? apiStartMessageId = null,
        SessionTokenUsageState? tokenUsage = null,
        IReadOnlyList<ChatMessage>? messagesForTitle = null,
        bool updateTitleUi = true)
    {
        if (string.IsNullOrEmpty(sessionId) || SubAgentSessionHub.IsSubAgentSessionId(sessionId))
            return;

        var capture = await UiThreadBridge.InvokeAsync(() =>
        {
            IReadOnlyList<ChatMessage> messages;
            long apiStart;
            SessionTokenUsageState? usage;

            if (messagesForTitle is not null)
            {
                messages = messagesForTitle;
                apiStart = apiStartMessageId ?? _apiStartMessageId;
                usage = tokenUsage ?? _sessionTokenUsage;
            }
            else if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
            {
                messages = Messages.ToList();
                apiStart = apiStartMessageId ?? _apiStartMessageId;
                usage = tokenUsage ?? _sessionTokenUsage;
            }
            else if (_streamingOwnerState is not null
                     && string.Equals(_streamingOwnerState.SessionId, sessionId, StringComparison.Ordinal))
            {
                messages = _streamingOwnerState.Messages.ToList();
                apiStart = apiStartMessageId ?? (_streamingOwnerState.ApiStartMessageId > 0
                    ? _streamingOwnerState.ApiStartMessageId
                    : _apiStartMessageId);
                usage = tokenUsage ?? _streamingOwnerState.TokenUsage ?? _sessionTokenUsage;
            }
            else
            {
                messages = [];
                apiStart = apiStartMessageId ?? 0;
                usage = tokenUsage;
            }

            return (Messages: messages, ApiStartMessageId: apiStart, TokenUsage: usage);
        }).ConfigureAwait(false);

        if (capture.Messages.Count == 0 && capture.ApiStartMessageId <= 0 && capture.TokenUsage is null)
            return;

        try
        {
            await _sessionStore.UpdateSessionMetadataAsync(
                    sessionId,
                    capture.Messages,
                    capture.ApiStartMessageId,
                    capture.TokenUsage)
                .ConfigureAwait(false);

            if (!updateTitleUi || !string.Equals(sessionId, _currentSessionId, StringComparison.Ordinal))
                return;

            var newTitle = AiChatSessionStore.DeriveTitleFromMessages(
                ChatHistoryMapper.ToSession(capture.Messages).Messages);
            await UiThreadBridge.InvokeAsync(() =>
            {
                CurrentSessionTitle = newTitle;
                for (var i = 0; i < Sessions.Count; i++)
                {
                    if (Sessions[i].Id != sessionId)
                        continue;

                    var old = Sessions[i];
                    Sessions[i] = new ChatSessionSummary
                    {
                        Id = old.Id,
                        Title = newTitle,
                        UpdatedAtUtc = old.UpdatedAtUtc,
                        SortOrder = old.SortOrder
                    };
                    break;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Exception($"AiPanel PersistSessionMetadata failed session={sessionId}", ex);
        }
    }

    private async Task PersistCompressionRoundAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> tailMessages,
        long apiStartMessageId,
        int retainFromIndex)
    {
        if (string.IsNullOrEmpty(sessionId) || tailMessages.Count == 0)
            return;

        await AppendPendingStableMessagesAsync(sessionId, tailMessages).ConfigureAwait(false);

        var updateFrom = Math.Clamp(retainFromIndex, 0, tailMessages.Count - 1);
        for (var i = 0; i < tailMessages.Count; i++)
        {
            var message = tailMessages[i];
            if (message.Id <= 0 || !ChatHistoryMapper.TryGetPersistableDtos(message, out _))
                continue;
            if (i >= updateFrom || message.Visual == ChatMessageVisual.Hidden)
                await UpdatePersistedMessageInDbAsync(sessionId, message).ConfigureAwait(false);
        }

        await PersistSessionMetadataAsync(
                sessionId,
                apiStartMessageId,
                tokenUsage: null,
                messagesForTitle: tailMessages,
                updateTitleUi: string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
            .ConfigureAwait(false);
    }

    /// <summary>发送结束时落盘归属会话（含切走后仍在后台发送的情况）。</summary>
    private async Task PersistStreamingOwnerSessionAsync(string? ownerSessionId = null)
    {
        var capture = await UiThreadBridge.InvokeAsync(() =>
        {
            var ownerId = ownerSessionId ?? _streamingOwnerSessionId;
            if (string.IsNullOrEmpty(ownerId))
                return (OwnerId: (string?)null, Messages: (List<ChatMessage>?)null, ApiStartMessageId: 0, TokenUsage: (SessionTokenUsageState?)null);

            if (_streamingOwnerState is not null
                && string.Equals(_streamingOwnerState.SessionId, ownerId, StringComparison.Ordinal))
            {
                return (
                    OwnerId: ownerId,
                    Messages: _streamingOwnerState.Messages.ToList(),
                    ApiStartMessageId: _streamingOwnerState.ApiStartMessageId > 0 ? _streamingOwnerState.ApiStartMessageId : _apiStartMessageId,
                    TokenUsage: _streamingOwnerState.TokenUsage ?? _sessionTokenUsage);
            }

            if (string.Equals(_currentSessionId, ownerId, StringComparison.Ordinal))
            {
                return (
                    OwnerId: ownerId,
                    Messages: Messages.ToList(),
                    ApiStartMessageId: _apiStartMessageId,
                    TokenUsage: _sessionTokenUsage);
            }

            return (OwnerId: ownerId, Messages: (List<ChatMessage>?)null, ApiStartMessageId: 0, TokenUsage: (SessionTokenUsageState?)null);
        }).ConfigureAwait(false);

        if (string.IsNullOrEmpty(capture.OwnerId) || capture.Messages is null)
            return;

        var loading = await UiThreadBridge.InvokeAsync(() => _isLoadingHistory).ConfigureAwait(false);
        if (loading)
            return;

        var updateUi = string.Equals(capture.OwnerId, _currentSessionId, StringComparison.Ordinal);
        await AppendPendingStableMessagesAsync(capture.OwnerId, capture.Messages).ConfigureAwait(false);
        await PersistRoundContextUpdatesAsync(capture.OwnerId, capture.Messages).ConfigureAwait(false);
        await PersistSessionMetadataAsync(
                capture.OwnerId,
                capture.ApiStartMessageId,
                capture.TokenUsage,
                messagesForTitle: capture.Messages,
                updateTitleUi: updateUi)
            .ConfigureAwait(false);

        await UiThreadBridge.InvokeAsync(() =>
        {
            if (_streamingOwnerState is not null
                && string.Equals(_streamingOwnerState.SessionId, capture.OwnerId, StringComparison.Ordinal))
                _streamingOwnerState = null;
        }).ConfigureAwait(false);
    }

    private static bool IsEmptyAssistantUiShell(ChatMessage m, string? content = null, string? reasoning = null)
    {
        if (m.IsUser || m.HasToolCall)
            return false;

        var c = content ?? m.Content;
        var r = reasoning ?? m.ReasoningContent;
        return string.IsNullOrWhiteSpace(c) && string.IsNullOrWhiteSpace(r);
    }

    private void CleanupAfterUserStop()
    {
        if (_suppressCancelMessageForRevert)
            return;

        RemoveTrailingOrphanAssistants();
        if (Messages.Count == 0)
            return;

        var last = Messages[^1];
        if (last.IsUser || ChatApiErrorHelper.IsUiOnlyAssistantMessage(last))
            return;

        AddToStreamingSession(new ChatMessage { Role = ChatRole.Assistant, Content = "已取消。" });
    }

    private void RemoveTrailingOrphanAssistants()
    {
        while (Messages.Count > 0)
        {
            var last = Messages[^1];
            if (last.IsUser)
                break;

            if (IsEmptyAssistantUiShell(last) || ChatApiErrorHelper.IsReasoningOnlyAssistant(last))
            {
                Messages.RemoveAt(Messages.Count - 1);
                continue;
            }

            break;
        }
    }

    private void RemoveTrailingEmptyAssistantShells()
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (!IsEmptyAssistantUiShell(Messages[i]))
                break;
            Messages.RemoveAt(i);
        }
    }

    private static ChatMessage CreateToolCallMessage(string toolName, string toolCallId, string argumentsJson)
    {
        var summary = ToolCallDisplayHelper.FormatCommandSummary(toolName, argumentsJson);
        return new ChatMessage
        {
            Role = ChatRole.Assistant,
            ToolName = toolName,
            ToolCallId = toolCallId,
            ToolArgumentsJson = argumentsJson,
            ToolDisplayName = ToolCallDisplayHelper.GetDisplayName(toolName),
            ToolCommand = string.IsNullOrEmpty(summary) ? argumentsJson : summary,
            IsToolRunning = true
        };
    }

    [RelayCommand(CanExecute = nameof(CanSendOrStop))]
    private void SendOrStop()
    {
        if (IsSending)
            CancelSending();
        else
            _ = SendMessageAsync();
    }

    private bool CanSendOrStop() =>
        IsConfigured && (
            (IsSending && !IsCompressingContext) ||
            (!IsReadOnlySession && !string.IsNullOrWhiteSpace(InputText)));

    private void CancelSending()
    {
        _sendCts?.Cancel();
        MarkRunningToolsCancelled();
    }

    /// <summary>Markdown 内链接：在应用内浏览器打开，避免 AI 聊天 WebView 导航离开内嵌页。</summary>
    public void OpenMarkdownLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        url = url.Trim();
        if (url.StartsWith('#'))
            return;

        var lower = url.ToLowerInvariant();
        if (lower.StartsWith("javascript:", StringComparison.Ordinal))
            return;

        if (lower.StartsWith("mailto:", StringComparison.Ordinal)
            || lower.StartsWith("tel:", StringComparison.Ordinal))
        {
            TryLaunchShellUrl(url);
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !Uri.TryCreate("https://" + url, UriKind.Absolute, out uri))
            return;

        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            _eventBus.Publish(new OpenBrowserTabRequestedEvent(uri.ToString()));
            return;
        }

        TryLaunchShellUrl(uri.ToString());
    }

    private static void TryLaunchShellUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiPanel] OpenMarkdownLink failed: {ex.Message}");
        }
    }

    /// <summary>撤销指定用户消息及之后全部对话（WebView 已用 confirm 确认）。</summary>
    public async Task TryRevertMessagesFromUserAsync(string messageUiId)
    {
        if (IsReadOnlySession
            || string.IsNullOrWhiteSpace(messageUiId)
            || string.IsNullOrEmpty(_currentSessionId)
            || SubAgentSessionHub.IsSubAgentSessionId(_currentSessionId))
            return;

        var suppressCancelMessage = IsSending;
        if (suppressCancelMessage)
            _suppressCancelMessageForRevert = true;

        try
        {
            if (IsSending)
            {
                CancelSending();
                await WaitForSendIdleAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }

            long truncateFromId = 0;
            var reverted = await UiThreadBridge.InvokeAsync(() =>
            {
                if (_isLoadingHistory)
                    return false;

                var index = -1;
                for (var i = 0; i < Messages.Count; i++)
                {
                    var m = Messages[i];
                    if (m.Role == ChatRole.User && ChatMessageIds.MatchesUiId(m, messageUiId))
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                    return false;

                truncateFromId = Messages[index].Id;

                ChatMessagesTruncated?.Invoke(this, new ChatMessagesTruncatedEventArgs(messageUiId));

                for (var i = Messages.Count - 1; i >= index; i--)
                    Messages.RemoveAt(i);

                AdjustApiStartMessageIdAfterRevert(index);
                ResetStreamingToolPlaceholders();
                _streamingToolByIndex.Clear();
                _streamingToolByCallId.Clear();
                RefreshTranscriptDisplay();
                TruncateSurfaceRoundsFrom(messageUiId);

                return true;
            }).ConfigureAwait(false);

            if (!reverted)
                return;

            if (truncateFromId > 0)
            {
                await _sessionStore.TruncateSessionMessagesAsync(_currentSessionId, truncateFromId)
                    .ConfigureAwait(false);
                await UiThreadBridge.InvokeAsync(() => PruneDbAppendTrackingFrom(truncateFromId)).ConfigureAwait(false);
            }

            await PersistSessionMetadataAsync(_currentSessionId!).ConfigureAwait(false);
            await RefreshContextTokenUsageAsync().ConfigureAwait(false);
        }
        finally
        {
            if (suppressCancelMessage)
                await UiThreadBridge.InvokeAsync(() => _suppressCancelMessageForRevert = false).ConfigureAwait(false);
        }
    }

    private void TruncateSurfaceRoundsFrom(string messageUiId)
    {
        var roundIndex = -1;
        for (var i = 0; i < SurfaceRounds.Count; i++)
        {
            if (SurfaceRounds[i].UserMessage is { } user
                && ChatMessageIds.MatchesUiId(user, messageUiId))
            {
                roundIndex = i;
                break;
            }
        }

        if (roundIndex < 0)
        {
            RebuildTailSurface();
            return;
        }

        for (var i = SurfaceRounds.Count - 1; i >= roundIndex; i--)
            SurfaceRounds.RemoveAt(i);

        if (_allSurfaceRounds is not null)
        {
            var globalIndex = _surfaceRoundStartIndex + roundIndex;
            if (globalIndex >= 0 && globalIndex < _allSurfaceRounds.Count)
            {
                _allSurfaceRounds.RemoveRange(globalIndex, _allSurfaceRounds.Count - globalIndex);
                if (_surfaceRoundStartIndex > _allSurfaceRounds.Count)
                    _surfaceRoundStartIndex = _allSurfaceRounds.Count;
            }
        }

        OnPropertyChanged(nameof(CanExpandSurfaceRounds));
    }

    [RelayCommand(CanExecute = nameof(CanRevertMessages))]
    private async Task RevertLastUserMessageAsync()
    {
        var messageUiId = await UiThreadBridge.InvokeAsync(() =>
        {
            for (var i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].Role != ChatRole.User)
                    continue;

                return ChatMessageIds.UiId(Messages[i]);
            }

            return (string?)null;
        }).ConfigureAwait(false);

        if (string.IsNullOrEmpty(messageUiId))
            return;

        await TryRevertMessagesFromUserAsync(messageUiId).ConfigureAwait(false);
    }

    private async Task WaitForSendIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var sending = await UiThreadBridge.InvokeAsync(() => IsSending).ConfigureAwait(false);
            if (!sending)
                return;

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private void AdjustApiStartMessageIdAfterRevert(int revertFromIndex)
    {
        if (_apiStartMessageId <= 0)
            return;

        var compressIdx = ChatHistoryMapper.ResolveMessageIndexFromMessageId(Messages, _apiStartMessageId);
        if (revertFromIndex <= compressIdx)
            _apiStartMessageId = 0;
    }

    private void MarkRunningToolsCancelled()
    {
        void Apply()
        {
            foreach (var m in Messages)
            {
                if (!m.IsToolRunning)
                    continue;

                if (string.IsNullOrEmpty(m.ToolOutput))
                    m.ToolOutput = "已取消";
                m.IsToolRunning = false;
            }

            _awaitingToolCallUi = false;
            RefreshWaitingForReplyState();
            _transcriptDisplay.RequestRefresh();
        }

        UiThreadBridge.PostBackground(Apply);
    }

    /// <summary>手动停止或异常中断时结束流式占位，避免 IsStreaming 卡住 WebView 同步与 SQLite 持久化。</summary>
    private void FinalizeInterruptedStreamMessages()
    {
        foreach (var m in Messages)
        {
            if (!m.IsStreaming && !m.IsThinking)
                continue;

            FinishThinking(m);
            m.IsStreaming = false;
        }
    }

    private static void FinishThinking(ChatMessage message)
    {
        if (message.IsThinking)
            message.IsThinking = false;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsSending || IsReadOnlySession) return;

        var userText = InputText.Trim();
        var ownerSessionId = await UiThreadBridge.InvokeAsync(() => _currentSessionId).ConfigureAwait(false);
        var userMessage = new ChatMessage { Role = ChatRole.User, Content = userText };

        if (!string.IsNullOrEmpty(ownerSessionId))
            await AppendStableMessageToDbAsync(ownerSessionId, userMessage).ConfigureAwait(false);

        await UiThreadBridge.InvokeAsync(() =>
        {
            InputText = string.Empty;
            _streamingOwnerSessionId = _currentSessionId;
            IsSending = true;
            AddToStreamingSession(userMessage);
            _awaitingFirstSseLine = true;
            StartTranscriptPump();
            RefreshWaitingForReplyState();
            SendOrStopCommand.NotifyCanExecuteChanged();
        }).ConfigureAwait(false);

        _sendCts = new CancellationTokenSource();
        var wasCancelled = false;

        try
        {
            var config = _settingsService.Load().Ai;
            if (!string.IsNullOrEmpty(SelectedModel))
                config.Model = SelectedModel;
            await UiThreadBridge.InvokeAsync(ResetStaleApiTokenUsageForLocalEstimate).ConfigureAwait(false);
            using (_sessionContext.BeginSessionScope(ownerSessionId))
            using (_runContext.BeginScope(_sendCts.Token, EnableThinking, SelectedModel))
            {
                await CallChatApiStreamingAsync(config, _sendCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            _eventBus.Publish(new StatusMessageEvent("AI 回复已取消。"));
            MarkRunningToolsCancelled();
        }
        catch (Exception ex)
        {
            _eventBus.Publish(new StatusMessageEvent($"AI 回复中断: {ChatApiErrorHelper.FormatException(ex)}"));
            AddToStreamingSession(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = $"[错误] {ChatApiErrorHelper.FormatException(ex)}"
            });
        }
        finally
        {
            StopTranscriptPump();
            await UiThreadBridge.InvokeAsync(() =>
            {
                FinalizeInterruptedStreamMessages();
                _awaitingFirstSseLine = false;
                _awaitingToolCallUi = false;
                if (wasCancelled)
                {
                    CleanupAfterUserStop();
                    MarkRunningToolsCancelled();
                }
                ResetStreamingToolPlaceholders();
                RefreshTranscriptDisplay();
            }).ConfigureAwait(false);

            await UiThreadBridge.InvokeAsync(() =>
            {
                IsSending = false;
                RefreshWaitingForReplyState();
                _sendCts = null;
            }).ConfigureAwait(false);

            try
            {
                var usageConfig = _settingsService.Load().Ai;
                if (!string.IsNullOrEmpty(SelectedModel))
                    usageConfig.Model = SelectedModel;
                var modelId = !string.IsNullOrEmpty(SelectedModel) ? SelectedModel : usageConfig.Model;
                await UiThreadBridge.InvokeAsync(() =>
                    UpdateLatestRoundContextTokenCounts(
                        ownerSessionId,
                        modelId,
                        ChatSystemPrompt.Build(_lookService.WorkspaceDirectory),
                        allowApiUsage: true)).ConfigureAwait(false);
            }
            catch
            {
                // token 快照失败不影响消息保存和下一轮发送
            }

            var compressed = false;
            try
            {
                var config = _settingsService.Load().Ai;
                if (!string.IsNullOrEmpty(SelectedModel))
                    config.Model = SelectedModel;
                if (!string.IsNullOrEmpty(ownerSessionId))
                {
                    compressed = await TryCompressSessionAfterRoundAsync(ownerSessionId, config, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // 压缩失败不阻断恢复可发送状态
            }

            if (!compressed)
            {
                var ownerMessages = await GetOwnerMessagesForDbAsync().ConfigureAwait(false);
                await AppendPendingStableMessagesAsync(ownerSessionId!, ownerMessages).ConfigureAwait(false);
                await PersistRoundContextUpdatesAsync(ownerSessionId!, ownerMessages).ConfigureAwait(false);
                await PersistSessionMetadataAsync(ownerSessionId!).ConfigureAwait(false);
            }

            await RefreshContextTokenUsageAsync().ConfigureAwait(false);

            await UiThreadBridge.InvokeAsync(() => _streamingOwnerSessionId = null).ConfigureAwait(false);
        }
    }
    private async Task<bool> TryCompressSessionAfterRoundAsync(
        string sessionId,
        AiSettings config,
        CancellationToken cancellationToken)
    {
        var modelId = !string.IsNullOrEmpty(SelectedModel) ? SelectedModel : config.Model;
        var contextLimit = AiEndpointCatalog.ResolveContextTokens(config, config.ApiBaseUrl, modelId);

        CompressionPrepResult? prep = null;
        try
        {
            prep = await UiThreadBridge.InvokeAsync(() => PrepareCompressionRound(
                    sessionId,
                    modelId,
                    contextLimit,
                    config.ContextCompressionThresholdPercent))
                .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        if (prep is null)
            return false;

        if (prep.CompressUserMessage is { } compressUser)
            await AppendStableMessageToDbAsync(sessionId, compressUser).ConfigureAwait(false);

        _sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var compressOk = false;

        await UiThreadBridge.InvokeAsync(() =>
        {
            IsCompressingContext = true;
            _awaitingFirstSseLine = true;
            IsSending = true;
            _streamingOwnerSessionId = sessionId;
            StartTranscriptPump();
            RefreshWaitingForReplyState();
            SendOrStopCommand.NotifyCanExecuteChanged();
        }).ConfigureAwait(false);

        try
        {
            _eventBus.Publish(new StatusMessageEvent("上下文占用较高，正在压缩较早对话…"));
            _compressionRoundActive = true;
            await UiThreadBridge.InvokeAsync(ResetStaleApiTokenUsageForLocalEstimate).ConfigureAwait(false);
            await UiThreadBridge.InvokeAsync(() => _activeCompressionPrep = prep).ConfigureAwait(false);
            using (_sessionContext.BeginSessionScope(sessionId))
            using (_runContext.BeginScope(_sendCts.Token, enableThinking: false, SelectedModel))
            {
                await CallChatApiStreamingAsync(config, _sendCts.Token, compressionRound: true)
                    .ConfigureAwait(false);
            }

            var finalize = await UiThreadBridge.InvokeAsync(() =>
            {
                var summaryText = FinalizeCompressionRound(sessionId, prep, out var ok, out var retainIdx);
                return (Summary: summaryText, Success: ok, RetainFromIndex: retainIdx);
            }).ConfigureAwait(false);

            compressOk = finalize.Success;
            if (!compressOk || string.IsNullOrWhiteSpace(finalize.Summary))
            {
                _eventBus.Publish(new StatusMessageEvent("上下文压缩未完成，已保留原始对话。"));
                return false;
            }

            var tailMessages = await UiThreadBridge.InvokeAsync(() =>
                    GetSessionMessagesSnapshot(sessionId))
                .ConfigureAwait(false);

            var apiStartMessageId = prep.BuildApiStartMessageId(finalize.RetainFromIndex, tailMessages);

            var compressionSystemPrompt = await UiThreadBridge.InvokeAsync(() =>
                    ChatSystemPrompt.Build(_lookService.WorkspaceDirectory))
                .ConfigureAwait(false);

            var persistPlan = await UiThreadBridge.InvokeAsync(() =>
            {
                if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
                {
                    ResetStaleApiTokenUsageForLocalEstimate();
                    _apiStartMessageId = apiStartMessageId;
                    UpdateCompressedContextTokenCounts(
                        sessionId,
                        ChatHistoryMapper.ResolveMessageIndexFromMessageId(Messages, apiStartMessageId),
                        modelId,
                        compressionSystemPrompt);
                    _transcriptDisplay.CancelPending();
                    RefreshTranscriptDisplay();
                    _surfaceManager.Clear();
                    RebuildTailSurface(notifyWebView: true);
                }
                else if (_streamingOwnerState is not null
                         && string.Equals(_streamingOwnerState.SessionId, sessionId, StringComparison.Ordinal))
                {
                    _streamingOwnerState.ApiStartMessageId = apiStartMessageId;
                    UpdateCompressedContextTokenCounts(
                        sessionId,
                        ChatHistoryMapper.ResolveMessageIndexFromMessageId(
                            _streamingOwnerState.Messages,
                            apiStartMessageId),
                        modelId,
                        compressionSystemPrompt);
                }

                return GetSessionMessagesSnapshot(sessionId);
            }).ConfigureAwait(false);

            if (persistPlan.Count > 0)
            {
                await PersistCompressionRoundAsync(
                        sessionId,
                        persistPlan,
                        apiStartMessageId,
                        finalize.RetainFromIndex)
                    .ConfigureAwait(false);
            }

            _eventBus.Publish(new StatusMessageEvent("上下文压缩已完成。"));
            compressOk = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            await PersistStreamingOwnerSessionAsync(sessionId).ConfigureAwait(false);
            _eventBus.Publish(new StatusMessageEvent("上下文压缩已取消。"));
            return false;
        }
        catch
        {
            await PersistStreamingOwnerSessionAsync(sessionId).ConfigureAwait(false);
            _eventBus.Publish(new StatusMessageEvent("上下文压缩失败，已保留原始对话。"));
            return false;
        }
        finally
        {
            _compressionRoundActive = false;
            await UiThreadBridge.InvokeAsync(() => _activeCompressionPrep = null).ConfigureAwait(false);
            StopTranscriptPump();
            var shouldRefreshTokens = compressOk;
            await UiThreadBridge.InvokeAsync(() =>
            {
                FinalizeInterruptedStreamMessages();
                _awaitingFirstSseLine = false;
                _awaitingToolCallUi = false;
                ResetStreamingToolPlaceholders();
                IsSending = false;
                IsCompressingContext = false;
                RefreshWaitingForReplyState();
                SendOrStopCommand.NotifyCanExecuteChanged();
                _sendCts?.Dispose();
                _sendCts = null;
                if (compressOk && string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
                    RequestChatSurfaceResync();
            }).ConfigureAwait(false);

            if (shouldRefreshTokens)
                await RefreshContextTokenUsageAsync().ConfigureAwait(false);
        }
    }

    private sealed class CompressionPrepResult
    {
        public required string SessionId { get; init; }
        public required int CompressUserIndex { get; init; }
        public required ChatMessage CompressUserMessage { get; init; }

        public long BuildApiStartMessageId(int retainFromIndex, IReadOnlyList<ChatMessage> messages)
        {
            if (retainFromIndex < 0 || retainFromIndex >= messages.Count)
                return 0;
            return messages[retainFromIndex].Id;
        }
    }

    private ChatMessage CreateStreamingAssistantMessage(bool thinking = false) =>
        new()
        {
            Role = ChatRole.Assistant,
            Content = "",
            IsStreaming = true,
            IsThinking = thinking
        };

    private CompressionPrepResult? PrepareCompressionRound(
        string sessionId,
        string modelId,
        int contextLimit,
        int compressionThresholdPercent)
    {
        if (!TryGetWritableMessages(sessionId, out var messages, out var apiStartMessageId))
            return null;

        IReadOnlyList<ChatMessage> readonlyMessages = messages is IReadOnlyList<ChatMessage> ro
            ? ro
            : messages.ToList();
        var systemPrompt = ChatSystemPrompt.Build(_lookService.WorkspaceDirectory);
        var measuredPromptTokens = ResolveCompressionPromptTokens(sessionId, modelId);

        if (!ChatContextCompressionService.ShouldCompress(
                readonlyMessages,
                systemPrompt,
                modelId,
                contextLimit,
                apiStartMessageId,
                measuredPromptTokens,
                compressionThresholdPercent))
            return null;

        var compressUserIndex = messages.Count;

        var compressUser = new ChatMessage
        {
            Role = ChatRole.User,
            Content = ChatContextCompressionService.CompressionInstruction,
            Visual = ChatMessageVisual.Hidden
        };
        compressUser.TranscriptSink = _transcriptDisplay;
        messages.Add(compressUser);
        _transcriptDisplay.RequestRefresh();

        return new CompressionPrepResult
        {
            SessionId = sessionId,
            CompressUserIndex = compressUserIndex,
            CompressUserMessage = compressUser
        };
    }

    private int? ResolveCompressionPromptTokens(string sessionId, string modelId)
    {
        SessionTokenUsageState? usage = null;
        if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
            usage = _sessionTokenUsage;
        else if (_streamingOwnerState is not null
                 && string.Equals(_streamingOwnerState.SessionId, sessionId, StringComparison.Ordinal))
            usage = _streamingOwnerState.TokenUsage;

        if (usage is { IsApiMeasured: true }
            && usage.TryGetUsablePromptTokens(modelId, out var storedPrompt))
            return storedPrompt;

        if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal)
            && _lastApiPromptTokens is int prompt and > 0)
            return prompt;

        return null;
    }

    private string? FinalizeCompressionRound(
        string sessionId,
        CompressionPrepResult prep,
        out bool success,
        out int retainFromIndex)
    {
        success = false;
        retainFromIndex = -1;
        if (!TryGetWritableMessages(sessionId, out var messages, out _))
            return null;

        retainFromIndex = ResolveCompressionRoundIndex(messages, prep);
        if (retainFromIndex < 0)
            return null;

        var summary = ExtractCompressionSummaryText(messages, retainFromIndex);
        if (string.IsNullOrWhiteSpace(summary))
            return null;

        ChatMessage? summaryAssistant = null;
        for (var i = messages.Count - 1; i > retainFromIndex; i--)
        {
            var message = messages[i];
            if (message.HasToolCall || message.IsUser)
                continue;

            if (!string.IsNullOrWhiteSpace(message.Content) || !string.IsNullOrWhiteSpace(message.ReasoningContent))
            {
                summaryAssistant = message;
                break;
            }
        }

        if (summaryAssistant is null)
            return null;

        ApplyCompressionRoundHiddenFlags(messages, retainFromIndex, summaryAssistant);

        success = true;
        return summary;
    }

    private static int ResolveCompressionRoundIndex(IList<ChatMessage> messages, CompressionPrepResult prep)
    {
        var byRef = messages.IndexOf(prep.CompressUserMessage);
        if (byRef >= 0)
            return byRef;

        if (prep.CompressUserIndex >= 0
            && prep.CompressUserIndex < messages.Count
            && ReferenceEquals(messages[prep.CompressUserIndex], prep.CompressUserMessage))
            return prep.CompressUserIndex;

        return -1;
    }

    private void ApplyCompressionRoundHiddenFlags(
        IList<ChatMessage> messages,
        int compressIdx,
        ChatMessage summaryAssistant)
    {
        MarkCompressionUiHidden(messages[compressIdx]);

        for (var i = compressIdx + 1; i < messages.Count; i++)
        {
            var message = messages[i];
            if (ReferenceEquals(message, summaryAssistant))
            {
                MarkCompressionUiHidden(message);
                break;
            }

            if (message.IsUser || message.HasToolCall)
                break;

            if (IsEmptyAssistantUiShell(message))
                MarkCompressionUiHidden(message);
            else
                break;
        }

        _transcriptDisplay.RequestRefresh();
    }

    private static void MarkCompressionUiHidden(ChatMessage message)
    {
        message.Visual = ChatMessageVisual.Hidden;
        message.IsStreaming = false;
        message.IsThinking = false;
    }

    private static string? ExtractCompressionSummaryText(IList<ChatMessage> messages, int afterIndex)
    {
        for (var i = messages.Count - 1; i > afterIndex; i--)
        {
            var message = messages[i];
            if (message.HasToolCall || message.IsUser)
                continue;

            if (!string.IsNullOrWhiteSpace(message.Content))
                return message.Content;

            if (!string.IsNullOrWhiteSpace(message.ReasoningContent))
                return message.ReasoningContent;
        }

        return null;
    }

    private bool TryGetWritableMessages(
        string sessionId,
        out IList<ChatMessage> messages,
        out long apiStartMessageId)
    {
        messages = null!;
        apiStartMessageId = 0;
        if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
        {
            messages = Messages;
            apiStartMessageId = _apiStartMessageId;
            return true;
        }

        if (_streamingOwnerState is not null
            && string.Equals(_streamingOwnerState.SessionId, sessionId, StringComparison.Ordinal))
        {
            messages = _streamingOwnerState.Messages;
            apiStartMessageId = _streamingOwnerState.ApiStartMessageId;
            return true;
        }

        return false;
    }

    private IReadOnlyList<ChatMessage> GetSessionMessagesSnapshot(string sessionId)
    {
        if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
            return Messages.ToArray();

        if (_streamingOwnerState is not null
            && string.Equals(_streamingOwnerState.SessionId, sessionId, StringComparison.Ordinal))
            return _streamingOwnerState.Messages.ToArray();

        return Array.Empty<ChatMessage>();
    }

    private async Task TryCompressContextAfterRoundAsync(AiSettings config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_currentSessionId))
            return;

        await TryCompressSessionAfterRoundAsync(_currentSessionId, config, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyList<ChatMessage> Messages, long ApiStartMessageId)> ResolveSessionMessagesForOwnerAsync(
        string sessionId)
    {
        var uiCapture = await UiThreadBridge.InvokeAsync(() =>
        {
            if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
                return (Messages: (IReadOnlyList<ChatMessage>)Messages, ApiStartMessageId: _apiStartMessageId, FromUi: true);

            if (_streamingOwnerState is not null
                && string.Equals(_streamingOwnerState.SessionId, sessionId, StringComparison.Ordinal))
            {
                return (
                    Messages: (IReadOnlyList<ChatMessage>)_streamingOwnerState.Messages,
                    ApiStartMessageId: _streamingOwnerState.ApiStartMessageId,
                    FromUi: true);
            }

            return (Messages: (IReadOnlyList<ChatMessage>?)null, ApiStartMessageId: 0, FromUi: false);
        }).ConfigureAwait(false);

        if (uiCapture.FromUi && uiCapture.Messages is not null)
            return (uiCapture.Messages, uiCapture.ApiStartMessageId);

        var data = await _sessionStore.LoadSessionAsync(sessionId).ConfigureAwait(false);
        if (data is null)
            return ([], 0);

        var mapped = await Task.Run(() =>
                ChatHistoryMapper.FromSession(new ChatSessionDto { Messages = data.Messages }).ToList())
            .ConfigureAwait(false);
        return (mapped, data.ApiStartMessageId);
    }

    private async Task<List<ChatMessage>> ResolveActiveSendMessagesAsync()
    {
        var list = await UiThreadBridge.InvokeAsync(() =>
        {
            if (IsSending
                && !string.IsNullOrEmpty(_streamingOwnerSessionId)
                && !string.Equals(_currentSessionId, _streamingOwnerSessionId, StringComparison.Ordinal)
                && _streamingOwnerState is not null
                && string.Equals(_streamingOwnerState.SessionId, _streamingOwnerSessionId, StringComparison.Ordinal))
                return _streamingOwnerState.Messages.ToList();

            return Messages.ToList();
        }).ConfigureAwait(false);
        return list;
    }

    private void RefreshContextTokenUsage()
    {
        var generation = Interlocked.Increment(ref _tokenRefreshGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _tokenRefreshGeneration))
                    return;

                await RefreshContextTokenUsageAsync().ConfigureAwait(false);
            }
            catch
            {
                // token 刷新失败不阻断
            }
        });
    }

    private async Task RefreshContextTokenUsageAsync()
    {
        var flags = await UiThreadBridge.InvokeAsync(() => (IsSending, IsCompressingContext)).ConfigureAwait(false);
        if (flags.IsSending || flags.IsCompressingContext)
            return;

        TokenUsageUiCapture? capture;
        try
        {
            capture = await CaptureTokenUsageUiCaptureAsync().ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (capture is null)
            return;

        TokenUsageUiResult result;
        try
        {
            result = await Task.Run(() =>
            {
                var state = BuildTokenUsageUiState(capture);
                return state is null
                    ? new TokenUsageUiResult(0, string.Empty)
                    : _contextUsageService.ComputeUiResult(state);
            }).ConfigureAwait(false);
        }
        catch
        {
            result = new TokenUsageUiResult(0, string.Empty);
        }

        try
        {
            await UiThreadBridge.InvokeAsync(() =>
            {
                ContextTokenUsagePercent = result.Percent;
                ContextTokenUsageDetails = result.Details;
            }).ConfigureAwait(false);
        }
        catch
        {
            // 面板已卸载
        }
    }

    private sealed record TokenUsageUiCapture(
        IReadOnlyList<ChatMessage> Messages,
        string SelectedModel,
        bool IsCompressingContext,
        long ApiStartMessageId,
        SessionTokenUsageState? SessionTokenUsage,
        int? LastApiPromptTokens,
        int? LastApiCachedPromptTokens,
        int? LastApiCompletionTokens,
        bool LastApiRoundCompleted,
        bool LastRequestIncludedUsage,
        string WorkspaceDirectory);

    private async Task<TokenUsageUiCapture?> CaptureTokenUsageUiCaptureAsync()
    {
        var uiState = await UiThreadBridge.InvokeAsync(() => (
            SelectedModel,
            IsCompressingContext,
            ApiStartMessageId: _apiStartMessageId,
            SessionTokenUsage: _sessionTokenUsage,
            LastApiPromptTokens: _lastApiPromptTokens,
            LastApiCachedPromptTokens: _lastApiCachedPromptTokens,
            LastApiCompletionTokens: _lastApiCompletionTokens,
            LastApiRoundCompleted: _lastApiRoundCompleted,
            LastRequestIncludedUsage: _lastRequestIncludedUsage,
            WorkspaceDirectory: _lookService.WorkspaceDirectory)).ConfigureAwait(false);

        var messages = await UiThreadBridge.InvokeAsync(() => (IReadOnlyList<ChatMessage>)Messages.ToArray())
            .ConfigureAwait(false);

        return new TokenUsageUiCapture(
            messages,
            uiState.SelectedModel,
            uiState.IsCompressingContext,
            uiState.ApiStartMessageId,
            uiState.SessionTokenUsage,
            uiState.LastApiPromptTokens,
            uiState.LastApiCachedPromptTokens,
            uiState.LastApiCompletionTokens,
            uiState.LastApiRoundCompleted,
            uiState.LastRequestIncludedUsage,
            uiState.WorkspaceDirectory);
    }

    private TokenUsageUiState? BuildTokenUsageUiState(TokenUsageUiCapture capture)
    {
        try
        {
            var config = _settingsService.Load().Ai;
            var modelId = !string.IsNullOrEmpty(capture.SelectedModel) ? capture.SelectedModel : config.Model;
            var limit = _contextUsageService.ResolveContextTokenLimit(config, modelId, out var limitSource);

            return new TokenUsageUiState(
                capture.Messages,
                modelId,
                limit,
                limitSource,
                capture.ApiStartMessageId,
                capture.IsCompressingContext,
                ChatSystemPrompt.Build(capture.WorkspaceDirectory),
                config.ContextCompressionThresholdPercent,
                capture.SessionTokenUsage,
                capture.LastApiPromptTokens,
                capture.LastApiCachedPromptTokens,
                capture.LastApiCompletionTokens,
                capture.LastApiRoundCompleted,
                capture.LastRequestIncludedUsage);
        }
        catch
        {
            return null;
        }
    }


    private async System.Threading.Tasks.Task CallChatApiStreamingAsync(
        AiSettings config,
        CancellationToken cancellationToken,
        bool compressionRound = false)
    {
        var baseUrl = config.ApiBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/chat/completions";
        IReadOnlyList<string> toolDefs = compressionRound
            ? Array.Empty<string>()
            : _toolRegistry.GetToolDefinitions();
        var enableThinking = EnableThinking;
        var sessionId = await UiThreadBridge.InvokeAsync(() => _streamingOwnerSessionId ?? _currentSessionId)
            .ConfigureAwait(false) ?? string.Empty;

        for (int round = 0; round < ChatSendOrchestrator.MaxToolRounds; round++)
        {
            try
            {
                BeginAwaitingFirstSseLine();

                var apiMessages = await BuildApiMessagesAsync(config, compressionRound).ConfigureAwait(false);
                var requestBody = _chatSendOrchestrator.BuildStreamingRequestBody(
                    config,
                    apiMessages,
                    toolDefs,
                    enableThinking,
                    disableTools: compressionRound);

                using var response = await SendStreamingChatCompletionAsync(
                    config, url, requestBody, cancellationToken).ConfigureAwait(false);

                _lastRequestIncludedUsage = ChatCompletionStreamOptions.ShouldIncludeUsage(config.ApiBaseUrl);

                var (content, reasoningContent, toolCalls) = await ParseSseStreamAsync(response, cancellationToken)
                    .ConfigureAwait(false);

                if (toolCalls != null && toolCalls.Count > 0)
                {
                    await UiThreadBridge.InvokeAsync(RemoveTrailingEmptyAssistantShells).ConfigureAwait(false);

                    var ownerMessages = await GetOwnerMessagesForDbAsync().ConfigureAwait(false);
                    await FinalizeStreamingFlagsForPersistAsync(ownerMessages).ConfigureAwait(false);
                    await AppendNewStableTailToDbAsync(sessionId, ownerMessages).ConfigureAwait(false);

                    var browserToolRound = toolCalls.Any(tc => BrowserUiGate.IsBrowserStackTool(tc.Name));
                    if (browserToolRound)
                        BrowserUiGate.EnterBrowserToolRound();

                    try
                    {
                    for (var ti = 0; ti < toolCalls.Count; ti++)
                    {
                        var tc = toolCalls[ti];
                        cancellationToken.ThrowIfCancellationRequested();

                        var tcArguments = await UiThreadBridge.InvokeAsync(() => ResolveToolCallArguments(tc, ti))
                            .ConfigureAwait(false);
                        ChatMessage toolMsg;
                        if (TryResolveStreamingToolMessage(tc, ti, out var existing))
                        {
                            toolMsg = existing;
                            await UiThreadBridge.InvokeAsync(() =>
                            {
                                if (!string.Equals(toolMsg.ToolArgumentsJson, tcArguments, StringComparison.Ordinal))
                                    toolMsg.ToolArgumentsJson = tcArguments;
                            }).ConfigureAwait(false);
                        }
                        else
                        {
                            toolMsg = CreateToolCallMessage(tc.Name, tc.Id, tcArguments);
                            await UiThreadBridge.InvokeAsync(() => AddToStreamingSession(toolMsg)).ConfigureAwait(false);
                            await AppendStableMessageToDbAsync(sessionId, toolMsg).ConfigureAwait(false);
                        }

                        EndAwaitingToolCallUi();

                        ToolCallResult result;
                        try
                        {
                            result = await AiToolUiDispatcher.ExecuteAsync(_toolRegistry, tc.Name, tcArguments, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _eventBus.Publish(new StatusMessageEvent($"工具 {tc.Name} 执行异常，已把错误返回给模型继续处理。"));
                            result = new ToolCallResult(
                                ToolResultJson.Error($"工具执行异常: {ex.Message}"),
                                tc.Name,
                                exitCode: 1);
                        }

                        var exitCode = result.ExitCode;
                        if (exitCode == 0 && ToolCallDisplayHelper.LooksLikeErrorResult(result.ResultText))
                            exitCode = 1;

                        var toolContentForApi = result.ResultText ?? string.Empty;
                        await UiThreadBridge.InvokeAsync(() =>
                        {
                            toolMsg.ToolOutput = result.ResultText ?? string.Empty;
                            toolMsg.ToolExitCode = exitCode;
                            toolMsg.IsToolRunning = false;

                            IReadOnlyList<ChatMessage> ownerList = !string.IsNullOrEmpty(_streamingOwnerSessionId)
                                && !string.Equals(_currentSessionId, _streamingOwnerSessionId, StringComparison.Ordinal)
                                ? GetStreamingOwnerMessages()
                                : Messages;
                            var ownerIndex = ownerList is IList<ChatMessage> mutable
                                ? mutable.IndexOf(toolMsg)
                                : ownerList.ToList().IndexOf(toolMsg);
                            var messageId = ChatHistoryMapper.GetToolMessageId(ownerList, ownerIndex);
                            var path = ChatContextCompressionService.BuildToolResultPath(messageId);
                            _toolResultRuntimeStore.Upsert(messageId, toolMsg.ToolOutput);
                            toolContentForApi = ToolResultContextProjection.ProjectForApi(
                                toolMsg.ToolOutput,
                                config.Model,
                                path);

                            _transcriptDisplay.RequestRefresh();
                        }).ConfigureAwait(false);

                        await UpdatePersistedMessageInDbAsync(sessionId, toolMsg).ConfigureAwait(false);
                    }
                    }
                    finally
                    {
                        if (browserToolRound)
                            BrowserUiGate.ExitBrowserToolRound();
                    }

                    ResetStreamingToolPlaceholders();
                    await UiThreadBridge.InvokeAsync(() =>
                    {
                        _awaitingToolCallUi = false;
                        RefreshWaitingForReplyState();
                    }).ConfigureAwait(false);
                    continue;
                }

                var tailMessages = await GetOwnerMessagesForDbAsync().ConfigureAwait(false);
                await FinalizeStreamingFlagsForPersistAsync(tailMessages).ConfigureAwait(false);
                await AppendNewStableTailToDbAsync(sessionId, tailMessages).ConfigureAwait(false);

                if (!HasVisibleAssistantReply(content, reasoningContent))
                {
                    var hint = round > 0
                        ? "操作已完成。"
                        : "（模型未返回正文，请检查 API 地址/密钥/模型。）";
                    ChatMessage? hintMsg = null;
                    await UiThreadBridge.InvokeAsync(() =>
                    {
                        hintMsg = new ChatMessage { Role = ChatRole.Assistant, Content = hint };
                        AddToStreamingSession(hintMsg);
                    }).ConfigureAwait(false);
                    if (hintMsg is not null)
                        await AppendStableMessageToDbAsync(sessionId, hintMsg).ConfigureAwait(false);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw;
            }
        }

        var limitMessage = _chatSendOrchestrator.BuildToolRoundLimitMessage();
        _eventBus.Publish(new StatusMessageEvent(limitMessage));
        ChatMessage? limitMsg = null;
        await UiThreadBridge.InvokeAsync(() =>
        {
            limitMsg = new ChatMessage { Role = ChatRole.Assistant, Content = limitMessage };
            AddToStreamingSession(limitMsg);
        }).ConfigureAwait(false);
        if (limitMsg is not null)
            await AppendStableMessageToDbAsync(sessionId, limitMsg).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task<(string content, string reasoningContent, List<ToolCallInfo>? toolCalls)> ParseSseStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ResetStreamingToolPlaceholders();

        var sb = new StringBuilder();
        var reasoningSb = new StringBuilder();
        var nonSseBuffer = new StringBuilder();
        var sawSsePayload = false;
        ChatMessage? currentMsg = null;
        var toolCallBuilders = new Dictionary<int, ToolCallBuilder>();
        var toolCallList = new List<ToolCallInfo>();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null) break;
            if (string.IsNullOrEmpty(line)) continue;
            if (!TryGetSseDataPayload(line, out var data))
            {
                // 兼容少数网关：stream=true 但返回的是普通 JSON（可能多行），先整段缓存，循环结束后一次性解析。
                nonSseBuffer.AppendLine(line);
                if (!string.IsNullOrWhiteSpace(line))
                    MarkFirstSseLineReceived();
                continue;
            }

            MarkFirstSseLineReceived();
            sawSsePayload = true;

            if (string.Equals(data.Trim(), "[DONE]", StringComparison.Ordinal))
                break;

            data = data.TrimStart();

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                TryCaptureUsageFromResponse(root);

                if (root.TryGetProperty("error", out var errEl))
                {
                    var errMsg = errEl.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String
                        ? em.GetString()
                        : errEl.GetRawText();
                    throw new InvalidOperationException($"API 错误: {errMsg}");
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;

                var choice = choices[0];

                // 多数 OpenAI 兼容实现会在最后一包只带 finish_reason、不带 delta；GetProperty("delta") 会抛 KeyNotFoundException 导致整轮对话失败。
                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (EnableThinking
                        && delta.TryGetProperty("reasoning_content", out var reasoningProp))
                    {
                        var reasoningChunk = reasoningProp.GetString();
                        if (!string.IsNullOrEmpty(reasoningChunk))
                        {
                            if (currentMsg == null)
                            {
                                currentMsg = CreateStreamingAssistantMessage(thinking: true);
                                var createdMsg = currentMsg;
                                UiThreadBridge.Post(() => AddToStreamingSession(createdMsg));
                            }

                            var reasoningAppended = ChatCompletionStreamText.AppendSuffix(reasoningSb, reasoningChunk);
                            if (reasoningAppended is not null)
                            {
                                var capturedChunk = reasoningAppended;
                                UiThreadBridge.Post(() => currentMsg!.AppendReasoningText(capturedChunk));
                            }
                        }
                    }

                    string? textChunk = null;
                    if (delta.TryGetProperty("content", out var contentProp))
                        textChunk = contentProp.GetString();
                    else if (delta.TryGetProperty("text", out var textProp))
                        textChunk = textProp.GetString();

                    if (textChunk != null && textChunk.Length == 0
                        && _sawAssistantContentThisStream
                        && toolCallBuilders.Count == 0
                        && _streamingToolByIndex.Count == 0)
                    {
                        BeginAwaitingToolCallUi();
                    }

                    if (!string.IsNullOrEmpty(textChunk))
                    {
                        var chunk = textChunk;
                        _sawAssistantContentThisStream = true;
                        if (currentMsg == null)
                        {
                            currentMsg = CreateStreamingAssistantMessage();
                            var createdMsg = currentMsg;
                            UiThreadBridge.PostBackground(() => AddToStreamingSession(createdMsg));
                        }

                        var appended = ChatCompletionStreamText.AppendSuffix(sb, chunk);
                        if (appended is not null)
                        {
                            var capturedChunk = appended;
                            UiThreadBridge.Post(() =>
                            {
                                if (currentMsg!.IsThinking)
                                    FinishThinking(currentMsg);
                                currentMsg.AppendStreamingText(capturedChunk);
                            });
                        }
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCallsDelta))
                    {
                        BeginAwaitingToolCallUi();
                        foreach (var tc in toolCallsDelta.EnumerateArray())
                        {
                            var callIndex = 0;
                            if (tc.TryGetProperty("index", out var indexProp) && indexProp.ValueKind == JsonValueKind.Number)
                                callIndex = indexProp.GetInt32();

                            if (!toolCallBuilders.TryGetValue(callIndex, out var builder))
                            {
                                builder = new ToolCallBuilder();
                                toolCallBuilders[callIndex] = builder;
                            }

                            // 流式 tool_calls 后续分片常带 id:""、仅含 arguments 片段；禁止用空串覆盖首个分片里的真实 call id / 函数名（如 Qwen 网关）。
                            if (tc.TryGetProperty("id", out var idProp))
                            {
                                var id = idProp.GetString();
                                if (!string.IsNullOrWhiteSpace(id))
                                    builder.Id = id;
                            }

                            if (tc.TryGetProperty("function", out var funcProp))
                            {
                                if (funcProp.TryGetProperty("name", out var nameProp))
                                {
                                    var fn = nameProp.GetString();
                                    if (!string.IsNullOrWhiteSpace(fn))
                                        builder.Name = fn;
                                }

                                if (funcProp.TryGetProperty("arguments", out var argsProp))
                                    builder.Arguments += argsProp.GetString() ?? "";
                            }

                            EnsureStreamingToolPlaceholder(callIndex, builder);
                        }
                    }
                }

                // finish_reason 可能在 choice 上，也可能只在 delta 里；不少中转从不返回 tool_calls，只在最后给 stop。
                string? finishReason = null;
                if (choice.TryGetProperty("finish_reason", out var finishProp))
                    finishReason = finishProp.GetString();
                else if (choice.TryGetProperty("delta", out var deltaForFr)
                         && deltaForFr.TryGetProperty("finish_reason", out var deltaFinishProp))
                    finishReason = deltaFinishProp.GetString();

                if (IsStreamingToolFinishReason(finishReason))
                    SyncStreamingToolPlaceholdersFromBuilders(toolCallBuilders);

                // 少数中转在流式响应里把正文放在 choices[0].message.content（非 delta），第二轮汇总答复常见。
                if (choice.TryGetProperty("message", out var messageEl)
                    && messageEl.ValueKind == JsonValueKind.Object
                    && messageEl.TryGetProperty("content", out var msgContentEl))
                {
                    var msgText = msgContentEl.GetString();
                    if (!string.IsNullOrEmpty(msgText))
                    {
                        var appended = ChatCompletionStreamText.AppendSuffix(sb, msgText);
                        if (appended is not null)
                        {
                            _sawAssistantContentThisStream = true;
                            if (currentMsg == null)
                            {
                                currentMsg = CreateStreamingAssistantMessage();
                                var createdMsg = currentMsg;
                                UiThreadBridge.Post(() => AddToStreamingSession(createdMsg));
                            }

                            var captured = appended;
                            UiThreadBridge.Post(() =>
                            {
                                if (currentMsg!.IsThinking)
                                    FinishThinking(currentMsg);
                                currentMsg.AppendStreamingText(captured);
                            });
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException)
            {
                // 单行非 JSON（心跳、注释）忽略
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception)
            {
                // 避免单行损坏导致整轮静默失败
            }
        }
        // 兼容：某些网关在 stream=true 下返回的是完整 JSON（非 SSE）。
        if (!sawSsePayload && nonSseBuffer.Length > 0)
        {
            var fullJson = nonSseBuffer.ToString().Trim();
            try
            {
                using var doc = JsonDocument.Parse(fullJson);
                var root = doc.RootElement;

                TryCaptureUsageFromResponse(root);

                if (root.TryGetProperty("error", out var errEl))
                {
                    var errMsg = errEl.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String
                        ? em.GetString()
                        : errEl.GetRawText();
                    throw new InvalidOperationException($"API 错误: {errMsg}");
                }

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];

                    if (choice.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.Object)
                    {
                        if (messageEl.TryGetProperty("content", out var msgContentEl))
                        {
                            var msgText = msgContentEl.GetString();
                            if (!string.IsNullOrEmpty(msgText))
                                ChatCompletionStreamText.AppendSuffix(sb, msgText);
                        }

                        if (messageEl.TryGetProperty("reasoning_content", out var msgReasoningEl))
                        {
                            var msgReasoning = msgReasoningEl.GetString();
                            if (!string.IsNullOrEmpty(msgReasoning))
                                ChatCompletionStreamText.AppendSuffix(reasoningSb, msgReasoning);
                        }

                        if (messageEl.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
                        {
                            var fallbackToolIndex = 0;
                            foreach (var tc in toolCallsEl.EnumerateArray())
                            {
                                var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                                if (!tc.TryGetProperty("function", out var fnEl) || fnEl.ValueKind != JsonValueKind.Object)
                                {
                                    fallbackToolIndex++;
                                    continue;
                                }
                                var name = fnEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                                if (string.IsNullOrWhiteSpace(name))
                                {
                                    fallbackToolIndex++;
                                    continue;
                                }
                                var args = fnEl.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() ?? "" : "";
                                toolCallList.Add(new ToolCallInfo
                                {
                                    Id = string.IsNullOrWhiteSpace(id) ? BuildFallbackToolCallId(fallbackToolIndex) : id!,
                                    Name = name!,
                                    Arguments = args
                                });
                                fallbackToolIndex++;
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        // 部分思考模型只向 reasoning_content 输出，delta.content 恒为空；
        // reasoning 必须作为独立 assistant 气泡保留，尤其是「reasoning + tool_calls + 无 content」的轮次。
        var finalContent = sb.ToString();
        var finalReasoning = reasoningSb.ToString();

        if (currentMsg == null
            && (!string.IsNullOrWhiteSpace(finalContent) || !string.IsNullOrWhiteSpace(finalReasoning)))
        {
            currentMsg = CreateStreamingAssistantMessage();
            var shellMsg = currentMsg;
            await UiThreadBridge.InvokeAsync(() => AddToStreamingSession(shellMsg)).ConfigureAwait(false);
        }

        // 流完全结束后再汇总 tool_calls：finish_reason 可能早于 arguments 末片到达，且 message.tool_calls 常与 delta Builders 不一致。
        if (toolCallBuilders.Count > 0 || toolCallList.Count > 0)
        {
            MergeToolCallsFromBuilders(toolCallBuilders, toolCallList);
            await UiThreadBridge.InvokeAsync(() => SyncStreamingToolPlaceholdersFromBuilders(toolCallBuilders))
                .ConfigureAwait(false);
        }

        if (toolCallList.Count > 0 && _streamingToolByIndex.Count > 0)
            EndAwaitingToolCallUi();

        if (toolCallList.Count > 0 && currentMsg != null)
        {
            await UiThreadBridge.InvokeAsync(() =>
            {
                if (IsEmptyAssistantUiShell(currentMsg, finalContent, finalReasoning))
                {
                    FinishThinking(currentMsg);
                    currentMsg.IsStreaming = false;
                    RemoveFromStreamingSessionNow(currentMsg);
                    return;
                }

                ApplyFinalReasoning(currentMsg, finalReasoning);
                FinishThinking(currentMsg);
                currentMsg.IsStreaming = false;

                // 流已结束后再合并正文，避免 MergeFinalContent 在 IsStreaming 时触发最后一次增量推送。
                if (!string.IsNullOrEmpty(finalContent))
                {
                    var merged = ChatCompletionStreamText.MergeFinalContent(currentMsg.Content, finalContent);
                    if (!string.Equals(currentMsg.Content, merged, StringComparison.Ordinal))
                        currentMsg.Content = merged;
                }
            }).ConfigureAwait(false);
        }
        else if (currentMsg != null)
        {
            await UiThreadBridge.InvokeAsync(() =>
            {
                ApplyFinalReasoning(currentMsg, finalReasoning);
                FinishThinking(currentMsg);
                currentMsg.IsStreaming = false;

                if (!string.IsNullOrEmpty(finalContent))
                {
                    var merged = ChatCompletionStreamText.MergeFinalContent(currentMsg.Content, finalContent);
                    if (!string.Equals(currentMsg.Content, merged, StringComparison.Ordinal))
                        currentMsg.Content = merged;
                }
            }).ConfigureAwait(false);
        }

        _lastApiRoundCompleted = true;

        if (!_compressionRoundActive)
        {
            await UiThreadBridge.InvokeAsync(() =>
            {
                var modelId = !string.IsNullOrEmpty(SelectedModel)
                    ? SelectedModel
                    : _settingsService.Load().Ai.Model;
                CommitSessionTokenUsageFromLastApi(modelId);
            }).ConfigureAwait(false);
        }

        return (finalContent, finalReasoning, toolCallList.Count > 0 ? toolCallList : null);
    }

    private static void ApplyFinalReasoning(ChatMessage message, string finalReasoning)
    {
        if (!string.IsNullOrEmpty(finalReasoning) || string.IsNullOrEmpty(message.ReasoningContent))
            message.ReasoningContent = finalReasoning;
    }

    private async Task<HttpResponseMessage> SendStreamingChatCompletionAsync(
        AiSettings config,
        string url,
        JsonObject requestBody,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var body = requestBody.DeepClone() as JsonObject
                       ?? throw new InvalidOperationException("无法构建 API 请求。");
            ChatCompletionStreamOptions.Apply(body, config.ApiBaseUrl);

            var requestJson = body.ToJsonString();
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return response;

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            response.Dispose();

            if (attempt == 0
                && ChatCompletionStreamOptions.TryDisableUsageForBaseUrl(config.ApiBaseUrl, statusCode, errorBody))
                continue;

            throw new InvalidOperationException(ChatApiErrorHelper.FormatHttpError(statusCode, errorBody));
        }

        throw new InvalidOperationException("无法完成 API 请求。");
    }

    private void TryCaptureUsageFromResponse(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return;

        if (usage.TryGetProperty("total_tokens", out var total) && TryReadUsageInt(total, out var totalTokens))
            _lastApiPromptTokens = totalTokens;
        else if (usage.TryGetProperty("prompt_tokens", out var pt) && TryReadUsageInt(pt, out var promptTokens))
            _lastApiPromptTokens = promptTokens;

        if (usage.TryGetProperty("prompt_cache_hit_tokens", out var cached) && TryReadUsageInt(cached, out var cachedTokens))
            _lastApiCachedPromptTokens = cachedTokens;

        if (usage.TryGetProperty("completion_tokens", out var ct) && TryReadUsageInt(ct, out var completionTokens))
            _lastApiCompletionTokens = completionTokens;

    }

    private static bool TryReadUsageInt(JsonElement el, out int value)
    {
        value = 0;
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetInt32(out value))
                return true;

            if (el.TryGetInt64(out var l) && l is >= 0 and <= int.MaxValue)
            {
                value = (int)l;
                return true;
            }
        }

        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        return false;
    }

    /// <summary>流式协议里表示「本段应在工具链继续」的 finish_reason（不同网关字符串略有差异）。</summary>
    private static bool IsStreamingToolFinishReason(string? fr)
    {
        if (string.IsNullOrWhiteSpace(fr)) return false;
        return fr.Equals("tool_calls", StringComparison.OrdinalIgnoreCase)
               || fr.Equals("function_call", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>兼容 <c>data: {...}</c>、<c>data:{...}</c>、大小写与首尾空白。</summary>
    private static bool TryGetSseDataPayload(string line, out string payload)
    {
        payload = "";
        var s = line.TrimStart();
        if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        payload = s.Length <= 5 ? "" : s[5..].TrimStart();
        return true;
    }

    private static void MergeToolCallsFromBuilders(Dictionary<int, ToolCallBuilder> builders, List<ToolCallInfo> target)
    {
        if (builders.Count == 0)
            return;

        if (target.Count == 0)
        {
            AppendToolCallsFromBuilders(builders, target);
            return;
        }

        var merged = new List<ToolCallInfo>();
        var maxIndex = Math.Max(builders.Keys.DefaultIfEmpty(-1).Max(), target.Count - 1);
        for (var i = 0; i <= maxIndex; i++)
        {
            builders.TryGetValue(i, out var builder);
            var existing = i < target.Count ? target[i] : null;
            var mergedCall = MergeToolCallInfo(existing, builder, i);
            if (mergedCall is not null)
                merged.Add(mergedCall);
        }

        target.Clear();
        target.AddRange(merged);
    }

    private static ToolCallInfo? MergeToolCallInfo(ToolCallInfo? existing, ToolCallBuilder? builder, int fallbackIndex)
    {
        var name = PickNonEmpty(builder?.Name, existing?.Name);
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var id = PickNonEmpty(builder?.Id, existing?.Id) ?? BuildFallbackToolCallId(fallbackIndex);
        var args = PickLongerArguments(builder?.Arguments, existing?.Arguments);
        return new ToolCallInfo
        {
            Id = id.Trim(),
            Name = name.Trim(),
            Arguments = args
        };
    }

    private static string? PickNonEmpty(string? primary, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(primary))
            return primary;
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback;
        return null;
    }

    private static string PickLongerArguments(string? primary, string? fallback)
    {
        primary ??= string.Empty;
        fallback ??= string.Empty;
        if (!string.IsNullOrEmpty(primary))
            return primary;
        return primary.Length >= fallback.Length ? primary : fallback;
    }

    private static void AppendToolCallsFromBuilders(Dictionary<int, ToolCallBuilder> builders, List<ToolCallInfo> target)
    {
        foreach (var kv in builders.OrderBy(x => x.Key))
        {
            var merged = MergeToolCallInfo(null, kv.Value, kv.Key);
            if (merged is not null)
                target.Add(merged);
        }
    }

    private static string BuildFallbackToolCallId(int index) => $"call_{Math.Max(0, index)}";

    private bool HasVisibleAssistantReply(string content, string reasoningContent)
    {
        if (!string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(reasoningContent))
            return true;

        if (Messages.Count == 0)
            return false;

        var last = Messages[^1];
        return last is { IsUser: false, HasToolCall: false }
               && (!string.IsNullOrEmpty(last.Content) || !string.IsNullOrEmpty(last.ReasoningContent));
    }

    private async Task<List<JsonNode>> BuildApiMessagesAsync(AiSettings config, bool compressionRound = false)
    {
        var meta = await UiThreadBridge.InvokeAsync(() => (
            SelectedModel,
            SessionId: _streamingOwnerSessionId ?? _currentSessionId,
            WorkspaceDir: _lookService.WorkspaceDirectory,
            CompressionPrep: compressionRound ? _activeCompressionPrep : null)).ConfigureAwait(false);

        if (string.IsNullOrEmpty(meta.SessionId))
            return [];

        var systemPrompt = ChatSystemPrompt.Build(meta.WorkspaceDir);
        CompressionRoundPrep? prep = null;
        if (compressionRound && meta.CompressionPrep is { } compression)
            prep = new CompressionRoundPrep(compression.CompressUserIndex);

        var modelId = !string.IsNullOrEmpty(meta.SelectedModel) ? meta.SelectedModel : config.Model;
        var built = await _apiPayloadBuilder.BuildFromDatabaseAsync(new ApiPayloadBuildRequest(
            meta.SessionId,
            config,
            modelId,
            systemPrompt,
            compressionRound,
            prep)).ConfigureAwait(false);

        return built?.ApiMessages ?? [];
    }

    private bool IsViewingSubAgentSession =>
        IsReadOnlySession && SubAgentSessionHub.IsSubAgentSessionId(_currentSessionId);

    private void SetSubAgentWaitingForReply(bool waiting)
    {
        if (!IsViewingSubAgentSession && waiting)
            return;

        if (_subAgentWaitingForReply == waiting)
            return;

        _subAgentWaitingForReply = waiting;
        RefreshWaitingForReplyState();
    }

    private void OnSubAgentRoundWaiting(SubAgentLiveSession session)
    {
        RunOnUi(() =>
        {
            UpdateLinkedSubAgentPreview(session);
            if (string.Equals(_currentSessionId, session.Id, StringComparison.Ordinal))
                SetSubAgentWaitingForReply(true);
        });
    }

    partial void OnIsReadOnlySessionChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseInput));
        OnPropertyChanged(nameof(CanRevertMessages));
        RevertLastUserMessageCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
        if (!IsViewingSubAgentSession)
            SetSubAgentWaitingForReply(false);
    }

    partial void OnIsConfiguredChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseInput));
        SendOrStopCommand.NotifyCanExecuteChanged();
    }

    private void OnSubAgentSessionStarted(SubAgentLiveSession session)
    {
        RunOnUi(() =>
        {
            LinkSpawnAgentTool(session);
            UpdateLinkedSubAgentPreview(session);
        });
    }

    private void OnSubAgentSessionUpdated(SubAgentLiveSession session)
    {
        RunOnUi(() =>
        {
            UpdateLinkedSubAgentPreview(session);

            if (string.Equals(_currentSessionId, session.Id, StringComparison.Ordinal))
                CurrentSessionTitle = session.Title;

            if (session.Status != SubAgentRunStatus.Running
                && string.Equals(_currentSessionId, session.Id, StringComparison.Ordinal))
                SetSubAgentWaitingForReply(false);

            if (session.Status == SubAgentRunStatus.Running)
                return;

            for (var i = 0; i < Sessions.Count; i++)
            {
                if (!string.Equals(Sessions[i].Id, session.Id, StringComparison.Ordinal))
                    continue;

                var old = Sessions[i];
                Sessions[i] = new ChatSessionSummary
                {
                    Id = old.Id,
                    Title = session.Title,
                    UpdatedAtUtc = old.UpdatedAtUtc,
                    SortOrder = old.SortOrder,
                    IsSubAgent = true,
                    IsRunning = false
                };
                return;
            }
        });
    }

    private void OnSubAgentMessageAppended(SubAgentLiveSession session, ChatMessage msg)
    {
        RunOnUi(() =>
        {
            UpdateLinkedSubAgentPreview(session);

            if (string.Equals(_currentSessionId, session.Id, StringComparison.Ordinal))
            {
                if (!Messages.Contains(msg))
                {
                    msg.TranscriptSink = _transcriptDisplay;
                    Messages.Add(msg);
                    _transcriptDisplay.RequestRefresh();
                }

                SetSubAgentWaitingForReply(false);
            }
        });
    }

    private void OnSubAgentMessagePatched(SubAgentLiveSession session, SubAgentMessagePatch patch)
    {
        RunOnUi(() =>
        {
            ApplySubAgentMessagePatch(patch);
            UpdateLinkedSubAgentPreview(session);
            if (string.Equals(_currentSessionId, session.Id, StringComparison.Ordinal))
                _transcriptDisplay.RequestRefresh();
        });
    }

    private static void ApplySubAgentMessagePatch(SubAgentMessagePatch patch)
    {
        if (patch.IsToolRunning.HasValue)
            patch.Message.IsToolRunning = patch.IsToolRunning.Value;
        if (patch.ToolOutput is not null)
            patch.Message.ToolOutput = patch.ToolOutput;
        if (patch.ToolExitCode.HasValue)
            patch.Message.ToolExitCode = patch.ToolExitCode.Value;
    }

    private static void RunOnUi(Action action) => UiThreadBridge.Post(action);

    private ChatMessage? LinkSpawnAgentTool(SubAgentLiveSession session)
    {
        if (string.IsNullOrEmpty(session.ParentSessionId))
            return null;

        IEnumerable<ChatMessage> source = string.Equals(_currentSessionId, session.ParentSessionId, StringComparison.Ordinal)
            ? Messages
            : _streamingOwnerState is not null
              && string.Equals(_streamingOwnerState.SessionId, session.ParentSessionId, StringComparison.Ordinal)
                ? _streamingOwnerState.Messages
                : [];

        foreach (var m in source.Reverse())
        {
            if (!m.IsToolRunning || !string.Equals(m.ToolName, "spawn_agent", StringComparison.Ordinal))
                continue;

            m.LinkedSubAgentSessionId = session.Id;
            return m;
        }

        return null;
    }

    private void UpdateLinkedSubAgentPreview(SubAgentLiveSession session)
    {
        var tool = FindLinkedSpawnAgentTool(session) ?? LinkSpawnAgentTool(session);
        if (tool is null)
            return;

        tool.ToolOutput = BuildSubAgentPreviewJson(session);
    }

    private ChatMessage? FindLinkedSpawnAgentTool(SubAgentLiveSession session)
    {
        if (string.IsNullOrEmpty(session.ParentSessionId))
            return null;

        IEnumerable<ChatMessage> source = string.Equals(_currentSessionId, session.ParentSessionId, StringComparison.Ordinal)
            ? Messages
            : _streamingOwnerState is not null
              && string.Equals(_streamingOwnerState.SessionId, session.ParentSessionId, StringComparison.Ordinal)
                ? _streamingOwnerState.Messages
                : [];

        return source.Reverse().FirstOrDefault(m =>
            string.Equals(m.LinkedSubAgentSessionId, session.Id, StringComparison.Ordinal)
            || (m.IsToolRunning && string.Equals(m.ToolName, "spawn_agent", StringComparison.Ordinal)));
    }

    private static string BuildSubAgentPreviewJson(SubAgentLiveSession session)
    {
        List<ChatMessage> items;
        string title;
        string task;
        SubAgentRunStatus status;
        lock (session.Sync)
        {
            title = session.Title;
            task = session.Task;
            status = session.Status;
            items = session.Messages.TakeLast(5).ToList();
        }

        return ToolResultJson.Data(o =>
        {
            o["ok"] = true;
            o["preview"] = true;
            o["title"] = title;
            o["status"] = status.ToString();
            o["task"] = TruncatePreviewText(task, 240);
            var arr = new JsonArray();
            foreach (var msg in items)
                arr.Add(BuildSubAgentPreviewMessage(msg));
            o["recent_messages"] = arr;
        });
    }

    private static JsonObject BuildSubAgentPreviewMessage(ChatMessage msg)
    {
        var obj = new JsonObject
        {
            ["role"] = msg.IsUser ? "user" : msg.HasToolCall ? "tool" : "assistant"
        };

        if (msg.HasToolCall)
        {
            obj["name"] = msg.ToolDisplayName;
            obj["command"] = TruncatePreviewText(msg.ToolCommand, 300);
            obj["running"] = msg.IsToolRunning;
            obj["exitCode"] = msg.ToolExitCode;
            if (!string.IsNullOrWhiteSpace(msg.ToolOutput))
                obj["output"] = TruncatePreviewText(msg.ToolOutput, 500);
            return obj;
        }

        if (!string.IsNullOrWhiteSpace(msg.Content))
            obj["content"] = TruncatePreviewText(msg.Content, 600);
        if (!string.IsNullOrWhiteSpace(msg.ReasoningContent))
            obj["reasoning"] = TruncatePreviewText(msg.ReasoningContent, 600);
        if (msg.IsStreaming)
            obj["streaming"] = true;
        if (msg.IsThinking)
            obj["thinking"] = true;

        return obj;
    }

    private static string TruncatePreviewText(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    public void RefreshConfig() => _ = RefreshConfigAsync();

    private async Task RefreshConfigAsync()
    {
        var snapshot = await Task.Run(() =>
        {
            var config = _settingsService.Load().Ai;
            var models = AiEndpointCatalog.GetCatalog(config, config.ApiBaseUrl)
                .Select(entry => entry.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return (config, models);
        }).ConfigureAwait(false);

        await UiThreadBridge.InvokeAsync(() =>
        {
            IsConfigured = snapshot.config.IsConfigured;
            ConfiguredModel = snapshot.config.Model;
            ConfiguredBaseUrl = snapshot.config.ApiBaseUrl;
            AvailableModels.Clear();
            foreach (var modelId in snapshot.models)
            {
                if (!AvailableModels.Contains(modelId))
                    AvailableModels.Add(modelId);
            }

            if (AvailableModels.Count == 0)
                SelectedModel = string.Empty;
            else if (!string.IsNullOrEmpty(snapshot.config.Model) && AvailableModels.Contains(snapshot.config.Model))
                SelectedModel = snapshot.config.Model;
            else
                SelectedModel = AvailableModels[0];

            EnableThinking = snapshot.config.EnableThinking;
        }).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(_currentSessionId))
            RefreshContextTokenUsage();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _eventBus.Publish(new SettingsRequestedEvent("AI设置"));
    }

    private class ToolCallBuilder
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
    }

    private class ToolCallInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Arguments { get; init; } = "";
    }
}
