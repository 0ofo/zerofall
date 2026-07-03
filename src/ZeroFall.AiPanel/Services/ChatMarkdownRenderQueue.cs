using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ZeroFall.AiPanel.Services;

/// <summary>单 worker 顺序执行 Markdig，避免 SSE 在 UI 线程上反复全量渲染。</summary>
public sealed class ChatMarkdownRenderQueue : IChatMarkdownRenderQueue, IDisposable
{
    private readonly Channel<ChatMarkdownRenderRequest> _channel =
        Channel.CreateUnbounded<ChatMarkdownRenderRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public ChatMarkdownRenderQueue()
    {
        _worker = Task.Run(WorkerLoopAsync);
    }

    public void Enqueue(ChatMarkdownRenderRequest request)
    {
        if (string.IsNullOrEmpty(request.MessageUiId))
            return;

        var key = BuildKey(request.MessageUiId, request.Reasoning);
        if (!_inFlight.TryAdd(key, 0))
            return;

        if (!_channel.Writer.TryWrite(request))
            _inFlight.TryRemove(key, out _);
    }

    private async Task WorkerLoopAsync()
    {
        var token = _cts.Token;
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(token))
            {
                var key = BuildKey(request.MessageUiId, request.Reasoning);
                try
                {
                    var blocks = MarkdownRenderEngine.RenderSnapshot(request.Markdown);
                    var html = ChatMessageRenderCache.Serialize(blocks);
                    var result = new ChatMarkdownRenderResult
                    {
                        MessageUiId = request.MessageUiId,
                        Reasoning = request.Reasoning,
                        Blocks = blocks,
                        Html = html
                    };

                    try
                    {
                        request.OnCompleted(result);
                    }
                    catch
                    {
                    }
                }
                catch
                {
                }
                finally
                {
                    _inFlight.TryRemove(key, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string BuildKey(string messageUiId, bool reasoning) =>
        $"{messageUiId}:{(reasoning ? 'r' : 'c')}";

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        _worker.Dispose();
        _cts.Dispose();
    }
}
