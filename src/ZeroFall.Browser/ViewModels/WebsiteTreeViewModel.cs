using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Browser.Services;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using ZeroFall.Traffic.Ingest;

namespace ZeroFall.Browser.ViewModels;

public partial class WebsiteTreeViewModel : BrowserTabViewModelBase, IDisposable
{
    private const int BaseTrafficEventsPerUiBatch = 25;
    private const int BurstTrafficEventsPerUiBatch = 100;
    private const int TrafficBurstQueueThreshold = 200;
    private const int MaxArchiveTreeEntries = 3000;
    private const int MaxMemoryRequestLeavesPerFolder = 300;
    private const string TargetScopeRootId = "target-scope";
    private const string WebsiteTreeTabId = "website-tree";
    private static readonly TimeSpan BaseDisplayTreeRefreshDebounce = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan BurstDisplayTreeRefreshDebounce = TimeSpan.FromMilliseconds(800);

    private readonly IEventBus _eventBus;
    private readonly ITargetScopeService _targetScope;
    private readonly TrafficArchiveService _trafficArchive;
    private readonly SiteTechnologyStore _technologyStore;
    private readonly TrafficFingerprintService _fingerprintService;
    private readonly WebsiteTreeRootContext _rootContext;
    private readonly IDisposable _trafficSub;
    private readonly IDisposable _clearedSub;
    private readonly IDisposable _projectOpenedSubscription;
    private readonly IDisposable _documentNavigatedSub;
    private readonly IDisposable _dockTabSelectedSub;
    private readonly IDisposable _panelVisibilitySub;
    private readonly WebsiteTreeNodeViewModel _targetScopeRoot;
    // 树根：地址栏出现过的网站（host+端口，80/443 省略）
    private readonly Dictionary<string, WebsiteTreeNodeViewModel> _rootSiteNodes = new(StringComparer.OrdinalIgnoreCase);
    // 关联子站点：被某根站点页面触发的不同 host 请求，挂在对应根下。key = "rootAuthority|requestAuthority"
    private readonly Dictionary<string, WebsiteTreeNodeViewModel> _assocSiteNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WebsiteTreeNodeViewModel> _pathNodes = new(StringComparer.Ordinal);
    private readonly Queue<WebTrafficRecordedEvent> _pendingTrafficEvents = new();
    private readonly Queue<WebTrafficRecordedEvent> _orphanTrafficEvents = new();
    private readonly object _memoryTreeGate = new();
    private readonly object _pendingTrafficGate = new();
    private bool _trafficFlushScheduled;
    private int _displayBuildGeneration;
    private readonly HashSet<string> _expandedNodeIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtySiteAuthorities = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sitesNeedingNormalize = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _displayTreeRefreshTimer;
    private bool _leftPanelVisible = true;
    private bool _websiteTreeTabSelected = true;
    private bool _displayRefreshDeferred;

    public ObservableCollection<WebsiteTreeNodeViewModel> RootNodes { get; } = new();

    private bool IsDisplayPanelActive => _leftPanelVisible && _websiteTreeTabSelected;

    [ObservableProperty]
    private WebsiteTreeNodeViewModel? _selectedNode;

    public WebsiteTreeViewModel(
        IEventBus eventBus,
        ITargetScopeService targetScope,
        TrafficArchiveService trafficArchive,
        SiteTechnologyStore technologyStore,
        TrafficFingerprintService fingerprintService,
        WebsiteTreeRootContext rootContext)
    {
        _eventBus = eventBus;
        _targetScope = targetScope;
        _trafficArchive = trafficArchive;
        _technologyStore = technologyStore;
        _fingerprintService = fingerprintService;
        _rootContext = rootContext;
        _technologyStore.TechnologiesChanged += OnTechnologiesChanged;
        _targetScopeRoot = new WebsiteTreeNodeViewModel
        {
            Id = TargetScopeRootId,
            Title = "目标空间",
            NodeType = WebsiteTreeNodeType.TargetScope,
            ItemIcon = WebsiteTreeIcons.TargetScope,
            IsExpanded = true
        };
        _targetScope.Changed += OnTargetScopeChanged;
        _trafficSub = eventBus.SubscribeDisposable<TrafficEntryIngestedEvent>(OnTrafficIngested);
        _clearedSub = eventBus.SubscribeDisposable<TrafficRecordsClearedEvent>(_ => Clear());
        _projectOpenedSubscription = eventBus.SubscribeDisposable<ProjectOpenedEvent>(OnProjectOpened);
        _documentNavigatedSub = eventBus.SubscribeDisposable<BrowserTabDocumentNavigatedEvent>(OnDocumentNavigated);
        _dockTabSelectedSub = eventBus.SubscribeDisposable<DockTabSelectedEvent>(OnDockTabSelected);
        _panelVisibilitySub = eventBus.SubscribeDisposable<PanelVisibilityChangedEvent>(OnPanelVisibilityChanged);
        _displayTreeRefreshTimer = new DispatcherTimer { Interval = BaseDisplayTreeRefreshDebounce };
        _displayTreeRefreshTimer.Tick += OnDisplayTreeRefreshTimerTick;
        RebuildTargetScopeChildren();
        RefreshDisplayTreeFull();
        TryScheduleArchiveRestoreIfProjectAlreadyOpen();
    }

    private void OnDockTabSelected(DockTabSelectedEvent e)
    {
        if (e.Region != DockPosition.Left)
            return;

        var wasActive = IsDisplayPanelActive;
        _websiteTreeTabSelected = string.Equals(e.Tab?.Id, WebsiteTreeTabId, StringComparison.Ordinal);
        if (!wasActive && IsDisplayPanelActive)
            Dispatcher.UIThread.Post(FlushDeferredDisplayRefresh, DispatcherPriority.ApplicationIdle);
    }

