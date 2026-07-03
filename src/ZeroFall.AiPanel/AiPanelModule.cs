using System;
using Microsoft.Extensions.DependencyInjection;
using ZeroFall.AiPanel.Services;
using ZeroFall.AiPanel.Tools.Builtin;
using ZeroFall.AiPanel.ViewModels;
using ZeroFall.AiPanel.Views;
using ZeroFall.Base;
using ZeroFall.Base.AiTools;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel;

public class AiPanelModule : IModule
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<LookService>();
        services.AddSingleton<AskToolService>();
        services.AddSingleton<ContextToolService>();
        services.AddSingleton<ReconPaidOperationGate>();
        services.AddSingleton<IReconPaidOperationGate>(sp => sp.GetRequiredService<ReconPaidOperationGate>());
        services.AddSingleton<WebToolService>();
        services.AddSingleton<TodoToolService>();
        services.AddSingleton<SubAgentRunner>();
        services.AddSingleton<SubAgentSessionHub>();
        services.AddSingleton<SubAgentToolService>();
        services.AddSingleton<AiToolRegistry>();
        services.AddSingleton<ICapabilityCatalog, CapabilityCatalog>();
        services.AddSingleton<ToolExecutionOrchestrator>();
        services.AddSingleton<ProxyCapabilityRegistrar>();
        services.AddSingleton<McpAiToolBridge>();
        services.AddSingleton<IMcpServerProbe, McpServerProbe>();
        services.AddSingleton<IAiChatSessionStore, AiChatSessionStore>();
        services.AddTransient<IChatSessionSurfaceManager, ChatSessionSurfaceManager>();
        services.AddSingleton<ChatContextCompressionService>();
        services.AddSingleton<ChatContextUsageService>();
        services.AddSingleton<ChatSessionCoordinator>();
        services.AddSingleton<ChatSessionApiPayloadBuilder>();
        services.AddSingleton<ChatSendOrchestrator>();
        services.AddSingleton<IAiToolResultRuntimeStore, AiToolResultRuntimeStore>();
        services.AddSingleton<IAiTodoStore, AiTodoStore>();
        services.AddSingleton<AiChatSessionContext>();
        services.AddSingleton<IAiChatSessionContext>(sp => sp.GetRequiredService<AiChatSessionContext>());
        services.AddSingleton<IChatMarkdownRenderQueue, ChatMarkdownRenderQueue>();
        services.AddSingleton<AiSessionListState>();
        services.AddSingleton(new AiPanelViewModelLifetime(isCoordinatorInstance: true));
        services.AddSingleton<AiSharedPanelHost>();
        services.AddSingleton<AiPanelViewModel>();
        services.AddSingleton<AiSessionDockCoordinator>();
    }

    public void Initialize(IServiceProvider sp)
    {
        var aiToolRegistry = sp.GetRequiredService<AiToolRegistry>();
        var capabilityCatalog = sp.GetRequiredService<ICapabilityCatalog>();
        var orchestrator = sp.GetRequiredService<ToolExecutionOrchestrator>();
        var proxyCapabilityRegistrar = sp.GetRequiredService<ProxyCapabilityRegistrar>();

        AiToolRegistration_LookService.Register(aiToolRegistry, sp);
        AiToolRegistration_ContextToolService.Register(aiToolRegistry, sp);
        AiToolRegistration_AskToolService.Register(aiToolRegistry, sp);
        AiToolRegistration_WebToolService.Register(aiToolRegistry, sp);
        AiToolRegistration_TodoToolService.Register(aiToolRegistry, sp);
        AiToolRegistration_SubAgentToolService.Register(aiToolRegistry, sp);
        proxyCapabilityRegistrar.Register(capabilityCatalog);
        aiToolRegistry.RegisterFromCatalog(capabilityCatalog, orchestrator);
        _ = sp.GetRequiredService<AiSessionDockCoordinator>();
        _ = sp.GetRequiredService<IAiTodoStore>();
        _ = sp.GetRequiredService<IAiChatSessionStore>();
    }
}
