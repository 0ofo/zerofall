using System.Threading.Tasks;
using ZeroFall.AiPanel.Services;
using ZeroFall.Base.AiTools;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Tools.Builtin;

/// <summary>AI 待办工具。用单条 markdown 文本表示，按当前会话隔离持久化。</summary>
public sealed class TodoToolService
{
    private readonly IAiTodoStore _store;
    private readonly IAiChatSessionContext _sessionContext;

    public TodoToolService(IAiTodoStore store, IAiChatSessionContext sessionContext)
    {
        _store = store;
        _sessionContext = sessionContext;
    }

    [AiTool("todo",
        "读取或更新当前会话待办清单（markdown，支持 - [ ] / - [x]）。不传参数或 read=true 为读取；传 markdown 则整体替换（空字符串可清空）。")]
    public async Task<string> TodoAsync(
        [ToolParam("待办 markdown 全文；读取时省略", Required = false)] string? markdown = null,
        [ToolParam("true 时仅读取当前待办", Required = false)] bool read = false)
    {
        var sid = _sessionContext.CurrentSessionId;
        if (string.IsNullOrEmpty(sid))
            return ToolResultJson.Error("当前无活动会话");

        if (markdown != null && !read)
        {
            await _store.SaveMarkdownAsync(sid, markdown).ConfigureAwait(false);
            return ToolResultJson.Data(o =>
            {
                o["ok"] = true;
                o["session_id"] = sid;
                o["saved"] = true;
                o["markdown"] = markdown;
            });
        }

        return await ReadTodoAsync(sid).ConfigureAwait(false);
    }

    private async Task<string> ReadTodoAsync(string sessionId)
    {
        var current = await _store.GetMarkdownAsync(sessionId).ConfigureAwait(false);
        return ToolResultJson.Data(o =>
        {
            o["ok"] = true;
            o["read"] = true;
            o["session_id"] = sessionId;
            o["markdown"] = current;
            o["empty"] = string.IsNullOrEmpty(current);
        });
    }
}
