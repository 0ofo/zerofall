using System;
using System.Collections.Generic;
using Datafinder.Platform.Models;

namespace Datafinder.Platform.Services;

/// <summary>
/// <see href="https://github.com/Aas-ee/open-webSearch">open-websearch</see> MCP 预设。
/// 默认 HTTP：<c>http://localhost:3000/mcp</c>（需先运行 <c>npx open-websearch serve</c>）。
/// </summary>
public static class OpenWebSearchMcpPreset
{
    public const string ServerId = "open-websearch";
    public const string DefaultHttpEndpoint = "http://localhost:3000/mcp";

    /// <summary>HTTP 本地 daemon（官方推荐 Web API / streamable HTTP 方式）。</summary>
    public static AiMcpServerConfig CreateHttpLocal() => new()
    {
        Id = ServerId,
        Enabled = true,
        Transport = "http",
        HttpEndpoint = DefaultHttpEndpoint
    };

    /// <summary>stdio + npx（无需单独 daemon，但首次启动较慢）。</summary>
    public static AiMcpServerConfig CreateStdio()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MODE"] = "stdio",
            ["DEFAULT_SEARCH_ENGINE"] = "duckduckgo",
            ["ALLOWED_SEARCH_ENGINES"] = "duckduckgo,bing,exa,brave"
        };

        var systemRoot = Environment.GetEnvironmentVariable("SYSTEMROOT");
        if (!string.IsNullOrWhiteSpace(systemRoot))
            env["SYSTEMROOT"] = systemRoot;

        return new()
        {
            Id = ServerId,
            Enabled = true,
            Transport = "stdio",
            Command = "npx",
            Arguments = ["-y", "open-websearch@latest"],
            EnvironmentVariables = env
        };
    }

    public static AiMcpServerConfig Create() => CreateHttpLocal();
}
