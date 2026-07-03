using System;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroFall.Browser;

public class BrowserModule : IModule
{
    private static readonly IBrowserFeatureRegistrar[] Features =
    [
        new BrowserCoreFeatureRegistrar(),
        new BrowserTrafficFeatureRegistrar(),
        new BrowserWebsiteTreeFeatureRegistrar(),
        new BrowserHttpToolsFeatureRegistrar(),
        new BrowserFetchAiToolsFeatureRegistrar(),
    ];

    public void RegisterServices(IServiceCollection services)
    {
        foreach (var feature in Features)
            feature.RegisterServices(services);
    }

    public void Initialize(IServiceProvider sp)
    {
        foreach (var feature in Features)
            feature.Initialize(sp);
    }
}
