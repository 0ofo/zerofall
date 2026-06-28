using System.Collections.Generic;
using System.Linq;
using Datafinder.Base.Data;

namespace Datafinder.Platform.Services.RelationalDb;

public sealed class RelationalDbBrowserRegistry : IRelationalDbBrowserRegistry
{
    private readonly IReadOnlyList<IRelationalDbBrowser> _browsers;

    public RelationalDbBrowserRegistry(IEnumerable<IRelationalDbBrowser> browsers)
    {
        _browsers = browsers.ToList();
    }

    public IRelationalDbBrowser? Resolve(string connectionReference) =>
        _browsers.FirstOrDefault(b => b.CanHandle(connectionReference));
}
