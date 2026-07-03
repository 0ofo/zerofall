using System.Threading;

using System.Threading.Tasks;

using ZeroFall.AiPanel.Services;

using ZeroFall.Base.AiTools;

using ZeroFall.Platform.Services;



namespace ZeroFall.AiPanel.Tools.Builtin;



/// <summary>子 Agent 工具：让主 Agent 派生独立子任务 Agent。</summary>

public sealed class SubAgentToolService

{

    private readonly SubAgentRunner _runner;

    private readonly IAiChatRunContext _runContext;

    private readonly IAiChatSessionContext _sessionContext;

    private readonly SubAgentSessionHub _sessionHub;



    public SubAgentToolService(

        SubAgentRunner runner,

        IAiChatRunContext runContext,

        IAiChatSessionContext sessionContext,

        SubAgentSessionHub sessionHub)

    {

        _runner = runner;

        _runContext = runContext;

        _sessionContext = sessionContext;

        _sessionHub = sessionHub;

    }



    [AiTool("spawn_agent", "派生一个子 Agent 执行独立子任务。子 Agent 共享主 Agent 的全部工具（终端/文件/浏览器/web/SQL/资产测绘等），跑自己的对话循环后用简洁中文摘要汇报。用于：复杂调研、多步搜索汇总、独立子流程等。不能再派生子 Agent。")]

    public async Task<string> SpawnAgentAsync(

        [ToolParam("交给子 Agent 的任务描述，越具体越好。例如：'调研 github.com/foo 仓库的技术栈并总结'。")] string task,

        [ToolParam("最大等待秒数，默认 180", Required = false)] int timeout_seconds = 180)

    {

        if (string.IsNullOrWhiteSpace(task))

            return ToolResultJson.Error("task 不能为空");

        if (SubAgentRunner.CurrentlyInSubAgent)

            return ToolResultJson.Error("当前已在子 Agent 上下文中，不能再派生子 Agent");



        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_runContext.CancellationToken);

        if (timeout_seconds > 0)

            cts.CancelAfter(System.TimeSpan.FromSeconds(timeout_seconds));



        var parentSessionId = _sessionContext.CurrentSessionId;

        var liveSession = _sessionHub.BeginSession(task, parentSessionId);

        var sessionId = liveSession.Id;



        var progress = new SubAgentProgress

        {

            OnRound = round => _sessionHub.SetRoundHint(sessionId, round),

            OnToolCall = (id, name, args) =>

                _sessionHub.BeginToolCall(sessionId, id, name, args),

            OnToolResult = (id, result, exitCode) =>

                _sessionHub.CompleteToolCall(sessionId, id, result, exitCode)

        };



        SubAgentResult result;

        try

        {

            result = await _runner.RunAsync(task, sessionId, progress, cts.Token);

        }

        catch (System.OperationCanceledException)

        {

            _sessionHub.EndAssistantStreaming(sessionId, removeIfEmpty: false);

            _sessionHub.SetStatus(sessionId, SubAgentRunStatus.Cancelled);

            return ToolResultJson.Data(o =>

            {

                o["success"] = false;

                o["error"] = "子 Agent 已取消";

                o["summary"] = string.Empty;

                o["sub_agent_session_id"] = sessionId;

            });

        }

        catch (System.Exception ex)

        {

            _sessionHub.EndAssistantStreaming(sessionId, removeIfEmpty: false);

            _sessionHub.SetStatus(sessionId, SubAgentRunStatus.Failed);

            return ToolResultJson.Data(o =>

            {

                o["success"] = false;

                o["error"] = $"子 Agent 执行异常: {ex.Message}";

                o["summary"] = string.Empty;

                o["sub_agent_session_id"] = sessionId;

            });

        }



        if (!result.Success)

        {

            var status = result.Error?.Contains("超时", System.StringComparison.Ordinal) == true

                ? SubAgentRunStatus.Cancelled

                : SubAgentRunStatus.Failed;

            _sessionHub.SetStatus(sessionId, status);

            return ToolResultJson.Data(o =>

            {

                o["success"] = false;

                o["error"] = result.Error;

                o["summary"] = result.Summary;

                o["rounds"] = result.Rounds;

                o["tool_calls"] = result.ToolCalls;

                o["sub_agent_session_id"] = sessionId;

            });

        }



        _sessionHub.SetStatus(sessionId, SubAgentRunStatus.Completed);

        return ToolResultJson.Data(o =>

        {

            o["success"] = true;

            o["summary"] = result.Summary;

            o["rounds"] = result.Rounds;

            o["tool_calls"] = result.ToolCalls;

            o["sub_agent_session_id"] = sessionId;

        });

    }

}


