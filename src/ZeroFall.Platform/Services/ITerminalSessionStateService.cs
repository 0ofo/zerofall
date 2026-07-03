using System.Threading;
using System.Threading.Tasks;

namespace ZeroFall.Platform.Services;

public interface ITerminalSessionStateService
{
    TerminalCommandPhase GetPhase(string? sessionId = null);

    Task<TerminalCommandPhase> GetPhaseAsync(string? sessionId = null, CancellationToken cancellationToken = default);

    /// <summary>自该会话最后一次 PTY/屏幕输出更新以来经过的秒数；无会话时返回 null。</summary>
    double? GetSecondsSinceLastOutput(string? sessionId = null);

    Task<double?> GetSecondsSinceLastOutputAsync(string? sessionId = null, CancellationToken cancellationToken = default);

    bool IsAwaitingCommand(string? sessionId = null) =>
        GetPhase(sessionId) == TerminalCommandPhase.Idle;

    bool IsCommandExecuting(string? sessionId = null) =>
        GetPhase(sessionId) == TerminalCommandPhase.Executing;
}
