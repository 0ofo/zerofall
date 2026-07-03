using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ZeroFall.Base.AiTools;
using ZeroFall.Base.Events;
using ZeroFall.Browser.Services;
using ZeroFall.Browser.Tools;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Browser.Views;
using ZeroFall.Dock.Controls;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using ZeroFall.Traffic.Ingest;

namespace ZeroFall.Browser;

public interface IBrowserFeatureRegistrar
{
    void RegisterServices(IServiceCollection services);
    void Initialize(IServiceProvider sp);
}

public sealed class BrowserCoreFeatureRegistrar : IBrowserFeatureRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<ICdpBridge>(_ => CdpBridge.Instance);
        services.AddSingleton<IBrowserTabManager, BrowserTabManager>();
        services.AddSingleton<WebsiteTreeRootContext>(sp =>
            new WebsiteTreeRootContext(sp.GetRequiredService<IBrowserTabManager>()));
        services.AddSingleton<IUiLayoutTabExtraProvider, BrowserUiLayoutTabExtraProvider>();
        services.AddSingleton<TrafficIngestGateway>();
        services.AddSingleton<ITrafficCaptureSink>(sp => sp.GetRequiredService<TrafficIngestGateway>());
        services.AddSingleton<ITargetScopeService, TargetScopeService>();
        services.AddSingleton<BrowserCoreEventCoordinator>();
    }

    public void Initialize(IServiceProvider sp)
    {
        var settingsService = sp.GetRequiredService<ISettingsService>();
        var proxyGatewayService = sp.GetRequiredService<IProxyGatewayService>();
        _ = settingsService.Load();
        WebView2ProxyOptions.SetFromGatewayState(proxyGatewayService.CurrentState);
        _ = sp.GetRequiredService<BrowserCoreEventCoordinator>();
    }
}

public sealed class BrowserCoreEventCoordinator : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IEventBus _eventBus;
    private readonly ICdpBridge _cdpBridge;
    private readonly IBrowserTabManager _browserTabManager;
    private readonly Action<OpenBrowserTabRequestedEvent> _openBrowserTabHandler;
    private readonly Action<TabClosedEvent> _tabClosedHandler;

    public BrowserCoreEventCoordinator(
        IServiceProvider services,
        IEventBus eventBus,
        ICdpBridge cdpBridge,
        IBrowserTabManager browserTabManager)
    {
        _services = services;
        _eventBus = eventBus;
        _cdpBridge = cdpBridge;
        _browserTabManager = browserTabManager;
        _openBrowserTabHandler = OnOpenBrowserTabRequested;
        _tabClosedHandler = OnContentTabClosed;
        _eventBus.Subscribe(_openBrowserTabHandler);
        _eventBus.Subscribe(_tabClosedHandler);
    }

    private void OnContentTabClosed(TabClosedEvent e)
    {
        if (!IsBrowserContentTabId(e.Tab.Id))
            return;

        if (NonReloadableTabContent.Resolve<BrowserTabView>(e.Tab.Content) is { } browserView)
            browserView.DisposeBrowserResources();

        _cdpBridge.Unregister(e.Tab.Id);
        _browserTabManager.OnTabUnregistered(e.Tab.Id);
    }

    private void OnOpenBrowserTabRequested(OpenBrowserTabRequestedEvent e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            OpenBrowserTabCore(e);
            return;
        }

        Dispatcher.UIThread.Post(() => OpenBrowserTabCore(e));
    }

    private void OpenBrowserTabCore(OpenBrowserTabRequestedEvent e)
    {
        var tabId = string.IsNullOrWhiteSpace(e.TabId)
            ? $"browser-{Guid.NewGuid():N}"
            : e.TabId.Trim();
        var tab = CreateBrowserContentTab(
            tabId,
            e.Title ?? "新标签页",
            e.Url,
            "SemiIconGlobeStroked",
            _eventBus,
            _cdpBridge,
            _browserTabManager,
            _services);
        _eventBus.Publish(new AddContentTabEvent(tab));
    }

    private static bool IsBrowserContentTabId(string tabId) =>
        tabId.StartsWith("browser", StringComparison.Ordinal)
        || tabId.StartsWith("fetch-", StringComparison.Ordinal);

    private static DockTabItemViewModel CreateBrowserContentTab(
        string tabId,
        string initialTitle,
        string initialUrl,
        string iconKey,
        IEventBus eventBus,
        ICdpBridge cdpBridge,
        IBrowserTabManager browserTabManager,
        IServiceProvider sp)
    {
        var tabVm = new BrowserTabViewModel(initialUrl, eventBus)
        {
            TabId = tabId,
            Title = initialTitle,
            CdpBridge = cdpBridge,
            TabManager = browserTabManager,
            CaptureSink = sp.GetRequiredService<ITrafficCaptureSink>()
        };
        var browserView = new BrowserTabView { DataContext = tabVm, Tag = tabVm };
        var icon = IconHelper.GetIcon(iconKey) ?? IconHelper.GetBrowserIcon();

        return new DockTabItemViewModel
        {
            Id = tabId,
            Title = tabVm.Title,
            Icon = icon,
            IsClosable = true,
            Content = TabContent.NonReloadable(browserView),
        };
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe(_openBrowserTabHandler);
        _eventBus.Unsubscribe(_tabClosedHandler);
    }
}

