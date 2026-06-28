using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Platform.Services;

public enum UiLayoutScope
{
    All,
    Active,
    Menu,
    Sidebar,
    Content,
    Bottom,
    Right,
    Tab
}

/// <summary>解析 get_ui_layout 的 scope：保留字为区域过滤，其余视为 Dock Tab Id。</summary>
public readonly record struct UiLayoutQuery(UiLayoutScope Scope, string? TabId = null)
{
    public static UiLayoutQuery Parse(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return new UiLayoutQuery(UiLayoutScope.All);

        var trimmed = scope.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "all" => new UiLayoutQuery(UiLayoutScope.All),
            "active" => new UiLayoutQuery(UiLayoutScope.Active),
            "menu" => new UiLayoutQuery(UiLayoutScope.Menu),
            "sidebar" => new UiLayoutQuery(UiLayoutScope.Sidebar),
            "content" => new UiLayoutQuery(UiLayoutScope.Content),
            "bottom" => new UiLayoutQuery(UiLayoutScope.Bottom),
            "right" => new UiLayoutQuery(UiLayoutScope.Right),
            _ => new UiLayoutQuery(UiLayoutScope.Tab, trimmed)
        };
    }
}

public sealed record UiLayoutMenuItem(
    string MenuPath,
    string Header,
    string? CommandId,
    int Order,
    bool IsSeparator);

public sealed record UiLayoutTabItem(
    string Id,
    string Title,
    bool Selected,
    JsonElement? Extra = null);

/// <summary>当前 Shell 布局快照：menu 为顶栏菜单项；sidebar/content/bottom/right 为各区域已打开 Tab（最深层仅 Tab；可选 extra 由模块自描述）。</summary>
public sealed record UiLayoutSnapshot(
    UiLayoutMenuItem[] Menu,
    UiLayoutTabItem[] Sidebar,
    UiLayoutTabItem[] Content,
    UiLayoutTabItem[] Bottom,
    UiLayoutTabItem[] Right);

public sealed record UiLayoutMenuSection(UiLayoutMenuItem[] Menu);

public sealed record UiLayoutSidebarSection(UiLayoutTabItem[] Sidebar);

public sealed record UiLayoutContentSection(UiLayoutTabItem[] Content);

public sealed record UiLayoutBottomSection(UiLayoutTabItem[] Bottom);

public sealed record UiLayoutRightSection(UiLayoutTabItem[] Right);

public sealed record UiLayoutActiveSection
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UiLayoutTabItem[]? Sidebar { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UiLayoutTabItem[]? Content { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UiLayoutTabItem[]? Bottom { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UiLayoutTabItem[]? Right { get; init; }
}

public sealed record UiLayoutTabFocus(string Region, UiLayoutTabItem Tab);

public interface IUiLayoutService
{
    UiLayoutSnapshot GetSnapshot();

    string GetLayoutJson(UiLayoutQuery query = default);

    /// <summary>按 Tab Id 在 sidebar/content/bottom/right 中查找并选中，无需指定区域。</summary>
    string SwitchTab(string tabId);
}
