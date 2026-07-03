using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ZeroFall.Base.AiTools;
using ZeroFall.Platform.Services;
using ZeroFall.Terminal;

namespace ZeroFall.Terminal.Tools;

public sealed class TerminalAiToolService
{
    private const int DefaultWaitSeconds = 3;

    private readonly ITerminalScreenService _screenService;
    private readonly ITerminalCommandService _commandService;
    private readonly ITerminalSessionStateService _sessionStateService;
    private readonly IAiChatRunContext _runContext;

    public TerminalAiToolService(
        ITerminalScreenService screenService,
        ITerminalCommandService commandService,
        ITerminalSessionStateService sessionStateService,
        IAiChatRunContext runContext)
    {
        _screenService = screenService;
        _commandService = commandService;
        _sessionStateService = sessionStateService;
        _runContext = runContext;
    }

    [AiTool(
        "send_terminal_command",
        """
        向 ZeroFall 底部交互式 PTY 终端发送一行输入并回车（等同用户亲自敲键盘）。
        这是在本应用内运行 shell / cmd / ssh / sudo / 远程排查的唯一正确方式（无 execute_command，勿用 ssh host 'cmd' 等非交互一次性写法）。
        工作流：send → 看返回 JSON 的 output → 若出现 password:、确认提示、分页符或输出未完，再 send 下一行或配合 read_terminal。
        ssh 示例：先 send "ssh user@host"，见 password: 后再 send 密码，登录后再 send 业务命令。
        vim 退出插入模式可 send "\\e" 或 "<Esc>"（发送 Escape）；也可用 interrupt_terminal 发 Ctrl+C。
        waitSeconds：最长等待秒数（默认 3，最大 30）；命令结束（输出含 _cmd_end_ 或 waitEndPattern 匹配）会提前返回。
        waitEndPattern：可选正则；Windows 未指定时默认 cmd/PS 提示符。也可在命令末尾加 `& echo _cmd_end_`（PS 用 `; echo _cmd_end_`）立即结束等待。
        返回 JSON 仅含 output 与 secondsSinceLastOutput（距上次终端输出更新的秒数；接近 0 表示仍在刷新，可再 read_terminal 加大 tail）。
        """)]
    public async Task<string> SendTerminalCommand(
        [ToolParam("要发送到 PTY 的一行内容（命令、密码、y 确认等）；慢命令可末尾加 & echo _cmd_end_")] string command,
        [ToolParam("最长等待秒数，默认 3；命令结束会提前返回", Required = false)] int waitSeconds = DefaultWaitSeconds,
        [ToolParam("等待结束的提示符正则（可选）；Windows 省略时默认匹配 cmd/PS 提示符", Required = false)] string? waitEndPattern = null,
        [ToolParam("终端会话 Id（可选，默认当前选中的内层 Tab）", Required = false)] string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ToolResultJson.Error("命令不能为空");

        var (endPattern, _) = TerminalCommandWait.ResolveEndPattern(waitEndPattern, out var patternError);
        if (patternError != null)
            return ToolResultJson.Error(patternError);

        var maxWait = ClampWaitSeconds(waitSeconds);
        var ct = _runContext.CancellationToken;
        await _commandService.SendCommandAsync(command, sessionId).ConfigureAwait(false);
        await TerminalCommandWait.WaitAsync(
            _screenService, _sessionStateService, sessionId, maxWait, endPattern, command, ct).ConfigureAwait(false);
        return await BuildTerminalResultAsync(sessionId, command, incrementalOutput: false).ConfigureAwait(false);
    }

    [AiTool(
        "interrupt_terminal",
        "向底部 PTY 终端发送 Ctrl+C，中断当前前台程序（如卡住的 ssh、ping、top）。返回 output 与 secondsSinceLastOutput。")]
    public async Task<string> InterruptTerminal(
        [ToolParam("最长等待秒数，默认 3", Required = false)] int waitSeconds = DefaultWaitSeconds,
        [ToolParam("等待结束的提示符正则（可选）；Windows 省略时默认 cmd/PS 提示符", Required = false)] string? waitEndPattern = null,
        [ToolParam("终端会话 Id（可选，默认当前选中的内层 Tab）", Required = false)] string? sessionId = null)
    {
        var (endPattern, _) = TerminalCommandWait.ResolveEndPattern(waitEndPattern, out var patternError);
        if (patternError != null)
            return ToolResultJson.Error(patternError);

        var maxWait = ClampWaitSeconds(waitSeconds);
        var ct = _runContext.CancellationToken;
        await _commandService.SendInterruptAsync(sessionId).ConfigureAwait(false);
        await TerminalCommandWait.WaitAsync(
            _screenService, _sessionStateService, sessionId, maxWait, endPattern, sentCommand: null, ct).ConfigureAwait(false);
        return await BuildTerminalResultAsync(sessionId, sentCommand: null, incrementalOutput: false).ConfigureAwait(false);
    }