public sealed class BrowserTrafficFeatureRegistrar : IBrowserFeatureRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<TrafficArchiveService>();
        services.AddSingleton<TrafficArchiveIngestCoordinator>();
        services.AddSingleton<SiteTechnologyStore>();
        services.AddSingleton<FingerprintAuditJournalService>();
        services.AddSingleton<TrafficMonitorTabViewModel>(sp =>
        {
            var eventBus = sp.GetRequiredService<IEventBus>();
            var trafficArchive = sp.GetRequiredService<TrafficArchiveService>();
            var ingestGateway = sp.GetRequiredService<TrafficIngestGateway>();
            var targetScope = sp.GetRequiredService<ITargetScopeService>();
            return new TrafficMonitorTabViewModel(eventBus, trafficArchive, targetScope, ingestGateway) { Title = "网络监控" };
        });
        services.AddSingleton<TrafficFingerprintService>(sp =>
            new TrafficFingerprintService(
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<SiteTechnologyStore>(),
                sp.GetRequiredService<FingerprintAuditJournalService>(),
                sp.GetRequiredService<IOutboundHttpClientFactory>(),
                sp.GetRequiredService<TrafficMonitorTabViewModel>(),
                sp.GetRequiredService<WebsiteTreeRootContext>()));
    }

    public void Initialize(IServiceProvider sp)
    {
        // 须在 ProjectOpenedEvent 之前完成订阅与 VM 构造，否则自动恢复项目时无法加载流量归档/网站树。
        _ = sp.GetRequiredService<TrafficArchiveService>();
        _ = sp.GetRequiredService<TrafficArchiveIngestCoordinator>();
        _ = sp.GetRequiredService<TrafficFingerprintService>();

        var dockRegistry = sp.GetRequiredService<IDockLayoutRegistry>();
        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom,
            TabId = "traffic-monitor",
            Title = "网络监控",
            IconKey = "SemiIconList",
            CreateTab = () =>
            {
                var trafficVm = sp.GetRequiredService<TrafficMonitorTabViewModel>();
                var icon = IconHelper.GetIcon("SemiIconList");
                return new DockTabItemViewModel
                {
                    Id = "traffic-monitor",
                    Title = "网络监控",
                    Icon = icon,
                    Content = new TrafficMonitorView { DataContext = trafficVm }
                };
            }
        });
    }
}

