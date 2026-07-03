using System.Threading;
using System.Threading.Tasks;

namespace ZeroFall.Platform.Services;

public interface ITerminalScreenService
{
    /// <summary>读取底部终端面板指定或当前选中会话内容（优先 XTerm scrollback，与界面一致；buffer 不可用时回退 PTY）。</summary>
    string? ReadVisibleScreen(string? sessionId = null);

    /// <summary>读取自上次 send_terminal_command 发出行到 buffer 末尾；无标记时回退全量内容。</summary>
    string? ReadSinceLastCommand(string? sessionId = null);

    /// <summary>自上次 AI 终端工具返回后的新增 output（read_terminal 专用）。</summary>
    string? ReadSinceLastAiToolRead(string? sessionId = null);

    /// <summary>推进 AI 读屏游标，避免下次 read_terminal 重复历史。</summary>
    void CommitAiReadCursor(string? sessionId = null);

    /// <summary>读取终端 buffer 末尾 <paramref name="lineCount"/> 行（与界面 scrollback 一致）。</summary>
    string? ReadLastLines(string? sessionId = null, int lineCount = 50);

    Task<string?> ReadVisibleScreenAsync(string? sessionId = null, CancellationToken cancellationToken = default);

    Task<string?> ReadLastLinesAsync(string? sessionId = null, int lineCount = 50, CancellationToken cancellationToken = default);

    Task<string?> ReadSinceLastCommandAsync(
        string? sessionId = null,
        string? sentCommandHint = null,
        CancellationToken cancellationToken = default);

    Task<string?> ReadSinceLastAiToolReadAsync(string? sessionId = null, CancellationToken cancellationToken = default);

    Task CommitAiReadCursorAsync(string? sessionId = null, CancellationToken cancellationToken = default);
}
