using System;
using System.Collections.Generic;

namespace ZeroFall.Browser.Services;

public interface ITargetScopeService
{
    IReadOnlyCollection<string> Hosts { get; }

    bool HasEntries { get; }

    event Action? Changed;

    bool AddHost(string host);

    bool RemoveHost(string host);

    void Clear();

    bool IsUrlInScope(string url);

    bool IsHostInScope(string host);
}
