using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace ZeroFall.AiPanel.Services;

/// <summary>创建 MCP 传输与探测连接，供 <see cref="McpAiToolBridge"/> 与 <see cref="McpServerProbe"/> 共用。</summary>
internal static class McpClientConnectHelper
{
    public static IClientTransport? TryCreateTransport(
        AiMcpServerConfig srv,
        IProxyGatewayService proxyGatewayService)
    {
        var serverSlug = Slugify(string.IsNullOrWhiteSpace(srv.Id) ? "srv" : srv.Id);
        var transportKind = (srv.Transport ?? "stdio").Trim().ToLowerInvariant();
        if (transportKind is "http" or "sse" or "streamable-http")
        {
            ApplyProxyEnvironmentForHttpTransport(proxyGatewayService);
            if (string.IsNullOrWhiteSpace(srv.HttpEndpoint)
                || !Uri.TryCreate(srv.HttpEndpoint.Trim(), UriKind.Absolute, out var endpoint))
                return null;

            return new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = endpoint,
                    Name = serverSlug
                },
                NullLoggerFactory.Instance);
        }

        if (string.IsNullOrWhiteSpace(srv.Command))
            return null;

        var args = srv.Arguments ?? new List<string>();
        IDictionary<string, string?>? env = null;
        if (srv.EnvironmentVariables is { Count: > 0 })
        {
            env = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (key, value) in srv.EnvironmentVariables)
                env[key] = value;
        }

        return new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = serverSlug,
                Command = srv.Command.Trim(),
                Arguments = args,
                WorkingDirectory = string.IsNullOrWhiteSpace(srv.WorkingDirectory) ? null : srv.WorkingDirectory.Trim(),
                EnvironmentVariables = env
            },
            NullLoggerFactory.Instance);
    }

    public static async Task<McpProbeResult> ProbeAsync(
        AiMcpServerConfig srv,
        IProxyGatewayService proxyGatewayService,
        CancellationToken cancellationToken)
    {
        var transport = TryCreateTransport(srv, proxyGatewayService);
        if (transport == null)
        {
            return new McpProbeResult(
                false,
                string.Equals(srv.Transport, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(srv.Transport, "sse", StringComparison.OrdinalIgnoreCase)
                    ? "配置无效：HTTP 模式需要有效的绝对 URL 端点。"
                    : "配置无效：stdio 模式需要填写启动命令。");
        }

        await using var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions(),
            NullLoggerFactory.Instance,
            cancellationToken).ConfigureAwait(false);

        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new McpProbeResult(true, $"连接成功，发现 {tools.Count} 个工具。", tools.Count);
        }
        catch (Exception ex)
        {
            return new McpProbeResult(false, $"连接失败: {ex.Message}");
        }
    }

    internal static void ApplyProxyEnvironmentForHttpTransport(IProxyGatewayService proxyGatewayService)
    {
        var state = proxyGatewayService.CurrentState;
        var endpoint = state.EffectiveEndpoint;
        var isProxyMode = state.EffectiveMode is ProxyModes.Fixed or ProxyModes.FluxzyGateway;
        if (isProxyMode && !string.IsNullOrWhiteSpace(endpoint))
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", endpoint);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", endpoint);
            return;
        }

        if (state.EffectiveMode == ProxyModes.Direct)
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", null);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
        }
    }

    internal static string Slugify(string id)
    {
        var chars = id.Trim().ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                continue;
            chars[i] = '_';
        }

        var s = new string(chars).Trim('_');
        return string.IsNullOrEmpty(s) ? "srv" : s;
    }
}
