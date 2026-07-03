using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Data;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Events;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.DataTable.Views;
using ZeroFall.Dock.Services;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Providers;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.Dock.ViewModels;

public partial class ContentPanelViewModel : DockPanelViewModel
{
    public const string WorkspaceGuideTabId = "workspace-guide";

    private readonly IEventBus _eventBus;
    private readonly ContentCreationService _contentCreation;

    public IRelayCommand OpenNewBrowserTabCommand { get; }

    public ContentPanelViewModel(IEventBus eventBus, ContentCreationService contentCreation) : base(DockPosition.Content, eventBus)
    {
        _eventBus = eventBus;
        _contentCreation = contentCreation;
        OpenNewBrowserTabCommand = new RelayCommand(() =>
            _eventBus.Publish(new OpenBrowserTabRequestedEvent(string.Empty, "新标签页")));

        SubscribeEvent(eventBus, (TreeNodeSelectedEvent e) => OnTreeNodeSelected(e));
        SubscribeEvent(eventBus, (OpenWorkspaceFileInEditorEvent e) => OnOpenWorkspaceFileInEditor(e));
        SubscribeEvent(eventBus, (RemoveDataSourceRequestedEvent e) => OnRemoveDataSource(e));
        SubscribeEvent(eventBus, (SqlTableBrowseEvent e) => OnSqlTableBrowse(e));
        SubscribeEvent(eventBus, (NewQueryRequestedEvent e) => OnNewQueryRequested(e));
        SubscribeEvent(eventBus, (SqlQueryResultEvent e) => _ = OpenSqlQueryResultTabAsync(e));
        SubscribeEvent(eventBus, (DataResultEvent e) => OnDataResult(e));
        SubscribeEvent(eventBus, (TabClosedEvent e) => OnTabClosed(e));
        SubscribeEvent(eventBus, (BrowserContentTabTitleChangedEvent e) => OnBrowserContentTabTitleChanged(e));
        SubscribeEvent(eventBus, (BrowserContentTabFaviconChangedEvent e) => OnBrowserContentTabFaviconChanged(e));
        SubscribeEvent(eventBus, (ProjectOpenedEvent e) => OnProjectOpened(e));
    }

    private void OnProjectOpened(ProjectOpenedEvent e)
    {
        Dispatcher.UIThread.Post(EnsureWorkspaceGuideTab);
    }

    private void EnsureWorkspaceGuideTab()
    {
        if (Tabs.Any(t => t.Id != WorkspaceGuideTabId))
            return;

        var guide = Tabs.FirstOrDefault(t => t.Id == WorkspaceGuideTabId);
        if (guide is not null)
        {
            SelectedTab = guide;
            return;
        }

        _eventBus.Publish(new SwitchDockTabRequestedEvent(DockPosition.Content, WorkspaceGuideTabId));
    }

