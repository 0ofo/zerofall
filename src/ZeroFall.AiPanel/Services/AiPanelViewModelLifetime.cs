namespace ZeroFall.AiPanel.Services;

/// <summary>区分全局会话协调 VM 与每个 AI Tab 的会话 VM。</summary>
public sealed class AiPanelViewModelLifetime(bool isCoordinatorInstance)
{
    public bool IsCoordinatorInstance { get; } = isCoordinatorInstance;
}
