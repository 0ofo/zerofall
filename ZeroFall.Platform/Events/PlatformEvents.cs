using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Base.Data;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.Platform.Events;

public record OpenFolderRequestedEvent;
public record OpenWorkspaceFileRequestedEvent(string? FilePath = null);
public record NewTerminalSessionRequestedEvent;
public record ExitRequestedEvent;
public record ProjectOpenedEvent(string DirectoryPath, string DatabasePath);
public record StatusMessageEvent(string Message);
public record PanelVisibilityChangedEvent(Registries.DockPosition Position, bool IsVisible);
public record DataSourceChangedEvent;
public record IndexFileRequestedEvent(string FilePath, string FileName);
public record TreeNodeSelectedEvent(TreeNodeViewModel Node);

/// <summary>在 Content 区用文本编辑器打开工作区文件（如 AI look 工具识别为文本时）。</summary>
public record OpenWorkspaceFileInEditorEvent(string FilePath);
public record TabClosedEvent(Registries.DockTabItemViewModel Tab);
public record SqlTableBrowseEvent(string FilePath, string TableName, DataSourceType DataSourceType = DataSourceType.Sqlite);
public record NewQueryRequestedEvent(string FilePath, string DataSourceName);
public record SqlQueryResultEvent(string Title, string FilePath, string Sql, List<string> Columns, List<string[]> Rows, long TotalRows, bool IsQuery);
public record DataResultEvent(IDataProvider Provider, string TabIdPrefix, string? SourceName = null);
public record AiPanelVisibilityChangedEvent(bool IsVisible);
public record TerminalVisibilityChangedEvent(bool IsVisible);
public record TerminalCommandRequestedEvent(string Command, string? SessionId = null);
public record TerminalOutputEvent(string Output);
public record SidebarVisibilityChangedEvent(bool IsVisible);
public record ThemeChangedEvent(string Theme);
public record SettingsRequestedEvent(string? TargetTabTitle = null);

/// <summary>应用设置已成功写入磁盘（含 AI / MCP 等），监听方应重新 <see cref="Services.ISettingsService.Load"/> 或刷新依赖配置的子系统。</summary>
public record AppSettingsSavedEvent;
public record RemoveDataSourceRequestedEvent(TreeNodeViewModel Node);
public record AddContentTabEvent(Registries.DockTabItemViewModel Tab);

/// <summary>在指定 Dock 区域追加或激活同 Id 标签（已存在则替换内容并选中）。</summary>
public record AddDockTabEvent(Registries.DockPosition Region, Registries.DockTabItemViewModel Tab);
public record OpenBrowserTabRequestedEvent(string Url, string? Title = null, string? TabId = null);
public record WebTrafficRecordedEvent(
    string EntryId,
    string Time, string Tab, string BrowserTabId, int PageSessionId, string TopLevelUrl,
    string Method, string Url, string Status,
    string RequestHeaders, string RequestBody, string ResponseHeaders, string ResponseBody,
    long? LatencyMs = null,
    byte[]? RequestBodyRaw = null,
    byte[]? ResponseBodyRaw = null,
    Models.WebTrafficResourceContext ResourceContext = Models.WebTrafficResourceContext.Unknown,
    /// <summary>筛选桶，对齐 <c>TrafficMimeCategory</c> 整型值；-1 表示未计算。</summary>
    int MimeFilterCategory = -1,
    string MimePrimaryClass = "",
    string MimeType = "",
    string SessionDocumentHost = "",
    bool HasQuery = false,
    bool FingerprintEligible = false,
    int ResponseBodyLength = 0,
    int? StatusCode = null);
public record WebTrafficBodyUpdatedEvent(
    string EntryId,
    string RequestBody,
    string ResponseBody,
    byte[]? RequestBodyRaw = null,
    byte[]? ResponseBodyRaw = null);

/// <summary>流量监控表与 SQLite 归档已全部清空。</summary>
public record TrafficRecordsClearedEvent;

public record BrowserContentTabTitleChangedEvent(string TabId, string Title);

/// <summary>浏览器标签完成文档导航（地址栏 TopLevelUrl / PageSessionId 已更新）。</summary>
public record BrowserTabDocumentNavigatedEvent(string TabId, int PageSessionId, string TopLevelUrl);

/// <summary>浏览器 Content Tab 站点图标更新（含 JS 动态换标）。<c>ImageBytes</c> 为 null 时恢复默认矢量图标。</summary>
public record BrowserContentTabFaviconChangedEvent(string TabId, byte[]? ImageBytes);

/// <summary>统一代理配置已变更，由 <see cref="Services.ProxyRuntimeCoordinator"/> 在后台应用。</summary>
public record ProxySettingsChangedEvent(Models.ProxySettings Settings);

/// <summary>代理运行态已应用且 WebView2 选项已更新，浏览器可安全重建 WebView。</summary>
public record ProxyRuntimeStateChangedEvent(ProxyRuntimeState State);
public record SwitchDockTabRequestedEvent(Registries.DockPosition Position, string TabId);
public record ActiveContentTabChangedEvent(string TabId, string Title);

/// <summary>右侧 AI 聊天 WebView（NavigateToString + bridge）已就绪，可安全挂载 Content 区浏览器 WebView2。</summary>
public record AiChatWebViewReadyEvent;
public record UiSelectionChangedEvent(string SelectionType, string Summary, string PayloadJson);
public record HttpReplayRequestedEvent(
    string SourceEntryId,
    string Method,
    string Url,
    string RequestHeaders,
    string RequestBody,
    string ResponseHeaders,
    string ResponseBody);

public record HttpIntruderRequestedEvent(
    string SourceEntryId,
    string Method,
    string Url,
    string RequestHeaders,
    string RequestBody,
    string ResponseHeaders,
    string ResponseBody);

public record HttpDecodeRequestedEvent(string Label, string InputText);

public record HttpDiffRequestedEvent(
    string LeftLabel,
    string LeftText,
    string RightLabel,
    string RightText);

public record SelectTrafficEntryRequestedEvent(string EntryId);

/// <summary>将端口扫描目标设为指定主机/域名并切换到「端口扫描」Tab（由流量表右键等触发）。</summary>
public record PortScanTargetHostRequestedEvent(string Host);

/// <summary>在底部打开（或聚焦）某次资产测绘任务在库中的结果表。</summary>
public record AssetReconHistoryResultsOpenRequestedEvent(string DatabasePath, string QueryTaskId, string QueryText);

public record CloseContentTabRequestedEvent(string TabId);

public partial class TreeNodeViewModel : Base.Mvvm.ViewModelBase
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private TreeNodeType _nodeType = TreeNodeType.DataSource;

    [ObservableProperty]
    private DataSourceType _dataSourceType;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private TreeNodeViewModel? _parent;

    [ObservableProperty]
    private ObservableCollection<TreeNodeViewModel> _children = new();

    /// <summary>SQL 查询用表标识（如 schema.table）；为空时使用 <see cref="Name"/>。</summary>
    [ObservableProperty]
    private string _tableReference = string.Empty;

    public string SqlTableName =>
        string.IsNullOrEmpty(TableReference) ? Name : TableReference;

    public int RecordId => int.TryParse(Id, out var rid) ? rid : 0;
}
