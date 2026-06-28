using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Datafinder.Platform.Models;

namespace Datafinder.Platform.Registries;

public enum DockPosition
{
    Left,
    Right,
    Bottom,
    Content,
    Setting
}

public class DockTabRegistration
{
    public required DockPosition Region { get; init; }
    public required string TabId { get; init; }
    public required string Title { get; init; }
    public required Func<DockTabItemViewModel> CreateTab { get; init; }
    public string? IconKey { get; init; }
    public bool IsClosable { get; init; } = true;
    public bool IsDefaultVisible { get; init; } = true;
}

public class SettingsPageEntry
{
    public required string Title { get; init; }
    public required string IconKey { get; init; }
    public required int Order { get; init; }
    public required Func<Control> CreateView { get; init; }
}

public class MenuItemEntry
{
    public required string Header { get; init; }
    public required string MenuPath { get; init; }
    public int MenuGroupOrder { get; init; }
    public int Order { get; init; }
    public ICommand? Command { get; init; }
    public object? CommandParameter { get; init; }
    public string? CommandId { get; init; }
    public string? IconKey { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsCheckable { get; init; }
}

public partial class DockTabItemViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _id = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _title = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private StreamGeometry? _icon;

    /// <summary>站点 favicon 位图；有值时 Tab 头优先显示此图，否则回退 <see cref="Icon"/>。</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private IImage? _faviconImage;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private object? _content;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isClosable = true;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private Dictionary<DockPosition, string>? _linkedTabIds;
}

/// <summary>
/// Tab 内容控件可实现此接口，供 Dock 宿主在该 Tab 激活时索取标签栏右侧工具面板。
/// 内容页只负责提供控件；挂载与卸载由 Dock 宿主统一处理，避免同一控件双父级。
/// </summary>
public interface IDockTabToolPanelProvider
{
    Control? GetDockTabToolPanel();
}

public interface IDockLayoutRegistry
{
    void RegisterTab(DockTabRegistration registration);
    IReadOnlyList<DockTabRegistration> GetRegistrations();
    IReadOnlyList<DockTabRegistration> GetRegistrationsForRegion(DockPosition region);
}

public interface IContentFactoryRegistry
{
    void Register(IContentFactory factory);
    void Register(string contentType, IContentFactory factory);
    bool TryCreateContent(string contentType, ContentFactoryContext context, out object? content);
    bool HasFactory(string contentType);
}

public interface ISettingsRegistry
{
    void Register(SettingsPageEntry entry);
    IReadOnlyList<SettingsPageEntry> GetPages();
}

public interface IMenuRegistry
{
    void Register(MenuItemEntry entry);
    IReadOnlyList<MenuItemEntry> GetItems();
    IReadOnlyList<MenuItemEntry> GetItemsForMenu(string menuPath);
}

public interface IContentFactory
{
    string ContentType { get; }
    object? CreateContent(ContentFactoryContext context);
}

public class ContentFactoryContext
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public string? TableName { get; init; }
    public string? DataSourceName { get; init; }
    public DataSourceType? DataSourceType { get; init; }
    public TreeNodeType? NodeType { get; init; }
    public Dictionary<string, object> Extra { get; init; } = new();
}
