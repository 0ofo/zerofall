using System.Text.Json.Nodes;
using System.Text.Json;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Platform.Services;

/// <summary>
/// 为 <see cref="get_ui_layout"/> 中单个 Dock Tab 附加 AI 可用的 extra 子 JSON（布局树仍只到 Tab 层）。
/// </summary>
public interface IUiLayoutTabExtraProvider
{
    bool TryGetExtra(DockTabItemViewModel tab, DockPosition region, out JsonElement extra);
}

public sealed record TerminalSessionLayoutItem(
    string SessionId,
    string Title,
    bool Selected,
    string CommandPhase);

/// <summary>Bottom「终端」Dock Tab 的 extra：内层 PTY 会话列表。</summary>
public sealed record TerminalUiLayoutExtra(
    int ActiveSessionCount,
    string? ActiveSessionId,
    TerminalSessionLayoutItem[] Sessions);

/// <summary>Content 区浏览器 Dock Tab 的 extra：供 browser_* 工具指定 tabId。</summary>
public sealed record BrowserContentTabUiLayoutExtra(
    string TabId,
    int PageSessionId,
    string Url,
    string TopLevelUrl,
    bool HasCdpSession,
    bool IsActiveBrowserTab);
