namespace Datafinder.Platform.Services;

public interface ITerminalScreenService
{
    /// <summary>读取底部终端面板指定或当前选中会话内容（优先 XTerm scrollback，与界面一致；buffer 不可用时回退 PTY）。</summary>
    string? ReadVisibleScreen(string? sessionId = null);

    /// <summary>读取自上次 send_terminal_command 发出行到 buffer 末尾；无标记时回退全量内容。</summary>
    string? ReadSinceLastCommand(string? sessionId = null);
}