    private void OnBrowserContentTabFaviconChanged(BrowserContentTabFaviconChangedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var tab = Tabs.FirstOrDefault(t => t.Id == e.TabId);
            if (tab is null)
                return;

            if (e.ImageBytes is not { Length: > 0 })
            {
                tab.FaviconImage = null;
                return;
            }

            try
            {
                using var ms = new MemoryStream(e.ImageBytes);
                tab.FaviconImage = new Bitmap(ms);
            }
            catch
            {
                tab.FaviconImage = null;
            }
        });
    }

    private void OnBrowserContentTabTitleChanged(BrowserContentTabTitleChangedEvent e)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == e.TabId);
        if (tab == null) return;
        var title = e.Title.Trim();
        if (title.Length > 72)
            title = title[..69] + "…";
        tab.Title = title;
    }

    private void OnTreeNodeSelected(TreeNodeSelectedEvent e)
    {
        if (e.Node.NodeType == TreeNodeType.Table)
        {
            _eventBus.Publish(new SqlTableBrowseEvent(e.Node.FilePath, e.Node.SqlTableName, e.Node.DataSourceType));
            return;
        }

        if (e.Node.NodeType == TreeNodeType.DataSource &&
            (e.Node.DataSourceType == DataSourceType.Sqlite || e.Node.DataSourceType == DataSourceType.MySql))
            return;

        if (e.Node.NodeType == TreeNodeType.Database) return;

        if (e.Node.NodeType == TreeNodeType.Folder) return;

        var existingTab = Tabs.FirstOrDefault(t => t.Id == e.Node.Id);
        if (existingTab != null)
        {
            SelectedTab = existingTab;
            NotifyContentHostTabActivated(existingTab.Content);
            return;
        }

        var content = _contentCreation.CreateContentForDataSource(e);

        var newTab = new DockTabItemViewModel
        {
            Id = e.Node.Id,
            Title = e.Node.Name,
            Icon = e.Node.NodeType == TreeNodeType.File
                ? IconHelper.GetIcon("SemiIconFile")
                : IconHelper.GetIconForDataSourceType(e.Node.DataSourceType),
            Content = content
        };

        Tabs.Add(newTab);
        SelectedTab = newTab;
        NotifyContentHostTabActivated(newTab.Content);
        _eventBus.Publish(new StatusMessageEvent($"已打开: {e.Node.Name}"));
    }

    private void OnOpenWorkspaceFileInEditor(OpenWorkspaceFileInEditorEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.FilePath))
            return;

        if (!File.Exists(e.FilePath))
        {
            _eventBus.Publish(new StatusMessageEvent($"文件不存在: {e.FilePath}"));
            return;
        }

        void Open()
        {
            var filePath = Path.GetFullPath(e.FilePath);
            var tabId = ContentCreationService.BuildFileTabId(filePath);
            var existingTab = Tabs.FirstOrDefault(t => t.Id == tabId);
            if (existingTab != null)
            {
                SelectedTab = existingTab;
                NotifyContentHostTabActivated(existingTab.Content);
                return;
            }

            var fileName = Path.GetFileName(filePath);
            var content = _contentCreation.CreateWorkspaceFilePreview(filePath, fileName);
            if (content == null)
                return;

            var newTab = new DockTabItemViewModel
            {
                Id = tabId,
                Title = fileName,
                Icon = IconHelper.GetIcon("SemiIconFile"),
                Content = content
            };

            Tabs.Add(newTab);
            SelectedTab = newTab;
            NotifyContentHostTabActivated(newTab.Content);
            _eventBus.Publish(new StatusMessageEvent($"已打开: {fileName}"));
        }

        if (Dispatcher.UIThread.CheckAccess())
            Open();
        else
            Dispatcher.UIThread.Post(Open);
    }

    private static void NotifyContentHostTabActivated(object? content)
    {
        if (content is ITableGridHost host)
            host.NotifyTabActivated();
    }

    private void OnSqlTableBrowse(SqlTableBrowseEvent e)
    {
        var tabId = $"{e.DataSourceType}-table:{e.FilePath}:{e.TableName}";

        var existingTab = Tabs.FirstOrDefault(t => t.Id == tabId);
        if (existingTab != null)
        {
            SelectedTab = existingTab;
            return;
        }

        var newTab = new DockTabItemViewModel
        {
            Id = tabId,
            Title = e.TableName,
            Icon = IconHelper.GetIcon("SemiIconGridView"),
            Content = new TextBlock { Text = "加载中..." }
        };

        Tabs.Add(newTab);
        SelectedTab = newTab;

        _ = LoadTableDataAsync(newTab, e.FilePath, e.TableName, e.DataSourceType);
        _eventBus.Publish(new StatusMessageEvent($"浏览表: {e.TableName}"));
    }

    private async Task LoadTableDataAsync(DockTabItemViewModel tab, string filePath, string tableName, DataSourceType dataSourceType)
    {
        try
        {
            var result = await _contentCreation.LoadTableDataAsync(filePath, tableName, dataSourceType);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                tab.Content = result.IsSuccess
                    ? result.Content
                    : new TextBlock { Text = result.Error };
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                tab.Content = new TextBlock { Text = $"加载失败: {ex.Message}" };
            });
        }
    }

    private void OnNewQueryRequested(NewQueryRequestedEvent e)
    {
        var queryId = $"query:{e.FilePath}:{Guid.NewGuid():N}";

        var content = _contentCreation.CreateSqlEditorContent(queryId, e.FilePath, e.DataSourceName);

        var newTab = new DockTabItemViewModel
        {
            Id = queryId,
            Title = $"查询 - {e.DataSourceName}",
            Icon = IconHelper.GetIcon("SemiIconCode"),
            Content = content
        };

        Tabs.Add(newTab);
        SelectedTab = newTab;
        _eventBus.Publish(new StatusMessageEvent($"新建查询: {e.DataSourceName}"));
    }

    private async Task OpenSqlQueryResultTabAsync(SqlQueryResultEvent e)
    {
        var tabId = $"sql-result:{e.FilePath}:{Guid.NewGuid():N}";
        var title = e.Title;
        if (title.Length > 48)
            title = title[..48] + "…";

        var provider = MemoryPagedDataProvider.FromStringRows(title, e.Columns, e.Rows);
        var dtvm = new DataTableViewModel { UserPageSize = 200, ShowHeaderPanel = false };
        await dtvm.InitializePagedAsync(provider);

        var content = _contentCreation.CreateDataTableContent(e.FilePath, dtvm);

        var newTab = new DockTabItemViewModel
        {
            Id = tabId,
            Title = dtvm.Title,
            Icon = IconHelper.GetIcon("SemiIconGridView"),
            Content = content
        };

        Tabs.Add(newTab);
        SelectedTab = newTab;
        _eventBus.Publish(new StatusMessageEvent($"SQL结果: {e.Rows.Count} 行"));
    }

    private async void OnDataResult(DataResultEvent e)
    {
        var tabId = $"{e.TabIdPrefix}:{Guid.NewGuid():N}";
        var title = e.Provider.Title;

        var newTab = new DockTabItemViewModel
        {
            Id = tabId,
            Title = title,
            Icon = IconHelper.GetIcon("SemiIconGridView"),
            Content = new TextBlock { Text = "加载中..." }
        };

        Tabs.Add(newTab);
        SelectedTab = newTab;

        try
        {
            var result = await _contentCreation.CreateFromProviderAsync(e.Provider);

            if (!result.IsSuccess)
            {
                newTab.Content = new TextBlock { Text = result.Error };
                return;
            }

            newTab.Content = result.Content;

            var rowCount = result.DataTable?.TotalRows ?? 0;
            _eventBus.Publish(new StatusMessageEvent($"{e.SourceName ?? "数据"}结果: {rowCount:N0} 行"));
        }
        catch (Exception ex)
        {
            newTab.Content = new TextBlock { Text = $"加载失败: {ex.Message}" };
        }
    }

    private void OnRemoveDataSource(RemoveDataSourceRequestedEvent e)
    {
        var tabsToRemove = Tabs.Where(t => t.Id == e.Node.Id).ToList();
        foreach (var tab in tabsToRemove)
        {
            TabContentLifetime.Release(tab.Content);
            tab.Content = null;
            Tabs.Remove(tab);
        }

        if (SelectedTab != null && !Tabs.Contains(SelectedTab))
            SelectedTab = Tabs.Count > 0 ? Tabs[0] : null;

        _eventBus.Publish(new StatusMessageEvent($"已移除: {e.Node.Name}"));
    }

    private void OnTabClosed(TabClosedEvent e)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == e.Tab.Id);
        if (tab != null)
        {
            TabContentLifetime.Release(tab.Content);
            tab.Content = null;
            Tabs.Remove(tab);
            if (SelectedTab == tab)
                SelectedTab = Tabs.Count > 0 ? Tabs[0] : null;
        }
        _eventBus.Publish(new StatusMessageEvent($"已关闭: {e.Tab.Title}"));
    }
}
