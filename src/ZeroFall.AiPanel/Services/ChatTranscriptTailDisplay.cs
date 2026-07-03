using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.AiPanel.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Services;

/// <summary>聊天区只展示尾部 transcript；后台拼字符串，UI 只更新 TextBlock。</summary>
public sealed class ChatTranscriptTailDisplay : IChatTranscriptSink
{
    public const int MaxBlocks = 20;
    private const int CoalesceMs = 250;
    private const int StreamingCoalesceMs = 1200;

    private Func<IReadOnlyList<ChatMessage>>? _messageSource;
    private Action<string>? _applyText;
    private int _generation;
    private int _flushScheduled;
    private int _streamingDepth;
    private string _lastAppliedText = string.Empty;

    public void Bind(Func<IReadOnlyList<ChatMessage>> messageSource, Action<string> applyText)
    {
        _messageSource = messageSource;
        _applyText = applyText;
    }

    public void BeginStreaming() => Interlocked.Increment(ref _streamingDepth);

    public void EndStreaming()
    {
        Interlocked.Decrement(ref _streamingDepth);
        RequestRefresh();
    }

    public void MarkDirty() => RequestRefresh();

    void IChatTranscriptSink.MarkDirty()
    {
        if (Volatile.Read(ref _streamingDepth) > 0)
            return;

        RequestRefresh();
    }

    public void RequestRefresh() => ScheduleRefresh();

    public void RefreshNow()
    {
        Interlocked.Increment(ref _generation);
        _ = FlushCoreAsync(Volatile.Read(ref _generation), clearScheduled: false);
    }

    public void CancelPending()
    {
        Interlocked.Increment(ref _generation);
        _lastAppliedText = string.Empty;
    }

    private int EffectiveCoalesceMs =>
        Volatile.Read(ref _streamingDepth) > 0 ? StreamingCoalesceMs : CoalesceMs;

    private void ScheduleRefresh()
    {
        if (_messageSource is null || _applyText is null)
            return;

        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
            return;

        var generation = Volatile.Read(ref _generation);
        _ = FlushDeferredAsync(generation);
    }

    private async Task FlushDeferredAsync(int generation)
    {
        try
        {
            await Task.Delay(EffectiveCoalesceMs).ConfigureAwait(false);
            await FlushCoreAsync(generation, clearScheduled: true).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
        }
    }

    private async Task FlushCoreAsync(int generation, bool clearScheduled)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            if (clearScheduled)
                Interlocked.Exchange(ref _flushScheduled, 0);
            return;
        }

        if (_messageSource is null || _applyText is null)
        {
            if (clearScheduled)
                Interlocked.Exchange(ref _flushScheduled, 0);
            return;
        }

        var snapshots = await ChatMessageUiSnapshot.CaptureArrayAsync(_messageSource).ConfigureAwait(false);
        if (snapshots is null || generation != Volatile.Read(ref _generation))
        {
            if (clearScheduled)
                Interlocked.Exchange(ref _flushScheduled, 0);
            return;
        }

        var text = ChatTranscriptTailFormatter.FormatSnapshots(snapshots, MaxBlocks);

        if (string.Equals(text, _lastAppliedText, StringComparison.Ordinal))
        {
            if (clearScheduled)
                Interlocked.Exchange(ref _flushScheduled, 0);
            return;
        }

        _lastAppliedText = text;

        try
        {
            await UiThreadBridge.InvokeAsync(() =>
            {
                if (clearScheduled)
                    Interlocked.Exchange(ref _flushScheduled, 0);
                if (generation != Volatile.Read(ref _generation))
                    return;

                _applyText!(text);
            }).ConfigureAwait(false);
        }
        catch
        {
            if (clearScheduled)
                Interlocked.Exchange(ref _flushScheduled, 0);
        }
    }
}

internal interface IChatTranscriptSink
{
    void MarkDirty();
}