public sealed class BrowserHttpToolsFeatureRegistrar : IBrowserFeatureRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<HttpReplayTabViewModel>(sp =>
        {
            var eventBus = sp.GetRequiredService<IEventBus>();
            var httpClientFactory = sp.GetRequiredService<IOutboundHttpClientFactory>();
            return new HttpReplayTabViewModel(eventBus, httpClientFactory) { Title = "HTTP 重放" };
        });
        services.AddSingleton<HttpIntruderTabViewModel>(sp =>
        {
            var eventBus = sp.GetRequiredService<IEventBus>();
            var httpClientFactory = sp.GetRequiredService<IOutboundHttpClientFactory>();
            return new HttpIntruderTabViewModel(eventBus, httpClientFactory) { Title = "Intruder" };
        });
        services.AddSingleton<HttpDecodeTabViewModel>(sp =>
            new HttpDecodeTabViewModel(sp.GetRequiredService<IEventBus>()));
        services.AddSingleton<HttpDiffTabViewModel>(sp =>
            new HttpDiffTabViewModel(sp.GetRequiredService<IEventBus>()));
    }

    public void Initialize(IServiceProvider sp)
    {
        HttpRequestEditorMenuRegistry.Instance.Register(new HttpReplayRequestEditorMenuContributor());

        var dockRegistry = sp.GetRequiredService<IDockLayoutRegistry>();
        RegisterHttpReplay(dockRegistry, sp);
        RegisterHttpIntruder(dockRegistry, sp);
        RegisterHttpDecode(dockRegistry, sp);
        RegisterHttpDiff(dockRegistry, sp);
        RegisterReplayHistory(dockRegistry, sp);
    }

    private static void RegisterHttpReplay(IDockLayoutRegistry dockRegistry, IServiceProvider sp) =>
        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Content,
            TabId = "http-replay",
            Title = "HTTP 重放",
            IconKey = "SemiIconExchange",
            IsDefaultVisible = false,
            CreateTab = () =>
            {
                var replayVm = sp.GetRequiredService<HttpReplayTabViewModel>();
                var icon = IconHelper.GetIcon("SemiIconExchange");
                return new DockTabItemViewModel
                {
                    Id = "http-replay",
                    Title = "HTTP 重放",
                    Icon = icon,
                    Content = new HttpReplayView { DataContext = replayVm },
                    IsClosable = false
                };
            }
        });

    private static void RegisterHttpIntruder(IDockLayoutRegistry dockRegistry, IServiceProvider sp) =>
        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Content,
            TabId = "http-intruder",
            Title = "Intruder",
            IconKey = "SemiIconPulse",
            IsDefaultVisible = false,
            CreateTab = () =>
            {
                var intruderVm = sp.GetRequiredService<HttpIntruderTabViewModel>();
                var icon = IconHelper.GetIcon("SemiIconPulse");
                return new DockTabItemViewModel
                {
                    Id = "http-intruder",
                    Title = "Intruder",
                    Icon = icon,
                    Content = new HttpIntruderView { DataContext = intruderVm },
                    IsClosable = true
                };
            }
        });

    private static void RegisterHttpDecode(IDockLayoutRegistry dockRegistry, IServiceProvider sp) =>
        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Content,
            TabId = "http-decode",
            Title = "Decoder",
            IconKey = "SemiIconCode",
            IsDefaultVisible = false,
            CreateTab = () =>
            {
                var decodeVm = sp.GetRequiredService<HttpDecodeTabViewModel>();
                var icon = IconHelper.GetIcon("SemiIconCode");
                return new DockTabItemViewModel
                {
                    Id = "http-decode",
                    Title = "Decoder",
                    Icon = icon,
                    Content = new HttpDecodeView { DataContext = decodeVm },
                    IsClosable = true
                };
            }
        });

    private static void RegisterHttpDiff(IDockLayoutRegistry dockRegistry, IServiceProvider sp) =>
        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Content,
            TabId = "http-diff",
            Title = "Comparer",
            IconKey = "SemiIconComponent",
            IsDefaultVisible = false,
            CreateTab = () =>
            {
                var diffVm = sp.GetRequiredService<HttpDiffTabViewModel>();
                var icon = IconHelper.GetIcon("SemiIconComponent");
                return new DockTabItemViewModel
                {
                    Id = "http-diff",
                    Title = "Comparer",
                    Icon = icon,
                    Content = new HttpDiffView { DataContext = diffVm },
                    IsClosable = true
                };
            }
        });

    private static void RegisterReplayHistory(IDockLayoutRegistry dockRegistry, IServiceProvider sp) =>
        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Left,
            TabId = "http-replay-history",
            Title = "重放历史",
            IconKey = "SemiIconHistory",
            IsDefaultVisible = false,
            CreateTab = () =>
            {
                var replayVm = sp.GetRequiredService<HttpReplayTabViewModel>();
                var icon = IconHelper.GetIcon("SemiIconHistory");
                return new DockTabItemViewModel
                {
                    Id = "http-replay-history",
                    Title = "重放历史",
                    Icon = icon,
                    Content = new HttpReplayHistoryView { DataContext = replayVm },
                    IsClosable = false
                };
            }
        });
}

