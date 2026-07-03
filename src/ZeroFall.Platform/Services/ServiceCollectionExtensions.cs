using ZeroFall.Platform.AppServices;
using ZeroFall.Base.Data;
using ZeroFall.Base.Events;
using ZeroFall.Base.Commands;
using ZeroFall.Platform.Services.RelationalDb;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroFall.Platform.Services;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers core backend services that can be reused by Desktop UI and Minimal API hosts.
    /// </summary>
    public static IServiceCollection AddZeroFallCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<ICommandService, CommandService>();
        services.AddSingleton<ISqliteService, SqliteService>();
        services.AddSingleton<IRelationalDbBrowser, SqliteRelationalDbBrowser>();
        services.AddSingleton<IRelationalDbBrowser, MySqlRelationalDbBrowser>();
        services.AddSingleton<IRelationalDbBrowserRegistry, RelationalDbBrowserRegistry>();
        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<ISettingsService>(sp =>
            new SettingsServiceImpl(sp.GetRequiredService<IEventBus>()));
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<IApiIndexService, ApiIndexService>();
        services.AddSingleton<IFileIndexService, FileIndexService>();
        services.AddSingleton<IInvestigationStore, InvestigationStore>();
        services.AddSingleton<FluxzyMitmProxyHost>();
        services.AddSingleton<IProxyGatewayService, FluxzyProxyGatewayService>();
        services.AddSingleton<ProxyRuntimeCoordinator>();
        services.AddSingleton<IOutboundHttpClientFactory, OutboundHttpClientFactory>();
        services.AddSingleton<IUiContextService, UiContextService>();
        services.AddSingleton<IZeroFallApiService, ZeroFallApiService>();
        services.AddSingleton<IAiChatRunContext, AiChatRunContext>();
        return services;
    }
}
