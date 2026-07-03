using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Services;

public sealed class ChatSessionSurfaceManager : IChatSessionSurfaceManager
{
    private const int HotTailCount = 32;
    private const int MaxHydratedEntries = 48;

    private readonly IAiChatSessionStore _sessionStore;

    private readonly List<ChatMessage> _shells = [];
    private readonly Dictionary<int, ChatMessage> _hydrated = new();
    private readonly LinkedList<int> _hydratedLru = new();

    private string _sessionId = string.Empty;
    private int _hotStartVisibleIndex;

    public ChatSessionSurfaceManager(IAiChatSessionStore sessionStore) =>
        _sessionStore = sessionStore;

    public string SessionId => _sessionId;

    public int TotalVisibleCount => _shells.Count;

    public int HotStartVisibleIndex => _hotStartVisibleIndex;

    public bool IsActive => _shells.Count > 0;

    public void Clear()
    {
        _sessionId = string.Empty;
        _shells.Clear();
        _hydrated.Clear();
        _hydratedLru.Clear();
        _hotStartVisibleIndex = 0;
    }

    public async Task InitializeAsync(string sessionId, IList<ChatMessage> hotMessages)
    {
        Clear();
        _sessionId = sessionId;

        var shells = await _sessionStore.LoadVisibleShellsAsync(sessionId).ConfigureAwait(false);
        _shells.AddRange(shells);

        ApplyHotStartIndex(shells, hotMessages);
        await EnsureHotTailLoadedAsync(hotMessages).ConfigureAwait(false);
        ApplyHotStartIndex(shells, hotMessages);
    }

    public async Task RebindHotTailAsync(string sessionId, IList<ChatMessage> hotMessages)
    {
        _hydrated.Clear();
        _hydratedLru.Clear();
        _sessionId = sessionId;

        var shells = await _sessionStore.LoadVisibleShellsAsync(sessionId).ConfigureAwait(false);
        _shells.Clear();
        _shells.AddRange(shells);

        ApplyHotStartIndex(shells, hotMessages);
    }

    public void SyncHotTailIndex(IReadOnlyList<ChatMessage> hotMessages)
    {
        if (_shells.Count == 0)
        {
            _hotStartVisibleIndex = 0;
            return;
        }

        ApplyHotStartIndex(_shells, hotMessages);
    }

    public IReadOnlyList<ChatMessage> BuildVisibleSurface(IReadOnlyList<ChatMessage> hotMessages)
    {
        var visibleHot = hotMessages.Where(m => m.Visual.IsVisibleInUi()).ToList();
        if (_shells.Count == 0)
            return visibleHot;

        if (VisibleHotAlignsWithShells(visibleHot, _shells))
            return visibleHot;

        var total = Math.Max(_shells.Count, _hotStartVisibleIndex + visibleHot.Count);
        var result = new List<ChatMessage>(total);
        for (var i = 0; i < total; i++)
        {
            if (_hydrated.TryGetValue(i, out var hydrated))
            {
                if (hydrated.Visual.IsVisibleInUi())
                    result.Add(hydrated);
                continue;
            }

            if (i >= _hotStartVisibleIndex)
            {
                var hotIndex = i - _hotStartVisibleIndex;
                if (hotIndex >= 0 && hotIndex < visibleHot.Count)
                {
                    result.Add(visibleHot[hotIndex]);
                    continue;
                }
            }

            if (i < _shells.Count)
                result.Add(_shells[i]);
        }

        return result;
    }

    public async Task<IReadOnlyList<ChatMessage>> HydrateVisibleRangeAsync(
        int fromVisible,
        int toVisible,
        IReadOnlyList<ChatMessage> hotMessages)
    {
        if (_shells.Count == 0 || string.IsNullOrWhiteSpace(_sessionId))
            return [];

        fromVisible = Math.Clamp(fromVisible, 0, _shells.Count - 1);
        toVisible = Math.Clamp(toVisible, fromVisible, _shells.Count - 1);
        var visibleHotCount = hotMessages.Count(m => m.Visual.IsVisibleInUi());

        var needed = new List<(int Index, string Id)>();
        for (var i = fromVisible; i <= toVisible; i++)
        {
            if (_hydrated.ContainsKey(i))
                continue;

            if (i >= _hotStartVisibleIndex)
            {
                var hotIndex = i - _hotStartVisibleIndex;
                if (hotIndex >= 0 && hotIndex < visibleHotCount)
                    continue;
            }

            needed.Add((i, ChatMessageIds.UiId(_shells[i])));
        }

        if (needed.Count == 0)
            return CollectHydratedRange(fromVisible, toVisible, hotMessages);

        var loaded = await _sessionStore
            .LoadVisibleMessagesRangeAsync(_sessionId, fromVisible, toVisible - fromVisible + 1)
            .ConfigureAwait(false);

        for (var i = 0; i < loaded.Count; i++)
        {
            var visibleIndex = fromVisible + i;
            if (visibleIndex > toVisible)
                break;

            CacheHydrated(visibleIndex, loaded[i]);
        }

        return CollectHydratedRange(fromVisible, toVisible, hotMessages);
    }

