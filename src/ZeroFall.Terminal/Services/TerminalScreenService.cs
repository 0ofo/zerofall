using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Platform.Services;
using ZeroFall.Terminal.ViewModels;

namespace ZeroFall.Terminal.Services;

public sealed class TerminalScreenService : ITerminalScreenService
{
    private TerminalHostViewModel? _host;
    private ITerminalTranscriptService? _transcript;

    internal void AttachHost(TerminalHostViewModel host) => _host = host;

    internal void AttachTranscript(ITerminalTranscriptService transcript) => _transcript = transcript;

    public string? ReadVisibleScreen(string? sessionId = null) =>
        ReadVisibleScreenAsync(sessionId).GetAwaiter().GetResult();

    public string? ReadSinceLastCommand(string? sessionId = null) =>
        ReadSinceLastCommandAsync(sessionId).GetAwaiter().GetResult();

    public string? ReadSinceLastAiToolRead(string? sessionId = null) =>
        ReadSinceLastAiToolReadAsync(sessionId).GetAwaiter().GetResult();

    public void CommitAiReadCursor(string? sessionId = null) =>
        CommitAiReadCursorAsync(sessionId).GetAwaiter().GetResult();

    public string? ReadLastLines(string? sessionId = null, int lineCount = 50) =>
        ReadLastLinesAsync(sessionId, lineCount).GetAwaiter().GetResult();

    public Task<string?> ReadVisibleScreenAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var host = _host;
        if (host == null)
            return Task.FromResult<string?>(null);

        return UiThreadHelper.RunAsync(() => host.ReadVisibleScreen(sessionId), cancellationToken);
    }

    public Task<string?> ReadSinceLastCommandAsync(
        string? sessionId = null,
        string? sentCommandHint = null,
        CancellationToken cancellationToken = default)
    {
        var host = _host;
        if (host == null)
            return Task.FromResult<string?>(null);

        var id = host.ResolveSessionId(sessionId);
        if (id != null && _transcript != null && _transcript.IsSessionRegistered(id))
        {
            return ReadTranscriptAsync(
                host,
                id,
                () => _transcript!.ReadFromLastCommand(id),
                incrementalAiRead: false,
                sentCommandHint,
                cancellationToken);
        }

        return UiThreadHelper.RunAsync(() => host.ReadSinceLastCommand(sessionId), cancellationToken);
    }

    public Task<string?> ReadSinceLastAiToolReadAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var host = _host;
        if (host == null)
            return Task.FromResult<string?>(null);

        var id = host.ResolveSessionId(sessionId);
        if (id != null && _transcript != null && _transcript.IsSessionRegistered(id))
        {
            return ReadTranscriptAsync(
                host,
                id,
                () => _transcript!.ReadSinceLastAiToolRead(id),
                incrementalAiRead: true,
                sentCommandHint: null,
                cancellationToken);
        }

        return UiThreadHelper.RunAsync(() => host.ReadSinceLastAiToolRead(sessionId), cancellationToken);
    }

    public Task<string?> ReadLastLinesAsync(
        string? sessionId = null,
        int lineCount = 50,
        CancellationToken cancellationToken = default)
    {
        var host = _host;
        if (host == null)
            return Task.FromResult<string?>(null);

        lineCount = Math.Clamp(lineCount, 1, TerminalScreenReader.AiMaxLines);

        var id = host.ResolveSessionId(sessionId);
        if (id != null && _transcript != null && _transcript.IsSessionRegistered(id))
        {
            return ReadTranscriptTailAsync(host, id, lineCount, cancellationToken);
        }

        return UiThreadHelper.RunAsync(() => host.ReadLastLines(sessionId, lineCount), cancellationToken);
    }

    private async Task<string?> ReadTranscriptTailAsync(
        TerminalHostViewModel host,
        string sessionId,
        int lineCount,
        CancellationToken cancellationToken)
    {
        await UiThreadHelper.RunAsync(() =>
        {
            host.PrepareTranscriptForAiRead(sessionId);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var transcriptText = _transcript!.ReadLastLines(sessionId, lineCount);
        string? xtermTail = null;
        await UiThreadHelper.RunAsync(() =>
        {
            xtermTail = host.ReadLastLines(sessionId, lineCount);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(transcriptText))
            return xtermTail ?? string.Empty;
        if (string.IsNullOrEmpty(xtermTail))
            return transcriptText;

        return xtermTail.Length > transcriptText.Length ? xtermTail : transcriptText;
    }

    private static async Task<string?> ReadTranscriptAsync(
        TerminalHostViewModel host,
        string sessionId,
        Func<string?> read,
        bool incrementalAiRead,
        string? sentCommandHint,
        CancellationToken cancellationToken)
    {
        await UiThreadHelper.RunAsync(() =>
        {
            host.PrepareTranscriptForAiRead(sessionId);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var transcriptText = read();
        if (incrementalAiRead)
            return transcriptText ?? string.Empty;

        string? ptySlice = null;
        await UiThreadHelper.RunAsync(() =>
        {
            ptySlice = host.ReadSinceLastCommandPtySlice(sessionId);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return TerminalAiReadMerge.PickSinceCommand(transcriptText, ptySlice, sentCommandHint);
    }

    public async Task CommitAiReadCursorAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var host = _host;
        if (host == null)
            return;

        var id = host.ResolveSessionId(sessionId);
        if (id != null && _transcript != null && _transcript.IsSessionRegistered(id))
            _transcript.CommitAiToolReadCursor(id);

        await UiThreadHelper.RunAsync(() =>
        {
            host.CommitAiReadCursor(sessionId);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }
}
