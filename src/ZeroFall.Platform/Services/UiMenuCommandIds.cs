namespace ZeroFall.Platform.Services;

/// <summary>顶栏菜单 commandId（<c>ui_context</c> scope=menu 可见），由 <c>invoke_ui_menu</c> 调用。</summary>
public static class UiMenuCommandIds
{
    public const string OpenFolder = "workspace.openFolder";
    public const string OpenFile = "workspace.openFile";
    public const string NewQuery = "workspace.newQuery";
    public const string NewBrowser = "ui.newBrowser";
    public const string NewTerminal = "ui.newTerminal";
    public const string OpenTerminalPanel = "ui.openTerminalPanel";
    public const string OpenReconPanel = "ui.openReconPanel";
    public const string OpenPortScanPanel = "ui.openPortScanPanel";
    public const string CloseTab = "ui.closeTab";
}

public sealed record UiMenuCommandArgs(string? Path = null, string? TabId = null);