    public void TrimHydratedCache(int keepFromVisible, int keepToVisible)
    {
        if (_hydrated.Count == 0)
            return;

        var remove = _hydrated.Keys
            .Where(i => i < keepFromVisible || i > keepToVisible)
            .ToList();

        foreach (var index in remove)
        {
            _hydrated.Remove(index);
            _hydratedLru.Remove(index);
        }
    }

    private async Task EnsureHotTailLoadedAsync(IList<ChatMessage> hotMessages)
    {
        if (_shells.Count == 0 || hotMessages.Count > 0)
            return;

        if (_hotStartVisibleIndex >= _shells.Count)
            _hotStartVisibleIndex = Math.Max(0, _shells.Count - HotTailCount);

        var take = Math.Min(HotTailCount, _shells.Count - _hotStartVisibleIndex);
        if (take <= 0)
            return;

        var loaded = await _sessionStore
            .LoadVisibleMessagesRangeAsync(_sessionId, _hotStartVisibleIndex, take)
            .ConfigureAwait(false);

        foreach (var message in loaded)
            hotMessages.Add(message);
    }

    private static int ResolveHotStartIndex(int shellCount, int hotCount)
    {
        if (shellCount <= 0)
            return 0;

        return Math.Max(0, shellCount - Math.Max(hotCount, HotTailCount));
    }

    private void ApplyHotStartIndex(
        IReadOnlyList<ChatMessage> shells,
        IEnumerable<ChatMessage> hotMessages)
    {
        var visibleHot = hotMessages.Where(m => m.Visual.IsVisibleInUi()).ToList();
        _hotStartVisibleIndex = MatchHotStartByTailIds(shells, visibleHot);
        if (_hotStartVisibleIndex >= 0)
            return;

        _hotStartVisibleIndex = MatchHotStartByLastVisibleId(shells, visibleHot);
        if (_hotStartVisibleIndex >= 0)
            return;

        _hotStartVisibleIndex = visibleHot.Count == 0
            ? ResolveHotStartIndex(shells.Count, 0)
            : ResolveHotStartIndex(shells.Count, visibleHot.Count);
    }

    /// <summary>内存热尾已含完整可见时间线时，直接按 Messages 顺序展示，避免 shell 拼接乱序。</summary>
    private static bool VisibleHotAlignsWithShells(IReadOnlyList<ChatMessage> visibleHot, IReadOnlyList<ChatMessage> shells)
    {
        if (visibleHot.Count == 0 || shells.Count == 0 || visibleHot.Count < shells.Count)
            return false;

        for (var i = 0; i < shells.Count; i++)
        {
            if (!string.Equals(ChatMessageIds.UiId(shells[i]), ChatMessageIds.UiId(visibleHot[i]), StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static int MatchHotStartByLastVisibleId(IReadOnlyList<ChatMessage> shells, IReadOnlyList<ChatMessage> visibleHot)
    {
        if (shells.Count == 0 || visibleHot.Count == 0)
            return -1;

        var lastHotId = ChatMessageIds.UiId(visibleHot[^1]);
        for (var i = shells.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(ChatMessageIds.UiId(shells[i]), lastHotId, StringComparison.Ordinal))
                continue;

            return Math.Max(0, i - (visibleHot.Count - 1));
        }

        return -1;
    }

    private static int MatchHotStartByTailIds(IReadOnlyList<ChatMessage> shells, IReadOnlyList<ChatMessage> visibleHot)
    {
        if (shells.Count == 0 || visibleHot.Count == 0)
            return -1;

        var firstHotId = ChatMessageIds.UiId(visibleHot[0]);
        for (var i = 0; i < shells.Count; i++)
        {
            if (string.Equals(ChatMessageIds.UiId(shells[i]), firstHotId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private IReadOnlyList<ChatMessage> CollectHydratedRange(
        int fromVisible,
        int toVisible,
        IReadOnlyList<ChatMessage> hotMessages)
    {
        var surface = BuildVisibleSurface(hotMessages);
        if (fromVisible >= surface.Count)
            return [];

        toVisible = Math.Min(toVisible, surface.Count - 1);
        var result = new List<ChatMessage>(toVisible - fromVisible + 1);
        for (var i = fromVisible; i <= toVisible; i++)
            result.Add(surface[i]);
        return result;
    }

    private void CacheHydrated(int visibleIndex, ChatMessage message)
    {
        message.IsArchiveShell = false;
        _hydrated[visibleIndex] = message;
        _hydratedLru.Remove(visibleIndex);
        _hydratedLru.AddLast(visibleIndex);

        while (_hydratedLru.Count > MaxHydratedEntries)
        {
            var evictIndex = _hydratedLru.First!.Value;
            _hydratedLru.RemoveFirst();
            _hydrated.Remove(evictIndex);
        }
    }
}
