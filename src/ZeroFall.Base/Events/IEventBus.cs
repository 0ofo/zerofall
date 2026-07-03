using System;

namespace ZeroFall.Base.Events;

public interface IEventBus
{
    void Subscribe<T>(Action<T> handler);
    void Unsubscribe<T>(Action<T> handler);
    void Publish<T>(T message);
}
