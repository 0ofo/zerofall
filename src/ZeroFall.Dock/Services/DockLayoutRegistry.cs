using System;
using System.Collections.Generic;
using System.Linq;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Dock.Services;

public sealed class ContentFactoryRegistry : IContentFactoryRegistry
{
    private readonly Dictionary<string, IContentFactory> _factories = new();

    public void Register(IContentFactory factory)
    {
        _factories[factory.ContentType] = factory;
    }

    public void Register(string contentType, IContentFactory factory)
    {
        _factories[contentType] = factory;
    }

    public bool TryCreateContent(string contentType, ContentFactoryContext context, out object? content)
    {
        content = null;
        if (_factories.TryGetValue(contentType, out var factory))
        {
            content = factory.CreateContent(context);
            return true;
        }
        return false;
    }

    public bool HasFactory(string contentType) => _factories.ContainsKey(contentType);
}

public sealed class SettingsRegistry : ISettingsRegistry
{
    private readonly List<SettingsPageEntry> _pages = new();
    private readonly object _lock = new();

    public void Register(SettingsPageEntry entry)
    {
        lock (_lock)
        {
            _pages.Add(entry);
            _pages.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
    }

    public IReadOnlyList<SettingsPageEntry> GetPages()
    {
        lock (_lock)
        {
            return _pages.ToList().AsReadOnly();
        }
    }
}

public sealed class MenuRegistry : IMenuRegistry
{
    private readonly List<MenuItemEntry> _items = new();
    private readonly object _lock = new();

    public void Register(MenuItemEntry entry)
    {
        lock (_lock)
        {
            _items.Add(entry);
        }
    }

    public IReadOnlyList<MenuItemEntry> GetItems()
    {
        lock (_lock)
        {
            return _items.ToList().AsReadOnly();
        }
    }

    public IReadOnlyList<MenuItemEntry> GetItemsForMenu(string menuPath)
    {
        lock (_lock)
        {
            return _items
                .Where(i => i.MenuPath == menuPath)
                .OrderBy(i => i.Order)
                .ToList()
                .AsReadOnly();
        }
    }
}

public sealed class DockLayoutRegistry : IDockLayoutRegistry
{
    private readonly List<DockTabRegistration> _registrations = new();
    private readonly object _lock = new();

    public void RegisterTab(DockTabRegistration registration)
    {
        lock (_lock)
        {
            _registrations.Add(registration);
        }
    }

    public IReadOnlyList<DockTabRegistration> GetRegistrations()
    {
        lock (_lock)
        {
            return _registrations.ToList().AsReadOnly();
        }
    }

    public IReadOnlyList<DockTabRegistration> GetRegistrationsForRegion(DockPosition region)
    {
        lock (_lock)
        {
            return _registrations
                .Where(r => r.Region == region)
                .ToList()
                .AsReadOnly();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _registrations.Clear();
        }
    }
}
