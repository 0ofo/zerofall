using System.Threading.Tasks;

namespace Datafinder.Platform.Services;

public interface ITerminalCommandService
{
    /// <summary>向底部终端面板交互式 shell 插入并执行命令（自动回车）。</summary>
    void SendCommand(string command, string? sessionId = null);

    /// <summary>写入 PTY 并等待发送完成后返回（AI 工具用）。</summary>
    Task SendCommandAsync(string command, string? sessionId = null);

    /// <summary>向 PTY 发送 Ctrl+C（SIGINT），中断当前命令。</summary>
    Task SendInterruptAsync(string? sessionId = null);
}