public sealed class BrowserWebsiteTreeFeatureRegistrar : IBrowserFeatureRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<WebsiteTreeViewModel>(sp =>
        {
            var eventBus = sp.GetRequiredService<IEventBus>();
            var targetScope = sp.GetRequiredService<ITargetScopeService>();
            var trafficArchive = sp.GetRequiredService<TrafficArchiveService>();
            var technologyStore = sp.GetRequiredService<SiteTechnologyStore>();
            var fingerprintService = sp.GetRequiredService<TrafficFingerprintService>();
            return new WebsiteTreeViewModel(
                eventBus,
                targetScope,
                trafficArchive,
                technologyStore,
                fingerprintService,
                sp.GetRequiredService<WebsiteTreeRootContext>()) { Title = "网站树" };
        });
    }

    public void Initialize(IServiceProvider sp)
    {
        _ = sp.GetRequiredService<WebsiteTreeViewModel>();

        var dockRegistry = sp.GetRequiredService<IDockLayoutRegistry>();
        dockRegistry.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Left,
            TabId = "website-tree",
            Title = "网站树",
            IconKey = "SemiIconTreeTriangleDown",
            CreateTab = () =>
            {
                var trafficVm = sp.GetRequiredService<TrafficMonitorTabViewModel>();
                var websiteTreeVm = sp.GetRequiredService<WebsiteTreeViewModel>();
                var icon = IconHelper.GetIcon("SemiIconTreeTriangleDown");
                return new DockTabItemViewModel
                {
                    Id = "website-tree",
                    Title = "网站树",
                    Icon = icon,
                    Content = new WebsiteTreeView
                    {
                        DataContext = websiteTreeVm,
                        TrafficMonitor = trafficVm
                    },
                    IsClosable = false
                };
            }
        });
    }
}

public sealed class BrowserFetchAiToolsFeatureRegistrar : IBrowserFeatureRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<HttpFetchAiToolService>();
    }

    public void Initialize(IServiceProvider sp)
    {
        var aiToolRegistry = sp.GetRequiredService<AiToolRegistry>();
        AiToolRegistration_HttpFetchAiToolService.Register(aiToolRegistry, sp);
    }
}

public sealed class BrowserAiToolsFeatureRegistrar : IBrowserFeatureRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<CdpHtmlInjectionService>();
        services.AddSingleton<BrowserAiToolService>(sp =>
            new BrowserAiToolService(
                sp.GetRequiredService<ICdpBridge>(),
                sp.GetRequiredService<IBrowserTabManager>(),
                sp.GetRequiredService<WebsiteTreeViewModel>(),
                sp.GetRequiredService<IOutboundHttpClientFactory>(),
                sp.GetRequiredService<CdpHtmlInjectionService>(),
                sp.GetRequiredService<ITrafficCaptureSink>(),
                sp.GetRequiredService<IAiChatRunContext>()));
    }

    public void Initialize(IServiceProvider sp)
    {
        var aiToolRegistry = sp.GetRequiredService<AiToolRegistry>();
        AiToolRegistration_BrowserAiToolService.Register(aiToolRegistry, sp);
    }
}
