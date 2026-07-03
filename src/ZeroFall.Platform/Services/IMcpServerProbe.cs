using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Platform.Models;

namespace ZeroFall.Platform.Services;

public sealed record McpProbeResult(bool Success, string Message, int ToolCount = 0);

/// <summary>探测单个 MCP 服务端是否可连接并列出工具（供设置页「测试连接」使用）。</summary>
public interface IMcpServerProbe
{
    Task<McpProbeResult> ProbeAsync(AiMcpServerConfig config, CancellationToken cancellationToken = default);
}
