using System;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using ZeroFall.Base.AiTools;
using ZeroFall.Base.Events;
using ZeroFall.Dock.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.Dock.Services;

public sealed class UiMenuCommandService : IUiMenuCommandService
{
    private readonly IEventBus _eventBus;
    private readonly IWorkspaceService _workspaceService;
    private DockLayoutViewModel? _dock;

    public UiMenuCommandService(IEventBus eventBus, IWorkspaceService workspaceService)
    {
        _eventBus = eventBus;
        _workspaceService = workspaceService;
    }

    public void Attach(DockLayoutViewModel dock) => _dock = dock;

    public string Execute(string commandId, UiMenuCommandArgs? args = null)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            return ToolResultJson.Error("commandId 不能为空");

        commandId = commandId.Trim();
        args ??= new UiMenuCommandArgs();

        if (Dispatcher.UIThread.CheckAccess())
            return ExecuteCore(commandId, args);

        return Dispatcher.UIThread.Invoke(() => ExecuteCore(commandId, args));
    }

    private string ExecuteCore(string commandId, UiMenuCommandArgs args)
    {
        switch (commandId)
        {
            case UiMenuCommandIds.NewBrowser:
                _eventBus.Publish(new OpenBrowserTabRequestedEvent(string.Empty, "新标签页"));
                return ToolResultJson.Ok("已新建浏览器 Content 标签");

            case UiMenuCommandIds.NewTerminal:
                _eventBus.Publish(new NewTerminalSessionRequestedEvent());
                return ToolResultJson.Ok("已新建终端会话");

            case UiMenuCommandIds.OpenTerminalPanel:
                _eventBus.Publish(new TerminalVisibilityChangedEvent(true));
                _eventBus.Publish(new SwitchDockTabRequestedEvent(DockPosition.Bottom, "terminal"));
                return ToolResultJson.Ok("已打开终端面板");

            case UiMenuCommandIds.OpenReconPanel:
                _eventBus.Publish(new SwitchDockTabRequestedEvent(DockPosition.Left, "asset-recon-left"));
                return ToolResultJson.Ok("已打开侦察面板");

            case UiMenuCommandIds.OpenPortScanPanel:
                _eventBus.Publish(new SwitchDockTabRequestedEvent(DockPosition.Content, "port-scan"));
                return ToolResultJson.Ok("已打开端口扫描面板");

            case UiMenuCommandIds.OpenFolder:
                _eventBus.Publish(new OpenFolderRequestedEvent());
                return ToolResultJson.Ok("已请求打开文件夹对话框");

            case UiMenuCommandIds.OpenFile:
                if (string.IsNullOrWhiteSpace(args.Path))
                {
                    _eventBus.Publish(new OpenWorkspaceFileRequestedEvent());
                    return ToolResultJson.Ok("已请求打开文件对话框");
                }

                var filePath = ResolveWorkspaceFilePath(args.Path);
                if (filePath == null)
                {
                    return ToolResultJson.Error(
                        "未打开工作区，无法解析相对路径。请先打开文件夹，或使用绝对路径。");
                }

                if (!File.Exists(filePath))
                    return ToolResultJson.Error($"文件不存在: {args.Path.Trim()}");

                _eventBus.Publish(new OpenWorkspaceFileRequestedEvent(filePath));
                return ToolResultJson.Ok($"已在 Content 区打开文件: {args.Path.Trim()}");

            case UiMenuCommandIds.NewQuery:
                _eventBus.Publish(new NewQueryRequestedEvent(string.Empty, string.Empty));
                return ToolResultJson.Ok("已新建 SQL 查询标签");

            case UiMenuCommandIds.CloseTab:
                return CloseContentTab(args.TabId);

            default:
                return ToolResultJson.Error(
                    $"未知 commandId「{commandId}」。请 get_ui_layout scope=menu 查看可用项。");
        }
    }

    private string CloseContentTab(string? tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
            return ToolResultJson.Error("ui.closeTab 需要 tabId 参数");

        tabId = tabId.Trim();
        if (_dock == null)
            return ToolResultJson.Error("UI 布局尚未就绪");

        var tab = _dock.ContentPanel.Tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
        if (tab == null)
        {
            return ToolResultJson.Error(
                $"Content 区未找到 Tab「{tabId}」。仅可关闭 Content 区可关闭标签（如 browser-*）。");
        }

        if (!tab.IsClosable)
            return ToolResultJson.Error($"Tab「{tabId}」不可关闭");

        _eventBus.Publish(new CloseContentTabRequestedEvent(tabId));
        return ToolResultJson.Ok($"已关闭 Content 标签 {tabId}");
    }

    private string? ResolveWorkspaceFilePath(string path) =>
        WorkspacePathHelper.ResolveFilePath(
            path,
            _workspaceService.CurrentWorkspace?.DirectoryPath);
}
