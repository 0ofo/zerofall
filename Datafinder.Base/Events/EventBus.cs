using System;
using System.Collections.Generic;
using System.Linq;

namespace Datafinder.Base.Events;

public sealed class EventBus : IEventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _lock = new();

    public void Subscribe<T>(Action<T> handler)
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
            {
                list = new List<Delegate>();
                _handlers[typeof(T)] = list;
            }
            list.Add(handler);
        }
    }

    public void Unsubscribe<T>(Action<T> handler)
    {
        lock (_lock)
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
            {
                list.Remove(handler);
            }
        }
    }

    public void Publish<T>(T message)
    {
        List<Delegate>? handlers;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out handlers)) return;
            handlers = handlers.ToList();
        }

        foreach (var handler in handlers)
        {
            ((Action<T>)handler)(message);
        }
    }
}