    private void OnPanelVisibilityChanged(PanelVisibilityChangedEvent e)
    {
        if (e.Position != DockPosition.Left)
            return;

        var wasActive = IsDisplayPanelActive;
        _leftPanelVisible = e.IsVisible;
        if (!wasActive && IsDisplayPanelActive)
            Dispatcher.UIThread.Post(FlushDeferredDisplayRefresh, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>用户展开站点时，立即同步重建该站点展示子树。</summary>
    public void NotifyDisplayNodeExpanding(WebsiteTreeNodeViewModel node)
    {
        if (string.IsNullOrEmpty(node.Id))
            return;

        CaptureExpandedNodeIdsFromDisplay();
        _expandedNodeIds.Add(node.Id);

        var rootAuthority = ResolveRootAuthorityForDisplayNode(node);
        if (string.IsNullOrEmpty(rootAuthority))
            return;

        WebsiteTreeNodeViewModel? source;
        lock (_memoryTreeGate)
        {
            if (!_rootSiteNodes.TryGetValue(rootAuthority, out source))
                return;
        }

        Interlocked.Increment(ref _displayBuildGeneration);
        ReplaceOrInsertSiteRootNode(BuildSiteDisplayCloneSync(source, _expandedNodeIds));
    }

    private static string? ResolveRootAuthorityForDisplayNode(WebsiteTreeNodeViewModel node)
    {
        var fromId = ResolveRootAuthorityFromNodeId(node.Id);
        if (!string.IsNullOrEmpty(fromId))
            return fromId;

        if (node.NodeType is WebsiteTreeNodeType.Path or WebsiteTreeNodeType.Request)
            return ResolveRootAuthorityFromNodeId(GetPathRootIdFromPathNode(node));

        return null;
    }

    private void FlushDeferredDisplayRefresh()
    {
        if (!_displayRefreshDeferred && _dirtySiteAuthorities.Count == 0 && _sitesNeedingNormalize.Count == 0)
            return;

        _displayRefreshDeferred = false;
        foreach (var key in _rootSiteNodes.Keys)
            _dirtySiteAuthorities.Add(key);

        foreach (var key in _rootSiteNodes.Keys)
            _sitesNeedingNormalize.Add(key);

        ScheduleRefreshDisplayTree();
    }

    private void ScheduleTrafficBatchProcessing()
    {
        var priority = IsDisplayPanelActive ? DispatcherPriority.Background : DispatcherPriority.ApplicationIdle;
        Dispatcher.UIThread.Post(ProcessPendingTrafficBatch, priority);
    }

    private void OnDocumentNavigated(BrowserTabDocumentNavigatedEvent e)
    {
        _rootContext.OnDocumentNavigated(e.TabId, e.PageSessionId, e.TopLevelUrl);
        Dispatcher.UIThread.Post(TryFlushOrphanTrafficAndRefresh, DispatcherPriority.Background);
    }

    private void TryFlushOrphanTrafficAndRefresh()
    {
        if (!FlushOrphanTrafficEvents(out var dirtySites))
            return;

        ScheduleRefreshDisplayTree(normalize: true, dirtySites: dirtySites);
    }

    private void TryScheduleArchiveRestoreIfProjectAlreadyOpen()
    {
        if (!_trafficArchive.HasDatabase)
            return;

        ScheduleArchiveRestore();
    }

    private void OnProjectOpened(ProjectOpenedEvent e)
    {
        _technologyStore.Clear();
        ScheduleArchiveRestore();
    }

    private void ScheduleArchiveRestore()
    {
        StartupPerformance.RunOnUiIdle(async () =>
        {
            try
            {
                await _trafficArchive.WaitForReadyAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await LoadFromArchiveAsync().ConfigureAwait(false);
        });
    }

    private async Task LoadFromArchiveAsync()
    {
        var entries = await _trafficArchive.QueryAsync(
            TrafficFilterSpec.SiteMapRestore,
            string.Empty,
            onlyLastBrowserTab: false,
            MaxArchiveTreeEntries,
            TrafficArchiveProjection.SiteMapMeta).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ClearSiteMap();
            _rootContext.BeginReplay();
            var events = new List<WebTrafficRecordedEvent>(entries.Count);
            for (var i = entries.Count - 1; i >= 0; i--)
                events.Add(ToTrafficEvent(entries[i]));

            foreach (var ev in events)
                _rootContext.OnReplayEvent(ev);

            lock (_memoryTreeGate)
            {
                foreach (var ev in events)
                    AddTrafficRecord(ev, fromArchiveReplay: true);
            }

            _rootContext.EndReplay();
            NormalizeSitePathTrees();
            RefreshDisplayTreeFull();
        });
    }

    private static WebTrafficRecordedEvent ToTrafficEvent(TrafficLogEntryViewModel entry) =>
        TrafficEntryMetadataComputer.ToEvent(entry);

    private void OnTargetScopeChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            RebuildTargetScopeChildren();
            RefreshTargetScopeDisplayNode();
        }, DispatcherPriority.Background);
    }

    private void RebuildTargetScopeChildren()
    {
        _targetScopeRoot.Children.Clear();
        foreach (var host in _targetScope.Hosts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            _targetScopeRoot.Children.Add(new WebsiteTreeNodeViewModel
            {
                Id = $"scope:{host}",
                Title = host,
                Host = host,
                NodeType = WebsiteTreeNodeType.ScopeHost,
                ItemIcon = WebsiteTreeIcons.ScopeHost
            });
        }
    }

    public bool TryAddNodeToTargetScope(WebsiteTreeNodeViewModel? node)
    {
        var host = ResolveScopeHost(node);
        return !string.IsNullOrEmpty(host) && _targetScope.AddHost(host);
    }

    public bool TryRemoveScopeHost(WebsiteTreeNodeViewModel? node)
    {
        if (node?.NodeType != WebsiteTreeNodeType.ScopeHost)
            return false;

        var host = string.IsNullOrWhiteSpace(node.Host) ? node.Title : node.Host;
        return _targetScope.RemoveHost(host);
    }

    private static string? ResolveScopeHost(WebsiteTreeNodeViewModel? node) =>
        node?.NodeType switch
        {
            WebsiteTreeNodeType.ScopeHost => TargetScopeService.NormalizeHost(node.Host)
                ?? TargetScopeService.NormalizeHost(node.Title),
            WebsiteTreeNodeType.Site => TargetScopeService.NormalizeHost(node.Host)
                ?? TargetScopeService.NormalizeHost(node.Title),
            WebsiteTreeNodeType.Request => TargetScopeService.NormalizeHost(node.Host),
            _ => null
        };

    private void OnTrafficIngested(TrafficEntryIngestedEvent e)
    {
        if (e.Decision == TrafficCaptureDedup.Decision.SupersedeProxy
            && !string.IsNullOrWhiteSpace(e.SupersededEntryId))
            RemoveRequestNodeByEntryId(e.SupersededEntryId);

        var trafficEvent = TrafficEntryMetadataComputer.ToEvent(e.Entry);
        lock (_pendingTrafficGate)
        {
            _pendingTrafficEvents.Enqueue(trafficEvent);
            if (_trafficFlushScheduled)
                return;

            _trafficFlushScheduled = true;
        }

        Dispatcher.UIThread.Post(ScheduleTrafficBatchProcessing, DispatcherPriority.Background);
    }

    private void ProcessPendingTrafficBatch()
    {
        var batchSize = GetTrafficBatchSize();
        var batch = new List<WebTrafficRecordedEvent>(batchSize);
        var dirtySites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (batch.Count < batchSize)
        {
            lock (_pendingTrafficGate)
            {
                if (_pendingTrafficEvents.Count == 0)
                {
                    _trafficFlushScheduled = false;
                    break;
                }

                batch.Add(_pendingTrafficEvents.Dequeue());
            }
        }

        if (batch.Count == 0)
            return;

        lock (_memoryTreeGate)
        {
            foreach (var e in batch)
                _rootContext.OnReplayEvent(e);

            foreach (var e in batch)
            {
                var rootAuthority = AddTrafficRecord(e);
                if (!string.IsNullOrEmpty(rootAuthority))
                    dirtySites.Add(rootAuthority);
            }
        }

        if (FlushOrphanTrafficEvents(out var orphanDirtySites))
        {
            foreach (var site in orphanDirtySites)
                dirtySites.Add(site);
        }

        if (dirtySites.Count > 0)
        {
            foreach (var site in dirtySites)
                _sitesNeedingNormalize.Add(site);

            ScheduleRefreshDisplayTree(dirtySites: dirtySites);
        }

        lock (_pendingTrafficGate)
        {
            if (_pendingTrafficEvents.Count == 0)
            {
                _trafficFlushScheduled = false;
                return;
            }
        }

        ScheduleTrafficBatchProcessing();
    }

    private int GetTrafficBatchSize()
    {
        lock (_pendingTrafficGate)
            return _pendingTrafficEvents.Count >= TrafficBurstQueueThreshold
                ? BurstTrafficEventsPerUiBatch
                : BaseTrafficEventsPerUiBatch;
    }

    private void EnqueueOrphanTraffic(WebTrafficRecordedEvent e)
    {
        lock (_pendingTrafficGate)
            _orphanTrafficEvents.Enqueue(e);
    }

    private bool FlushOrphanTrafficEvents(out HashSet<string> dirtySites)
    {
        dirtySites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_orphanTrafficEvents.Count == 0)
            return false;

        var retry = new List<WebTrafficRecordedEvent>();
        lock (_pendingTrafficGate)
        {
            while (_orphanTrafficEvents.Count > 0)
                retry.Add(_orphanTrafficEvents.Dequeue());
        }

        var stillOrphan = new List<WebTrafficRecordedEvent>();
        lock (_memoryTreeGate)
        {
            foreach (var e in retry)
            {
                var rootAuthority = AddTrafficRecord(e);
                if (!string.IsNullOrEmpty(rootAuthority))
                    dirtySites.Add(rootAuthority);
                else
                    stillOrphan.Add(e);
            }
        }

        if (stillOrphan.Count > 0)
        {
            lock (_pendingTrafficGate)
            {
                foreach (var e in stillOrphan)
                    _orphanTrafficEvents.Enqueue(e);
            }
        }

        return dirtySites.Count > 0;
    }

    private string? AddTrafficRecord(WebTrafficRecordedEvent e, bool fromArchiveReplay = false)
    {
        if (fromArchiveReplay && RequestEntryExistsInTree(e.EntryId))
            return null;

        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri))
            return null;

        // 根网站 = 地址栏文档主站；CDN/资源域名挂关联子站，不能自成根。
        var rootAuthority = WebsiteTreeRootContext.ResolveRootAuthority(e, _rootContext);
        if (string.IsNullOrEmpty(rootAuthority))
        {
            if (!fromArchiveReplay)
                EnqueueOrphanTraffic(e);
            return null;
        }

        if (WebsiteTreeHostClassifier.IsResourceHostName(rootAuthority))
        {
            rootAuthority = _rootContext.TryGetDocumentRootAuthority(e);
            if (string.IsNullOrEmpty(rootAuthority))
            {
                if (!fromArchiveReplay)
                    EnqueueOrphanTraffic(e);
                return null;
            }
        }

        if (!_rootSiteNodes.TryGetValue(rootAuthority, out var rootNode))
        {
            rootNode = new WebsiteTreeNodeViewModel
            {
                Id = $"site:{rootAuthority}",
                Title = rootAuthority,
                Host = rootAuthority,
                NodeType = WebsiteTreeNodeType.Site
            };
            _rootSiteNodes[rootAuthority] = rootNode;
        }

        // 路径树根：同站直接用根节点；不同 host 作关联子站点挂在根下。
        var requestAuthority = ResolveSiteKey(uri);
        WebsiteTreeNodeViewModel pathRoot;
        if (string.Equals(rootAuthority, requestAuthority, StringComparison.OrdinalIgnoreCase))
        {
            pathRoot = rootNode;
        }
        else
        {
            var assocKey = $"{rootAuthority}|{requestAuthority}";
            if (!_assocSiteNodes.TryGetValue(assocKey, out var assocNode))
            {
                assocNode = new WebsiteTreeNodeViewModel
                {
                    Id = $"assoc:{assocKey}",
                    Title = requestAuthority,
                    Host = requestAuthority,
                    NodeType = WebsiteTreeNodeType.Site
                };
                _assocSiteNodes[assocKey] = assocNode;
                rootNode.Children.Add(assocNode);
            }
            pathRoot = assocNode;
        }

        var current = pathRoot;
        // data:/blob: 的“路径”含 base64 等大量 '/'，不能按目录分段。
        if (!ShouldSkipPathSegments(uri))
        {
            var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            current = EnsurePathParent(pathRoot, pathRoot, segments);
        }

        // 每个请求都单独建节点（不折叠计数）；展示层排序，内存层追加即可。
        TrimOldRequestLeaves(current);
        current.Children.Add(new WebsiteTreeNodeViewModel
        {
            Id = $"req:{e.EntryId}",
            Title = BuildRequestDisplayTitle(e.Method, uri, e.Url),
            Host = string.IsNullOrWhiteSpace(uri.Host) ? string.Empty : uri.Host.ToLowerInvariant(),
            RequestPath = BuildRequestPathKey(e.Method, uri),
            NodeType = WebsiteTreeNodeType.Request,
            EntryId = e.EntryId,
            ItemIcon = WebsiteTreeIcons.ResolveRequestIcon(e.Status, e.ResponseHeaders, uri)
        });

        return rootAuthority;
    }

    private static void TrimOldRequestLeaves(WebsiteTreeNodeViewModel parent)
    {
        var requestIndices = new List<int>();
        for (var i = 0; i < parent.Children.Count; i++)
        {
            if (parent.Children[i].NodeType == WebsiteTreeNodeType.Request)
                requestIndices.Add(i);
        }

        var toRemove = requestIndices.Count - MaxMemoryRequestLeavesPerFolder + 1;
        if (toRemove <= 0)
            return;

        for (var r = toRemove - 1; r >= 0; r--)
            parent.Children.RemoveAt(requestIndices[r]);
    }

    /// <summary>请求合并键：方法 + 路径（不含 query/fragment），用于文件夹聚合分组。</summary>
    private static string BuildRequestPathKey(string method, Uri uri)
    {
        if (ShouldSkipPathSegments(uri))
            return $"{method} {uri.Scheme}";
        var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        return $"{method} {path}";
    }

    /// <summary>网站唯一键：authority（host+端口，小写，80/443 省略）。无 host 回退 scheme。</summary>
    private static string ResolveSiteKey(Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(uri.Host))
            return WebsiteTreeRootContext.NormalizeAuthority(uri);
        return uri.Scheme.ToLowerInvariant();
    }

    private bool RequestEntryExistsInTree(string entryId)
    {
        foreach (var root in _rootSiteNodes.Values)
        {
            if (ContainsRequestEntry(root, entryId))
                return true;
        }

        return false;
    }

    private static bool ContainsRequestEntry(WebsiteTreeNodeViewModel node, string entryId)
    {
        foreach (var child in node.Children)
        {
            if (child.NodeType == WebsiteTreeNodeType.Request
                && string.Equals(child.EntryId, entryId, StringComparison.Ordinal))
                return true;

            if (ContainsRequestEntry(child, entryId))
                return true;
        }

        return false;
    }

    /// <summary>按 URL 路径前缀建节点（不含最后一段文件名）；字典键为 site/path:a/b/c。</summary>
    private WebsiteTreeNodeViewModel EnsurePathParent(
        WebsiteTreeNodeViewModel siteNode,
        WebsiteTreeNodeViewModel siteTreeRoot,
        string[] segments)
    {
        if (segments.Length == 0)
            return siteNode;

        // 只建目录段（最后一段是文件名，留给 Request 叶子）
        var dirCount = segments.Length - 1;
        var acc = string.Empty;
        WebsiteTreeNodeViewModel current = siteNode;
        for (var i = 0; i < dirCount; i++)
        {
            acc = i == 0 ? segments[i] : $"{acc}/{segments[i]}";
            current = GetOrCreatePathNode(siteTreeRoot, siteNode, current, acc, segments[i]);
        }

        return current;
    }

    private WebsiteTreeNodeViewModel GetOrCreatePathNode(
        WebsiteTreeNodeViewModel siteTreeRoot,
        WebsiteTreeNodeViewModel siteNode,
        WebsiteTreeNodeViewModel treeParent,
        string pathPrefix,
        string segmentTitle)
    {
        var pathKey = BuildPathKey(siteNode.Id, pathPrefix);
        if (_pathNodes.TryGetValue(pathKey, out var existing))
        {
            if (!treeParent.Children.Contains(existing))
            {
                RemoveFromTreeParent(siteTreeRoot, existing);
                treeParent.Children.Add(existing);
            }
            return existing;
        }

        var pathNode = new WebsiteTreeNodeViewModel
        {
            Id = pathKey,
            Title = segmentTitle,
            NodeType = WebsiteTreeNodeType.Path,
            ItemIcon = WebsiteTreeIcons.Folder
        };
        _pathNodes[pathKey] = pathNode;
        if (!treeParent.Children.Contains(pathNode))
            treeParent.Children.Add(pathNode);
        return pathNode;
    }

    private void ScheduleRefreshDisplayTree(bool normalize = false, IEnumerable<string>? dirtySites = null)
    {
        if (dirtySites != null)
            MarkSitesDirty(dirtySites, normalize);
        else if (normalize)
        {
            foreach (var key in _rootSiteNodes.Keys)
                _sitesNeedingNormalize.Add(key);
        }

        UpdateDisplayDebounceInterval();
        _displayTreeRefreshTimer.Stop();
        _displayTreeRefreshTimer.Start();
    }

    private void MarkSitesDirty(IEnumerable<string> dirtySites, bool normalize)
    {
        foreach (var site in dirtySites)
        {
            if (string.IsNullOrWhiteSpace(site))
                continue;

            _dirtySiteAuthorities.Add(site);
            if (normalize)
                _sitesNeedingNormalize.Add(site);
        }
    }

    private void UpdateDisplayDebounceInterval()
    {
        int queueDepth;
        lock (_pendingTrafficGate)
            queueDepth = _pendingTrafficEvents.Count;

        var interval = queueDepth >= TrafficBurstQueueThreshold
            ? BurstDisplayTreeRefreshDebounce
            : BaseDisplayTreeRefreshDebounce;

        if (_displayTreeRefreshTimer.Interval != interval)
            _displayTreeRefreshTimer.Interval = interval;
    }

    private void OnDisplayTreeRefreshTimerTick(object? sender, EventArgs e)
    {
        _displayTreeRefreshTimer.Stop();
        UpdateDisplayDebounceInterval();

        if (!IsDisplayPanelActive)
        {
            _displayRefreshDeferred = _displayRefreshDeferred
                                      || _dirtySiteAuthorities.Count > 0
                                      || _sitesNeedingNormalize.Count > 0;
            return;
        }

        if (_sitesNeedingNormalize.Count > 0)
            NormalizeDirtySitePathTrees();

        _displayRefreshDeferred = false;
        if (_dirtySiteAuthorities.Count > 0)
            RefreshDisplayTreePartial();
        else
            RefreshDisplayTreeFull();
    }

    private void RefreshDisplayTreePartial()
    {
        CaptureExpandedNodeIdsFromDisplay();
        var authorities = _dirtySiteAuthorities.ToList();
        _dirtySiteAuthorities.Clear();
        var generation = Interlocked.Increment(ref _displayBuildGeneration);
        foreach (var authority in authorities)
            ApplySiteDisplayBuild(authority, generation);
    }

    private void ApplySiteDisplayBuild(string authority, int generation)
    {
        WebsiteTreeNodeViewModel clone;
        lock (_memoryTreeGate)
        {
            if (!_rootSiteNodes.TryGetValue(authority, out var source))
                return;

            var expandedIds = new HashSet<string>(_expandedNodeIds, StringComparer.Ordinal);
            var techSnapshots = CaptureTechnologySnapshots(source);
            var rootSnapshot = techSnapshots[source.Id];
            clone = WebsiteTreeDisplayBuilder.BuildSiteDisplayClone(
                new WebsiteTreeDisplayBuilder.SiteBuildInput(source, expandedIds, rootSnapshot),
                site => techSnapshots.GetValueOrDefault(site.Id) ?? EmptyTechnologySnapshot);
        }

        if (generation != _displayBuildGeneration)
            return;

        ReplaceOrInsertSiteRootNode(clone);
    }

    private static WebsiteTreeDisplayBuilder.TechnologySnapshot EmptyTechnologySnapshot { get; } =
        new(null, Array.Empty<ZeroFall.Fingerprint.Core.Framework>());

    private void ReplaceOrInsertSiteRootNode(WebsiteTreeNodeViewModel siteClone)
    {
        for (var i = 0; i < RootNodes.Count; i++)
        {
            if (string.Equals(RootNodes[i].Id, siteClone.Id, StringComparison.Ordinal))
            {
                RootNodes[i] = siteClone;
                return;
            }
        }

        RootNodes.Add(siteClone);
    }

    private void RefreshTargetScopeDisplayNode()
    {
        if (!IsDisplayPanelActive)
        {
            _displayRefreshDeferred = true;
            return;
        }

        CaptureExpandedNodeIdsFromDisplay();
        var scopeClone = BuildTargetScopeDisplayClone();
        for (var i = 0; i < RootNodes.Count; i++)
        {
            if (RootNodes[i].NodeType == WebsiteTreeNodeType.TargetScope)
            {
                RootNodes[i] = scopeClone;
                return;
            }
        }

        RootNodes.Insert(0, scopeClone);
    }

    private WebsiteTreeNodeViewModel BuildTargetScopeDisplayClone()
    {
        var scopeClone = CloneRegularNode(_targetScopeRoot, _expandedNodeIds);
        if (!_expandedNodeIds.Contains(TargetScopeRootId))
            scopeClone.IsExpanded = _targetScopeRoot.IsExpanded;
        return scopeClone;
    }

    private void RefreshDisplayTreeFull()
    {
        CaptureExpandedNodeIdsFromDisplay();
        RebuildTargetScopeChildren();
        RootNodes.Clear();
        RootNodes.Add(BuildTargetScopeDisplayClone());
        _dirtySiteAuthorities.Clear();

        var generation = Interlocked.Increment(ref _displayBuildGeneration);
        foreach (var authority in _rootSiteNodes.Keys.ToList())
            ApplySiteDisplayBuild(authority, generation);
    }

    private void CaptureExpandedNodeIdsFromDisplay()
    {
        foreach (var root in RootNodes)
            CollectExpandedNodeIds(root, _expandedNodeIds);
    }

    private static void CollectExpandedNodeIds(WebsiteTreeNodeViewModel node, HashSet<string> ids)
    {
        if (node.IsExpanded)
            ids.Add(node.Id);

        foreach (var child in node.Children)
            CollectExpandedNodeIds(child, ids);
    }

    private void OnTechnologiesChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var key in _rootSiteNodes.Keys)
                _dirtySiteAuthorities.Add(key);
            ScheduleRefreshDisplayTree();
        }, DispatcherPriority.Background);
    }

    private WebsiteTreeNodeViewModel CloneNodeForDisplay(
        WebsiteTreeNodeViewModel source,
        IReadOnlySet<string> expandedIds)
    {
        if (source.NodeType == WebsiteTreeNodeType.Site)
            return BuildSiteDisplayCloneSync(source, expandedIds);

        return CloneRegularNode(source, expandedIds);
    }

    private WebsiteTreeNodeViewModel BuildSiteDisplayCloneSync(
        WebsiteTreeNodeViewModel source,
        IReadOnlySet<string> expandedIds)
    {
        lock (_memoryTreeGate)
        {
            var techSnapshots = CaptureTechnologySnapshots(source);
            var rootSnapshot = techSnapshots[source.Id];
            return WebsiteTreeDisplayBuilder.BuildSiteDisplayClone(
                new WebsiteTreeDisplayBuilder.SiteBuildInput(source, expandedIds, rootSnapshot),
                site => techSnapshots.GetValueOrDefault(site.Id) ?? EmptyTechnologySnapshot);
        }
    }

    private Dictionary<string, WebsiteTreeDisplayBuilder.TechnologySnapshot> CaptureTechnologySnapshots(
        WebsiteTreeNodeViewModel rootSite)
    {
        var result = new Dictionary<string, WebsiteTreeDisplayBuilder.TechnologySnapshot>(StringComparer.Ordinal);
        CaptureSiteTechnologyRecursive(rootSite, result);
        return result;
    }

    private void CaptureSiteTechnologyRecursive(
        WebsiteTreeNodeViewModel siteNode,
        Dictionary<string, WebsiteTreeDisplayBuilder.TechnologySnapshot> result)
    {
        if (siteNode.NodeType != WebsiteTreeNodeType.Site)
            return;

        result[siteNode.Id] = new WebsiteTreeDisplayBuilder.TechnologySnapshot(
            _technologyStore.GetCmsSummary(siteNode.Host),
            CollectSiteFrameworks(siteNode).ToList());

        foreach (var child in siteNode.Children)
        {
            if (child.NodeType == WebsiteTreeNodeType.Site)
                CaptureSiteTechnologyRecursive(child, result);
        }
    }

    private WebsiteTreeNodeViewModel CloneRegularNode(
        WebsiteTreeNodeViewModel source,
        IReadOnlySet<string> expandedIds)
    {
        var clone = new WebsiteTreeNodeViewModel
        {
            Id = source.Id,
            Title = source.Title,
            NodeType = source.NodeType,
            EntryId = source.EntryId,
            Host = source.Host,
            RequestPath = source.RequestPath,
            ItemIcon = source.ItemIcon,
            IsExpanded = source.IsExpanded || expandedIds.Contains(source.Id)
        };

        foreach (var child in source.Children)
            clone.Children.Add(CloneNodeForDisplay(child, expandedIds));

        return clone;
    }

    private IReadOnlyList<ZeroFall.Fingerprint.Core.Framework> CollectSiteFrameworks(WebsiteTreeNodeViewModel siteNode)
    {
        var merged = new Dictionary<string, ZeroFall.Fingerprint.Core.Framework>(StringComparer.OrdinalIgnoreCase);
        void AddFromHost(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;
            foreach (var fw in _technologyStore.GetFrameworks(host))
            {
                if (fw is null || string.IsNullOrEmpty(fw.Name))
                    continue;
                merged[fw.DisplayText] = fw;
            }
        }

        AddFromHost(siteNode.Host);
        foreach (var child in siteNode.Children)
        {
            if (child.NodeType == WebsiteTreeNodeType.Site)
                AddFromHost(child.Host);
        }

        return merged.Values
            .OrderBy(f => f.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void NormalizeDirtySitePathTrees()
    {
        lock (_memoryTreeGate)
        {
            foreach (var authority in _sitesNeedingNormalize.ToList())
            {
                if (_rootSiteNodes.TryGetValue(authority, out var site))
                    NormalizeSitePathTree(site);
            }
        }

        _sitesNeedingNormalize.Clear();
    }

    private void NormalizeSitePathTrees()
    {
        foreach (var root in _rootSiteNodes.Values)
            NormalizeSitePathTree(root);
    }

    private void NormalizeSitePathTree(WebsiteTreeNodeViewModel siteNode)
    {
        RebalancePathHierarchy(siteNode);
        foreach (var child in siteNode.Children)
        {
            if (child.NodeType == WebsiteTreeNodeType.Site)
                NormalizeSitePathTree(child);
        }
    }

    private WebsiteTreeNodeViewModel EnsureIntermediateAndReparent(
        WebsiteTreeNodeViewModel siteNode,
        WebsiteTreeNodeViewModel treeParent,
        string pathPrefix,
        string segmentTitle,
        List<WebsiteTreeNodeViewModel> deeperNodes)
    {
        var pathKey = BuildPathKey(siteNode.Id, pathPrefix);
        if (!_pathNodes.TryGetValue(pathKey, out var intermediate))
        {
            intermediate = new WebsiteTreeNodeViewModel
            {
                Id = pathKey,
                Title = segmentTitle,
                NodeType = WebsiteTreeNodeType.Path,
                ItemIcon = WebsiteTreeIcons.Folder
            };
            _pathNodes[pathKey] = intermediate;
            if (!treeParent.Children.Contains(intermediate))
                treeParent.Children.Add(intermediate);
        }

        foreach (var deep in deeperNodes)
        {
            if (ReferenceEquals(deep, intermediate))
                continue;

            RemoveFromTreeParent(siteNode, deep);
            RelativizePathTitleUnderParent(deep, pathPrefix);

            if (!intermediate.Children.Contains(deep))
                intermediate.Children.Add(deep);
        }

        return intermediate;
    }

    /// <summary>将同层并列的前缀路径（如 logo 与 logo/1.png）收拢为父子关系。</summary>
    private void NormalizePathSiblings(WebsiteTreeNodeViewModel parent)
    {
        var changed = false;
        var pathChildren = parent.Children.Where(c => c.NodeType == WebsiteTreeNodeType.Path).ToList();
        foreach (var shortNode in pathChildren.OrderBy(n => GetPathPrefixFromId(n.Id).Length))
        {
            var shortPrefix = GetPathPrefixFromId(shortNode.Id);
            if (string.IsNullOrEmpty(shortPrefix))
                continue;

            foreach (var longNode in pathChildren)
            {
                if (ReferenceEquals(shortNode, longNode))
                    continue;

                var longPrefix = GetPathPrefixFromId(longNode.Id);
                if (!longPrefix.StartsWith(shortPrefix + "/", StringComparison.Ordinal))
                    continue;

                if (!parent.Children.Contains(longNode))
                    continue;

                parent.Children.Remove(longNode);
                shortNode.Children.Add(longNode);
                RelativizePathTitleUnderParent(longNode, shortPrefix);
                changed = true;
            }
        }

        foreach (var child in parent.Children.Where(c => c.NodeType == WebsiteTreeNodeType.Path).ToList())
            NormalizePathSiblings(child);

        if (changed)
            CollapseSingleChildPathChains(parent);
    }

    private static void RelativizePathTitleUnderParent(WebsiteTreeNodeViewModel node, string parentPrefix)
    {
        var selfPath = GetPathPrefixFromId(node.Id);
        if (string.IsNullOrEmpty(selfPath) || selfPath.Length <= parentPrefix.Length)
            return;

        var suffix = selfPath[parentPrefix.Length..].TrimStart('/');
        if (string.IsNullOrEmpty(suffix))
            return;

        if (suffix.Contains('/'))
            node.Title = suffix[..suffix.IndexOf('/')];
        else if (string.Equals(node.Title, selfPath, StringComparison.Ordinal)
                 || node.Title.Contains('/', StringComparison.Ordinal))
            node.Title = suffix;
    }

    /// <summary>
    /// 按路径前缀强制重排整棵子树，保证如 logo 与 logo/1.png 不会并列在同层。
    /// </summary>
    private void RebalancePathHierarchy(WebsiteTreeNodeViewModel siteNode)
    {
        var nodes = CollectPathNodes(siteNode);
        if (nodes.Count == 0)
            return;

        var byPrefix = new Dictionary<string, WebsiteTreeNodeViewModel>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var prefix = GetPathPrefixFromId(node.Id);
            if (!string.IsNullOrEmpty(prefix) && !byPrefix.ContainsKey(prefix))
                byPrefix[prefix] = node;
        }

        foreach (var node in nodes.OrderBy(n => GetPathPrefixFromId(n.Id).Split('/').Length))
        {
            var prefix = GetPathPrefixFromId(node.Id);
            if (string.IsNullOrEmpty(prefix))
                continue;

            var expectedParent = FindExpectedParentForPrefix(siteNode, byPrefix, prefix);
            var currentParent = FindParentNode(siteNode, node);
            if (currentParent is null || ReferenceEquals(currentParent, expectedParent))
                continue;

            currentParent.Children.Remove(node);
            if (!expectedParent.Children.Contains(node))
                expectedParent.Children.Add(node);

            if (expectedParent.NodeType == WebsiteTreeNodeType.Path)
                RelativizePathTitleUnderParent(node, GetPathPrefixFromId(expectedParent.Id));
        }
    }

    private static List<WebsiteTreeNodeViewModel> CollectPathNodes(WebsiteTreeNodeViewModel siteNode)
    {
        var result = new List<WebsiteTreeNodeViewModel>();
        var seen = new HashSet<WebsiteTreeNodeViewModel>();
        var stack = new Stack<WebsiteTreeNodeViewModel>();
        stack.Push(siteNode);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var child in current.Children)
            {
                if (child.NodeType == WebsiteTreeNodeType.Path)
                {
                    if (seen.Add(child))
                        result.Add(child);
                    stack.Push(child);
                }
            }
        }

        return result;
    }

    private static WebsiteTreeNodeViewModel FindExpectedParentForPrefix(
        WebsiteTreeNodeViewModel siteNode,
        IReadOnlyDictionary<string, WebsiteTreeNodeViewModel> byPrefix,
        string prefix)
    {
        var parentPrefix = prefix;
        while (true)
        {
            var slash = parentPrefix.LastIndexOf('/');
            if (slash < 0)
                return siteNode;

            parentPrefix = parentPrefix[..slash];
            if (byPrefix.TryGetValue(parentPrefix, out var parent))
                return parent;
        }
    }

    private static WebsiteTreeNodeViewModel? FindParentNode(WebsiteTreeNodeViewModel root, WebsiteTreeNodeViewModel target)
    {
        foreach (var child in root.Children)
        {
            if (ReferenceEquals(child, target))
                return root;

            var found = FindParentNode(child, target);
            if (found is not null)
                return found;
        }

        return null;
    }

    private List<WebsiteTreeNodeViewModel> FindNodesWithPathPrefix(string pathRootId, string pathPrefix)
    {
        var marker = pathRootId + "/path:";
        var list = new List<WebsiteTreeNodeViewModel>();
        var seen = new HashSet<WebsiteTreeNodeViewModel>();
        foreach (var kv in _pathNodes)
        {
            if (!kv.Key.StartsWith(marker, StringComparison.Ordinal))
                continue;

            var existing = GetPathPrefixFromId(kv.Key);
            if (!string.Equals(existing, pathPrefix, StringComparison.Ordinal)
                && !existing.StartsWith(pathPrefix + "/", StringComparison.Ordinal))
                continue;

            if (seen.Add(kv.Value))
                list.Add(kv.Value);
        }

        return list;
    }

    /// <summary>仅取比 pathPrefix 深一层的最近路径节点，避免把 logo/1.png/a 误挂到 logo 下。</summary>
    private List<WebsiteTreeNodeViewModel> FindImmediateDeeperPathNodes(string pathRootId, string pathPrefix)
    {
        var deeper = FindNodesWithPathPrefix(pathRootId, pathPrefix)
            .Where(n => !string.Equals(GetPathPrefixFromId(n.Id), pathPrefix, StringComparison.Ordinal))
            .ToList();
        if (deeper.Count == 0)
            return deeper;

        var minDepth = deeper.Min(n => GetPathPrefixFromId(n.Id).Split('/').Length);
        return deeper.Where(n => GetPathPrefixFromId(n.Id).Split('/').Length == minDepth).ToList();
    }

    /// <summary>若节点仅有一个 Path 子节点，则递归合并为 a/b/c 单节点展示。</summary>
    private void CollapseSingleChildPathChains(WebsiteTreeNodeViewModel parent)
    {
        foreach (var child in parent.Children.Where(c => c.NodeType == WebsiteTreeNodeType.Path).ToList())
            CollapseSingleChildPathChains(child);

        while (parent.Children.Count == 1 && parent.Children[0].NodeType == WebsiteTreeNodeType.Path)
        {
            var head = parent.Children[0];
            var chain = new List<WebsiteTreeNodeViewModel> { head };
            var tail = head;
            while (tail.Children.Count == 1 && tail.Children[0].NodeType == WebsiteTreeNodeType.Path)
            {
                tail = tail.Children[0];
                chain.Add(tail);
            }

            if (chain.Count == 1)
                break;

            var pathRootId = GetPathRootIdFromPathNode(head);
            var mergedPath = string.Join("/", chain.Select(GetPathSegmentLabel));
            var mergedKey = BuildPathKey(pathRootId, mergedPath);

            var oldHeadKey = head.Id;
            head.Title = mergedPath;
            head.Id = mergedKey;

            var leafChildren = tail.Children.ToList();
            head.Children.Clear();
            foreach (var c in leafChildren)
                head.Children.Add(c);

            for (var i = 1; i < chain.Count; i++)
            {
                chain[i - 1].Children.Remove(chain[i]);
                _pathNodes.Remove(chain[i].Id);
            }

            _pathNodes.Remove(oldHeadKey);
            _pathNodes[mergedKey] = head;
        }
    }

    /// <summary>已合并的 a/b/c 与新路径 a/b/d 分叉时，拆出公共父节点 a/b。</summary>
    private void ExpandMergedPathForBranch(
        WebsiteTreeNodeViewModel siteNode,
        WebsiteTreeNodeViewModel mergedNode,
        string newPathPrefix)
    {
        var mergedPath = GetPathPrefixFromId(mergedNode.Id);
        var common = GetCommonPathPrefix(mergedPath, newPathPrefix);
        if (string.IsNullOrEmpty(common) || string.Equals(common, mergedPath, StringComparison.Ordinal))
            return;

        RemoveFromTreeParent(siteNode, mergedNode);
        _pathNodes.Remove(mergedNode.Id);

        var branchNode = EnsurePathParent(siteNode, siteNode, common.Split('/', StringSplitOptions.RemoveEmptyEntries));

        var oldSuffix = mergedPath[common.Length..].TrimStart('/');
        var branchSegment = oldSuffix.Contains('/') ? oldSuffix[..oldSuffix.IndexOf('/')] : oldSuffix;

        mergedNode.Title = branchSegment;
        mergedNode.Id = BuildPathKey(siteNode.Id, mergedPath);
        _pathNodes[mergedNode.Id] = mergedNode;
        branchNode.Children.Add(mergedNode);
    }

    private bool RemoveRequestNodeByEntryId(string entryId, bool refreshDisplay = true)
    {
        lock (_memoryTreeGate)
        {
            foreach (var kv in _rootSiteNodes)
            {
                if (!TryRemoveRequestByEntryId(kv.Value, entryId))
                    continue;

                if (refreshDisplay)
                    ScheduleRefreshDisplayTree(dirtySites: [kv.Key]);
                return true;
            }
        }

        return false;
    }

    private static bool TryRemoveRequestByEntryId(WebsiteTreeNodeViewModel node, string entryId)
    {
        for (var i = node.Children.Count - 1; i >= 0; i--)
        {
            var child = node.Children[i];
            if (child.NodeType == WebsiteTreeNodeType.Request
                && string.Equals(child.EntryId, entryId, StringComparison.Ordinal))
            {
                node.Children.RemoveAt(i);
                return true;
            }

            if (TryRemoveRequestByEntryId(child, entryId))
                return true;
        }

        return false;
    }

    private void RemoveFromTreeParent(WebsiteTreeNodeViewModel root, WebsiteTreeNodeViewModel node)
    {
        _ = TryRemoveFromTreeParent(root, node);
    }

    private static bool TryRemoveFromTreeParent(WebsiteTreeNodeViewModel root, WebsiteTreeNodeViewModel node)
    {
        if (root.Children.Remove(node))
            return true;

        foreach (var child in root.Children.ToList())
        {
            if (TryRemoveFromTreeParent(child, node))
                return true;
        }

        return false;
    }

    private WebsiteTreeNodeViewModel? FindBranchConflict(string pathRootId, string pathPrefix)
    {
        var marker = pathRootId + "/path:";
        foreach (var kv in _pathNodes)
        {
            if (!kv.Key.StartsWith(marker, StringComparison.Ordinal))
                continue;

            var existing = GetPathPrefixFromId(kv.Key);
            if (string.IsNullOrEmpty(existing)
                || string.Equals(existing, pathPrefix, StringComparison.Ordinal)
                || pathPrefix.StartsWith(existing + "/", StringComparison.Ordinal)
                || existing.StartsWith(pathPrefix + "/", StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrEmpty(GetCommonPathPrefix(existing, pathPrefix)))
                return kv.Value;
        }

        return null;
    }

    private static string BuildPathKey(string pathRootId, string pathPrefix) =>
        $"{pathRootId}/path:{pathPrefix}";

    private static string GetPathPrefixFromId(string id)
    {
        const string marker = "/path:";
        var idx = id.IndexOf(marker, StringComparison.Ordinal);
        return idx < 0 ? string.Empty : id[(idx + marker.Length)..];
    }

    private static string GetPathRootIdFromPathNode(WebsiteTreeNodeViewModel pathNode)
    {
        const string marker = "/path:";
        var idx = pathNode.Id.IndexOf(marker, StringComparison.Ordinal);
        return idx < 0 ? pathNode.Id : pathNode.Id[..idx];
    }

    private static string GetPathSegmentLabel(WebsiteTreeNodeViewModel node)
    {
        var prefix = GetPathPrefixFromId(node.Id);
        if (string.IsNullOrEmpty(prefix))
            return node.Title;

        var slash = prefix.LastIndexOf('/');
        return slash < 0 ? prefix : prefix[(slash + 1)..];
    }

    private static string GetCommonPathPrefix(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return string.Empty;

        var aParts = a.Split('/');
        var bParts = b.Split('/');
        var len = Math.Min(aParts.Length, bParts.Length);
        var i = 0;
        while (i < len && string.Equals(aParts[i], bParts[i], StringComparison.Ordinal))
            i++;

        return i == 0 ? string.Empty : string.Join('/', aParts.Take(i));
    }

    private static bool ShouldSkipPathSegments(Uri uri) =>
        uri.Scheme is "data" or "blob";

    /// <summary>请求叶节点：目录已在父路径节点展示，此处仅显示方法 + 资源名（状态由图标表达）。</summary>
    private static string BuildRequestDisplayTitle(string method, Uri uri, string rawUrl)
    {
        if (ShouldSkipPathSegments(uri))
            return $"{method} {FormatOpaqueUrlLabel(uri, rawUrl)}";

        const int maxShown = 120;
        const int maxInlineQueryLen = 48;

        var leaf = GetRequestLeafLabel(uri);
        var query = uri.Query ?? string.Empty;

        string uriPart;
        if (query.Length == 0)
            uriPart = TruncateTail(leaf, maxShown);
        else if (query.Length <= maxInlineQueryLen && leaf.Length + query.Length <= maxShown)
            uriPart = leaf + query;
        else if (query.Length > maxInlineQueryLen)
            uriPart = $"{leaf}?…(+{query.Length})";
        else
            uriPart = TruncateTail(leaf + query, maxShown);

        return $"{method} {uriPart}";
    }

    private static string GetRequestLeafLabel(Uri uri)
    {
        var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        if (path.Length > 1 && path.EndsWith('/'))
            path = path.TrimEnd('/');

        var fileName = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(fileName))
            return fileName;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[^1] : "/";
    }

    private static string FormatOpaqueUrlLabel(Uri uri, string rawUrl)
    {
        if (uri.Scheme == "data")
        {
            const string prefix = "data:";
            var s = string.IsNullOrEmpty(rawUrl) ? uri.OriginalString : rawUrl;
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = s[prefix.Length..];
                var comma = rest.IndexOf(',');
                var meta = comma >= 0 ? rest[..comma] : rest;
                if (meta.Length > 64)
                    meta = meta[..64] + "…";
                if (comma >= 0 && comma + 1 < rest.Length)
                    return $"data:{meta} (+{rest.Length - comma - 1})";
                return $"data:{meta}";
            }
        }

        var text = string.IsNullOrEmpty(rawUrl) ? uri.OriginalString : rawUrl;
        return TruncateTail(text, 96);
    }

    private static string TruncateTail(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
            return text;
        if (maxLen <= 5)
            return "…";
        return text[..(maxLen - 1)] + "…";
    }

    [RelayCommand(CanExecute = nameof(CanOperateSiteTechnologies))]
    private async Task ProbeSiteTechnologiesAsync(WebsiteTreeNodeViewModel? node)
    {
        if (node?.NodeType != WebsiteTreeNodeType.Site || string.IsNullOrWhiteSpace(node.Host))
            return;

        var baseUrl = ResolveSiteBaseUrl(node.Host);
        await _fingerprintService.RunActiveProbeAsync(node.Host, baseUrl).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            var rootAuthority = ResolveRootAuthorityFromNodeId(node.Id);
            if (!string.IsNullOrEmpty(rootAuthority))
                ScheduleRefreshDisplayTree(dirtySites: [rootAuthority]);
            else
                ScheduleRefreshDisplayTree();
        });
    }

    private static string? ResolveRootAuthorityFromNodeId(string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId))
            return null;

        if (nodeId.StartsWith("site:", StringComparison.Ordinal))
            return nodeId["site:".Length..];

        if (!nodeId.StartsWith("assoc:", StringComparison.Ordinal))
            return null;

        var payload = nodeId["assoc:".Length..];
        var sep = payload.IndexOf('|');
        return sep > 0 ? payload[..sep] : null;
    }

    [RelayCommand(CanExecute = nameof(CanOperateSiteTechnologies))]
    private void ToggleActiveProbeAssist(WebsiteTreeNodeViewModel? node)
    {
        if (node?.NodeType != WebsiteTreeNodeType.Site || string.IsNullOrWhiteSpace(node.Host))
            return;

        var enabled = !_technologyStore.IsActiveProbeEnabled(node.Host);
        _technologyStore.SetActiveProbeEnabled(node.Host, enabled);
    }

    private bool CanOperateSiteTechnologies(WebsiteTreeNodeViewModel? node) =>
        node?.NodeType == WebsiteTreeNodeType.Site && !string.IsNullOrWhiteSpace(node.Host);

    public bool IsActiveProbeEnabledForNode(WebsiteTreeNodeViewModel? node) =>
        node?.NodeType == WebsiteTreeNodeType.Site
        && !string.IsNullOrWhiteSpace(node.Host)
        && _technologyStore.IsActiveProbeEnabled(node.Host);

    private static string ResolveSiteBaseUrl(string host) =>
        host.Contains(':') ? $"http://{host}/" : $"https://{host}/";

    [RelayCommand]
    private void Clear()
    {
        lock (_pendingTrafficGate)
        {
            _pendingTrafficEvents.Clear();
            _orphanTrafficEvents.Clear();
            _trafficFlushScheduled = false;
        }

        ClearSiteMap();
        _technologyStore.Clear();
        _dirtySiteAuthorities.Clear();
        _sitesNeedingNormalize.Clear();
        _displayRefreshDeferred = false;
        RefreshDisplayTreeFull();
    }

    private void ClearSiteMap()
    {
        _rootSiteNodes.Clear();
        _assocSiteNodes.Clear();
        _pathNodes.Clear();
        _rootContext.Clear();
        SelectedNode = null;
    }

    /// <summary>
    /// 导出指定网站的树结构 JSON。site 可匹配根站点或关联子站点；未传则取第一个根站点。
    /// </summary>
    public string BuildSiteTreeJson(string? rootAuthority)
    {
        WebsiteTreeNodeViewModel? root;
        if (string.IsNullOrWhiteSpace(rootAuthority))
        {
            root = _rootSiteNodes.Values.FirstOrDefault();
        }
        else
        {
            root = TryResolveSiteNode(rootAuthority);
            if (root is null)
                return BuildSiteNotFoundJson(rootAuthority);
        }

        if (root is null)
            return "{\"error\":\"无网站树数据\"}";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            WriteSiteTreeJson(writer, root);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>按 host（或 host:port）匹配根站点或关联子站点节点。</summary>
    private WebsiteTreeNodeViewModel? TryResolveSiteNode(string authority)
    {
        var normalized = NormalizeSiteAuthority(authority);
        foreach (var kv in _rootSiteNodes)
        {
            if (string.Equals(NormalizeSiteAuthority(kv.Key), normalized, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        foreach (var kv in _assocSiteNodes)
        {
            var sep = kv.Key.IndexOf('|');
            if (sep < 0 || sep >= kv.Key.Length - 1)
                continue;

            var assocAuthority = kv.Key[(sep + 1)..];
            if (string.Equals(NormalizeSiteAuthority(assocAuthority), normalized, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return null;
    }

    private string BuildSiteNotFoundJson(string authority)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("error", $"未找到站点 {authority.Trim()}");
            writer.WritePropertyName("availableSites");
            writer.WriteStartArray();
            foreach (var site in ListAvailableSiteAuthorities())
                writer.WriteStringValue(site);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private IEnumerable<string> ListAvailableSiteAuthorities()
    {
        var sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _rootSiteNodes.Keys)
            sites.Add(key);
        foreach (var node in _assocSiteNodes.Values)
        {
            if (!string.IsNullOrWhiteSpace(node.Host))
                sites.Add(node.Host);
        }

        return sites.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeSiteAuthority(string authority)
    {
        var value = authority.Trim().ToLowerInvariant();
        if (value.EndsWith(":80", StringComparison.Ordinal))
            return value[..^3];
        if (value.EndsWith(":443", StringComparison.Ordinal))
            return value[..^4];
        return value;
    }

    private static void WriteSiteTreeJson(Utf8JsonWriter writer, WebsiteTreeNodeViewModel siteNode) =>
        WebsiteTreeSiteLayout.WriteSiteTreeJson(writer, siteNode);

    public void Dispose()
    {
        _displayTreeRefreshTimer.Stop();
        _displayTreeRefreshTimer.Tick -= OnDisplayTreeRefreshTimerTick;
        _technologyStore.TechnologiesChanged -= OnTechnologiesChanged;
        _targetScope.Changed -= OnTargetScopeChanged;
        _trafficSub.Dispose();
        _clearedSub.Dispose();
        _projectOpenedSubscription.Dispose();
        _documentNavigatedSub.Dispose();
        _dockTabSelectedSub.Dispose();
        _panelVisibilitySub.Dispose();
    }
}
