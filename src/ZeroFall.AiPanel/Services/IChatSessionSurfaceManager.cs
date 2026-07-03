using System.Collections.Generic;
using System.Threading.Tasks;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Services;

/// <summary>会话 UI surface：可见消息 shell + 热尾 hydration。</summary>
public interface IChatSessionSurfaceManager
{
    string SessionId { get; }

    int TotalVisibleCount { get; }

    int HotStartVisibleIndex { get; }

    bool IsActive { get; }

    void Clear();

    Task InitializeAsync(string sessionId, IList<ChatMessage> hotMessages);

    Task RebindHotTailAsync(string sessionId, IList<ChatMessage> hotMessages);

    void SyncHotTailIndex(IReadOnlyList<ChatMessage> hotMessages);

    IReadOnlyList<ChatMessage> BuildVisibleSurface(IReadOnlyList<ChatMessage> hotMessages);

    Task<IReadOnlyList<ChatMessage>> HydrateVisibleRangeAsync(
        int fromVisible,
        int toVisible,
        IReadOnlyList<ChatMessage> hotMessages);

    void TrimHydratedCache(int keepFromVisible, int keepToVisible);
}
