using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ZeroFall.AiPanel.Services;
using ZeroFall.Base.AiTools;
using ZeroFall.Platform.Serialization;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Tools.Builtin;

public sealed class ContextToolService
{
    private readonly IUiContextService _uiContextService;
    private readonly IUiLayoutService _uiLayoutService;
    private readonly IUiMenuCommandService _uiMenuCommandService;
    private readonly LookService _lookService;

    public ContextToolService(
        IUiContextService uiContextService,
        IUiLayoutService uiLayoutService,
        IUiMenuCommandService uiMenuCommandService,
        LookService lookService)
    {
        _uiContextService = uiContextService;
        _uiLayoutService = uiLayoutService;
        _uiMenuCommandService = uiMenuCommandService;
        _lookService = lookService;
    }

    [AiTool(
        "ui_context",
        """
        获取当前 UI 上下文与布局快照。默认 scope=active，仅返回各区域当前 Tab，加上当前内容标签页/选中数据摘要。
        scope 可选：active|all|menu|sidebar|content|bottom|right，或 Dock Tab Id（如 terminal、browser-xxx）。
        includeSelection=true 时附带当前选中数据摘要；只需要布局时可设 false。
        """)]
    public async Task<string> UiContext(
        [ToolParam("active|all|menu|sidebar|content|bottom|right，或 Dock Tab Id，默认 active", Required = false)] string scope = "active",
        [ToolParam("是否附带当前选中数据摘要，默认 true", Required = false)] bool includeSelection = true)
    {
        var root = new JsonObject();
        if (includeSelection)
        {
            var snapshot = _uiContextService.GetSnapshot();
            root["context"] = JsonSerializer.SerializeToNode(snapshot, PlatformJsonContext.Default.UiContextSnapshot);
        }

        var layoutJson = await _uiLayoutService.GetLayoutJsonAsync(UiLayoutQuery.Parse(scope)).ConfigureAwait(false);
        try
        {
            root["layout"] = JsonNode.Parse(layoutJson);
        }
        catch
        {
            root["layout"] = layoutJson;
        }

        return root.ToJsonString();
    }

    public string GetUiContext()
    {
        var snapshot = _uiContextService.GetSnapshot();
        return JsonSerializer.Serialize(snapshot, PlatformJsonContext.Default.UiContextSnapshot);
    }

    public Task<string> GetUiLayout(string scope = "all") =>
        _uiLayoutService.GetLayoutJsonAsync(UiLayoutQuery.Parse(scope));

    [AiTool(
        "invoke_ui_menu",
        """
        执行顶栏菜单 commandId（见 ui_context scope=menu 各条的 commandId）。
        常用：ui.newBrowser、ui.newTerminal、ui.openTerminalPanel、ui.openReconPanel、ui.openPortScanPanel、
        workspace.openFile（需 path，可相对工作区）、ui.closeTab（需 tabId，仅 Content 可关闭标签）。
        """)]
    public Task<string> InvokeUiMenu(
        [ToolParam("菜单 commandId")] string commandId,
        [ToolParam("workspace.openFile 的文件路径（可相对工作区）", Required = false)] string? path = null,
        [ToolParam("ui.closeTab 的 Content Tab Id", Required = false)] string? tabId = null)
    {
        var resolvedPath = ResolveOptionalPath(path);
        return _uiMenuCommandService.ExecuteAsync(commandId, new UiMenuCommandArgs(resolvedPath, tabId));
    }

    [AiTool(
        "switch_ui_tab",
        """
        按 Tab Id 切换 UI 选中标签，无需指定区域（自动在 sidebar/content/bottom/right 中查找）。
        tabId 来自 ui_context 各区域 Tab 的 id 字段（如 terminal、traffic-monitor、browser-xxx、port-scan）。
        注意：Bottom 终端内层 sessionId 不是 Dock Tab Id；切换终端会话请用 send_terminal_command 的 sessionId 参数。
        """)]
    public Task<string> SwitchUiTab(
        [ToolParam("Dock Tab Id，来自 ui_context")] string tabId)
        => _uiLayoutService.SwitchTabAsync(tabId);

    private string? ResolveOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = path.Trim();
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        var workspace = _lookService.WorkspaceDirectory;
        if (string.IsNullOrWhiteSpace(workspace))
            return path;

        return Path.GetFullPath(Path.Combine(workspace, path));
    }
}
