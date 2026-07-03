using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Platform.Services;
using ZeroFall.Terminal.ViewModels;

namespace ZeroFall.Terminal.Services;

public sealed class TerminalSessionStateService : ITerminalSessionStateService
{
    private TerminalHostViewModel? _host;
    private ITerminalTranscriptService? _transcript;

    internal void AttachHost(TerminalHostViewModel host) => _host = host;

    internal void AttachTranscript(ITerminalTranscriptService transcript) => _transcript = transcript;

    public TerminalCommandPhase GetPhase(string? sessionId = null) =>
        GetPhaseAsync(sessionId).GetAwaiter().GetResult();

    public Task<TerminalCommandPhase> GetPhaseAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var host = _host;
        if (host == null)
            return Task.FromResult(TerminalCommandPhase.Unknown);

        cancellationToken.ThrowIfCancellationRequested();

        var id = host.ResolveSessionId(sessionId);
        if (id != null && _transcript != null && _transcript.IsSessionRegistered(id))
        {
            var phase = _transcript.GetPhase(id);
            if (phase != null)
                return Task.FromResult(phase.Value);
        }

        return UiThreadHelper.RunAsync(() => host.GetCommandPhase(sessionId), cancellationToken);
    }

    public double? GetSecondsSinceLastOutput(string? sessionId = null) =>
        GetSecondsSinceLastOutputAsync(sessionId).GetAwaiter().GetResult();

    public Task<double?> GetSecondsSinceLastOutputAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var host = _host;
        if (host == null)
            return Task.FromResult<double?>(null);

        cancellationToken.ThrowIfCancellationRequested();

        var id = host.ResolveSessionId(sessionId);
        if (id != null && _transcript != null && _transcript.IsSessionRegistered(id))
            return Task.FromResult(_transcript.GetSecondsSinceLastOutput(id));

        return UiThreadHelper.RunAsync(() => host.GetSecondsSinceLastOutput(sessionId), cancellationToken);
    }
}