    [AiTool(
        "read_terminal",
        """
        只读底部 PTY 终端屏幕末尾若干行（不发送任何输入）。
        用于等待慢命令、ssh 登录过程、或观察当前输出；可与 send_terminal_command 交替多轮调用。
        查某会话**完整历史输出**（含已滚出屏幕的）请用 sql：terminal_transcript_lines（path 省略），比反复 read 更可靠。
        tail：返回末尾行数，默认 50，最大 400。
        waitSeconds：最长等待秒数（默认 3）；_cmd_end_ 或 waitEndPattern 匹配会提前返回。
        返回 JSON 含 output、tail 与 secondsSinceLastOutput（接近 0 表示仍在刷输出，可再调用本工具）。
        """)]
    public async Task<string> ReadTerminal(
        [ToolParam("返回终端末尾行数，默认 50", Required = false)] int tail = 50,
        [ToolParam("最长等待秒数，默认 3", Required = false)] int waitSeconds = DefaultWaitSeconds,
        [ToolParam("等待结束的提示符正则（可选）；Windows 省略时默认 cmd/PS 提示符", Required = false)] string? waitEndPattern = null,
        [ToolParam("终端会话 Id（可选，默认当前选中的内层 Tab）", Required = false)] string? sessionId = null)
    {
        var (endPattern, _) = TerminalCommandWait.ResolveEndPattern(waitEndPattern, out var patternError);
        if (patternError != null)
            return ToolResultJson.Error(patternError);

        var maxWait = ClampWaitSeconds(waitSeconds);
        var ct = _runContext.CancellationToken;
        await TerminalCommandWait.WaitAsync(
            _screenService, _sessionStateService, sessionId, maxWait, endPattern, sentCommand: null, ct).ConfigureAwait(false);
        return await BuildTailReadResultAsync(sessionId, tail).ConfigureAwait(false);
    }

    private static int ClampWaitSeconds(int waitSeconds) =>
        Math.Clamp(waitSeconds, 0, TerminalCommandWait.MaxWaitSeconds);

    private async Task<string> BuildTailReadResultAsync(string? sessionId, int tail)
    {
        var lineCount = Math.Clamp(tail, 1, TerminalScreenReader.AiMaxLines);
        var secondsSinceLastOutput = await _sessionStateService.GetSecondsSinceLastOutputAsync(sessionId).ConfigureAwait(false);
        var output = await _screenService.ReadLastLinesAsync(sessionId, lineCount).ConfigureAwait(false);

        if (output == null)
        {
            return ToolResultJson.Data(o =>
            {
                o["ok"] = false;
                o["output"] = string.Empty;
                o["tail"] = lineCount;
                WriteSecondsSinceLastOutput(o, secondsSinceLastOutput);
            });
        }

        return ToolResultJson.Data(o =>
        {
            o["ok"] = true;
            o["output"] = output;
            o["tail"] = lineCount;
            WriteSecondsSinceLastOutput(o, secondsSinceLastOutput);
        });
    }

    private async Task<string> BuildTerminalResultAsync(
        string? sessionId,
        string? sentCommand,
        bool incrementalOutput)
    {
        var secondsSinceLastOutput = await _sessionStateService.GetSecondsSinceLastOutputAsync(sessionId).ConfigureAwait(false);
        var sinceCommand = await _screenService.ReadSinceLastCommandAsync(sessionId, sentCommand).ConfigureAwait(false);
        var output = incrementalOutput
            ? await _screenService.ReadSinceLastAiToolReadAsync(sessionId).ConfigureAwait(false)
            : sinceCommand;

        if (!incrementalOutput)
            output = TerminalOutputFormatter.FormatSendOutput(output, sentCommand);

        try
        {
            if (output == null)
            {
                return ToolResultJson.Data(o =>
                {
                    o["ok"] = false;
                    o["output"] = string.Empty;
                    WriteSecondsSinceLastOutput(o, secondsSinceLastOutput);
                });
            }

            return ToolResultJson.Data(o =>
            {
                o["output"] = output;
                WriteSecondsSinceLastOutput(o, secondsSinceLastOutput);
            });
        }
        finally
        {
            if (incrementalOutput)
                await _screenService.CommitAiReadCursorAsync(sessionId).ConfigureAwait(false);
        }
    }

    private static void WriteSecondsSinceLastOutput(System.Text.Json.Nodes.JsonObject o, double? secondsSinceLastOutput)
    {
        if (secondsSinceLastOutput != null)
            o["secondsSinceLastOutput"] = secondsSinceLastOutput.Value;
    }
}
