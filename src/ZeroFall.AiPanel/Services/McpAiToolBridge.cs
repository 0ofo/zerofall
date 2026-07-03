using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Base.AiTools;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ZeroFall.AiPanel.Services;

/// <summary>
/// 将 MCP 远端工具桥接到 <see cref="AiToolRegistry"/>：使用官方 <c>ModelContextProtocol.Core</c>（支持 Native AOT），
/// 工具名统一为 <c>mcp__{serverId}__{tool}</c> 以避免与内置工具冲突。
/// </summary>
public sealed class McpAiToolBridge : IAsyncDisposable
{
    public const string RegisteredToolPrefix = "mcp__";

    private readonly ISettingsService _settingsService;
    private readonly AiToolRegistry _toolRegistry;
    private readonly IProxyGatewayService _proxyGatewayService;
    private readonly IAiChatRunContext _runContext;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly List<IAsyncDisposable> _sessions = new();

    public McpAiToolBridge(
        ISettingsService settingsService,
        AiToolRegistry toolRegistry,
        IProxyGatewayService proxyGatewayService,
        IAiChatRunContext runContext)
    {
        _settingsService = settingsService;
        _toolRegistry = toolRegistry;
        _proxyGatewayService = proxyGatewayService;
        _runContext = runContext;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeSessionsCoreAsync().ConfigureAwait(false);
            _toolRegistry.UnregisterPrefix(RegisteredToolPrefix);

            var ai = _settingsService.Load().Ai;
            if (!ai.McpEnabled || ai.McpServers.Count == 0)
                return;

            foreach (var srv in ai.McpServers.Where(s => s.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ConnectOneServerAsync(srv, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task ConnectOneServerAsync(AiMcpServerConfig srv, CancellationToken cancellationToken)
    {
        var serverSlug = McpClientConnectHelper.Slugify(string.IsNullOrWhiteSpace(srv.Id) ? "srv" : srv.Id);
        var transport = McpClientConnectHelper.TryCreateTransport(srv, _proxyGatewayService);
        if (transport == null)
            return;

        var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions(),
            NullLoggerFactory.Instance,
            cancellationToken).ConfigureAwait(false);

        _sessions.Add(client);

        IList<McpClientTool> tools;
        try
        {
            tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var protocolName = tool.Name;
            if (string.IsNullOrEmpty(protocolName))
                continue;

            var safeTool = SanitizeToolSegment(protocolName);
            var regName = $"{RegisteredToolPrefix}{serverSlug}__{safeTool}";
            for (var n = 2; !usedNames.Add(regName); n++)
                regName = $"{RegisteredToolPrefix}{serverSlug}__{safeTool}_{n}";

            var openAiJson = BuildOpenAiToolJson(regName, tool);
            var captured = tool;
            _toolRegistry.RegisterOpenAiToolJson(regName, args => ExecuteCapturedToolAsync(captured, regName, args), openAiJson);
        }
    }

    private async Task<ToolCallResult> ExecuteCapturedToolAsync(
        McpClientTool tool,
        string regName,
        ToolArguments args)
    {
        var ct = _runContext.CancellationToken;
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dict = JsonArgumentsToDictionary(args.RawArgumentsJson);
            var result = await tool.CallAsync(dict, progress: null, options: null, ct)
                .ConfigureAwait(false);
            return new ToolCallResult(ToolResultJson.Normalize(FormatCallToolResult(result), result.IsError == true ? 1 : 0), regName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolCallResult(ToolResultJson.Error($"MCP 工具调用失败: {ex.Message}"), regName);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static string BuildOpenAiToolJson(string regName, McpClientTool tool)
    {
        var schemaText = "{\"type\":\"object\",\"properties\":{}}";
        try
        {
            var schema = tool.JsonSchema;
            if (schema.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                schemaText = schema.GetRawText();
        }
        catch
        {
        }

        JsonNode? schemaNode;
        try
        {
            schemaNode = JsonNode.Parse(schemaText);
        }
        catch
        {
            schemaNode = JsonNode.Parse(schemaText)!;
        }

        var fn = new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = regName,
                ["description"] = string.IsNullOrEmpty(tool.Description) ? (tool.Title ?? "") : tool.Description,
                ["parameters"] = schemaNode ?? new JsonObject()
            }
        };

        return fn.ToJsonString();
    }

    private static IReadOnlyDictionary<string, object?> JsonArgumentsToDictionary(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        using var doc = JsonDocument.Parse(rawJson);
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return d;

        foreach (var p in doc.RootElement.EnumerateObject())
            d[p.Name] = JsonElementToArg(p.Value);
        return d;
    }

    private static object? JsonElementToArg(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToArg).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(x => x.Name, x => JsonElementToArg(x.Value), StringComparer.Ordinal),
        _ => null
    };

    private static string FormatCallToolResult(CallToolResult result)
    {
        var sb = new StringBuilder(256);
        if (result.IsError == true)
            sb.Append("[错误] ");
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock t)
                sb.Append(t.Text);
        }

        if (sb.Length == 0 && result.StructuredContent is JsonElement sc
            && sc.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            return sc.GetRawText();

        return sb.Length > 0 ? sb.ToString() : "{}";
    }

    private static string SanitizeToolSegment(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                sb.Append(c);
            else
                sb.Append('_');
        }

        var r = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(r) ? "tool" : r;
    }

    private async Task DisposeSessionsCoreAsync()
    {
        for (var i = _sessions.Count - 1; i >= 0; i--)
        {
            try
            {
                await _sessions[i].DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _sessions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeSessionsCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
            _mutex.Dispose();
        }
    }
}
