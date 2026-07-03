using System.Collections.ObjectModel;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Services;

/// <summary>AI 会话列表的共享状态；主 VM 写入，独立 Tab VM 读取标题同步。</summary>
public sealed class AiSessionListState
{
    public ObservableCollection<ChatSessionSummary> Sessions { get; } = [];
}
