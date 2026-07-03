using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ZeroFall.AiPanel.Services;

public interface IAiToolResultRuntimeStore
{
    void Upsert(long messageId, string output);
    bool TryGet(long messageId, out string output);
    void Remove(long messageId);
    void RemoveMany(IEnumerable<long> messageIds);
}

/// <summary>工具结果落库前的完整 output 缓存；键为全局 message id。</summary>
public sealed class AiToolResultRuntimeStore : IAiToolResultRuntimeStore
{
    private readonly ConcurrentDictionary<long, string> _outputs = new();

    public void Upsert(long messageId, string output)
    {
        if (messageId <= 0)
            return;

        _outputs[messageId] = output ?? string.Empty;
    }

    public bool TryGet(long messageId, out string output)
    {
        output = string.Empty;
        return messageId > 0 && _outputs.TryGetValue(messageId, out output!);
    }

    public void Remove(long messageId)
    {
        if (messageId <= 0)
            return;

        _outputs.TryRemove(messageId, out _);
    }

    public void RemoveMany(IEnumerable<long> messageIds)
    {
        foreach (var messageId in messageIds)
            Remove(messageId);
    }
}
