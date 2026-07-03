using System.Collections.ObjectModel;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Models;

/// <summary>
/// 一轮对话：一条用户消息 + 其后直到下一条用户之前的全部气泡（助手 / 工具等）。
/// </summary>
public sealed class ChatRoundBlock
{
    public ChatMessage? UserMessage { get; init; }

    /// <summary>紧随用户气泡之后的非 User 消息。</summary>
    public ObservableCollection<ChatMessage> Following { get; } = [];
}
