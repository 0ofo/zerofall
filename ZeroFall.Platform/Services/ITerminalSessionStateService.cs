namespace ZeroFall.Platform.Services;

public interface ITerminalSessionStateService
{
    TerminalCommandPhase GetPhase(string? sessionId = null);

    bool IsAwaitingCommand(string? sessionId = null) =>
        GetPhase(sessionId) == TerminalCommandPhase.Idle;

    bool IsCommandExecuting(string? sessionId = null) =>
        GetPhase(sessionId) == TerminalCommandPhase.Executing;
}
