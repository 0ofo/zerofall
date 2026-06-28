using System;

namespace Datafinder.Base.Events;

public sealed class EventSubscription<T> : IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly Action<T> _handler;
    private bool _disposed;

    public EventSubscription(IEventBus eventBus, Action<T> handler)
    {
        _eventBus = eventBus;
        _handler = handler;
        _eventBus.Subscribe(_handler);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _eventBus.Unsubscribe(_handler);
    }
}

public static class EventBusExtensions
{
    public static IDisposable SubscribeDisposable<T>(this IEventBus eventBus, Action<T> handler)
    {
        return new EventSubscription<T>(eventBus, handler);
    }
}
