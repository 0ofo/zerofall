using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ZeroFall.Platform.Services;
using ZeroFall.Terminal;
using ZeroFall.Terminal.ViewModels;

namespace ZeroFall.Terminal.Services;

public sealed class TerminalCommandService : ITerminalCommandService
{
    private TerminalHostViewModel? _host;

    internal void AttachHost(TerminalHostViewModel host) => _host = host;

    public void SendCommand(string command, string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        var host = _host;
        if (host == null)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            host.SendCommand(command, sessionId);
        else
            Dispatcher.UIThread.Post(() => host.SendCommand(command, sessionId));
    }

    public Task SendCommandAsync(string command, string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;

        var host = _host;
        if (host == null)
            return Task.CompletedTask;

        return UiThreadHelper.RunAsync(
            () => host.SendCommandAsync(command, sessionId),
            CancellationToken.None);
    }

    public Task SendInterruptAsync(string? sessionId = null)
    {
        var host = _host;
        if (host == null)
            return Task.CompletedTask;

        return UiThreadHelper.RunAsync(
            () => host.SendInterruptAsync(sessionId),
            CancellationToken.None);
    }
}
