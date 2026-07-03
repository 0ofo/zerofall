using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Services;

public sealed class McpServerProbe : IMcpServerProbe
{
    private readonly IProxyGatewayService _proxyGatewayService;

    public McpServerProbe(IProxyGatewayService proxyGatewayService)
    {
        _proxyGatewayService = proxyGatewayService;
    }

    public Task<McpProbeResult> ProbeAsync(AiMcpServerConfig config, CancellationToken cancellationToken = default)
        => McpClientConnectHelper.ProbeAsync(config, _proxyGatewayService, cancellationToken);
}
