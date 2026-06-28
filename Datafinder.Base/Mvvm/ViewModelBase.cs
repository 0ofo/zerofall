using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Datafinder.Base.Events;

namespace Datafinder.Base.Mvvm;

public abstract class ViewModelBase : ObservableObject, IDisposable
{
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposed;

    protected IDisposable SubscribeEvent<T>(IEventBus eventBus, Action<T> handler)
    {
        var subscription = eventBus.SubscribeDisposable(handler);
        _subscriptions.Add(subscription);
        return subscription;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            foreach (var sub in _subscriptions)
            {
                sub.Dispose();
            }
            _subscriptions.Clear();
        }
    }
}
