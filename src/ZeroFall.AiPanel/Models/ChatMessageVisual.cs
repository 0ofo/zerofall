namespace ZeroFall.AiPanel.Models;

/// <summary>消息 UI 可见性；落库列 <c>visual</c>，默认可见。</summary>
public enum ChatMessageVisual : byte
{
    /// <summary>在 UI 中展示。</summary>
    Visible = 0,

    /// <summary>不在 UI 中展示，仍参与 API 与落库。</summary>
    Hidden = 1,
}

public static class ChatMessageVisualExtensions
{
    public static bool IsVisibleInUi(this ChatMessageVisual visual) =>
        visual == ChatMessageVisual.Visible;
}
