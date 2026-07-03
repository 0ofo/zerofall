using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZeroFall.Browser.ViewModels;

public enum WebsiteTreeNodeType
{
    TargetScope,
    ScopeHost,
    Tab,
    Site,
    Path,
    Request,
    Technology
}

public partial class WebsiteTreeNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private WebsiteTreeNodeType _nodeType;

    [ObservableProperty]
    private string _entryId = string.Empty;

    [ObservableProperty]
    private string _host = string.Empty;

    /// <summary>请求节点专用：方法+路径（不含 query/fragment），用于文件夹聚合分组。</summary>
    [ObservableProperty]
    private string _requestPath = string.Empty;

    /// <summary>路径/请求等节点的 Semi 图标；Tab/Site 可为 null。</summary>
    [ObservableProperty]
    private StreamGeometry? _itemIcon;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private ObservableCollection<WebsiteTreeNodeViewModel> _children = new();
}
