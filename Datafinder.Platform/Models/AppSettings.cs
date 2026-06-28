using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Datafinder.Platform.Models;

public class AppSettings
{
    [JsonPropertyName("ai")]
    public AiSettings Ai { get; set; } = new();

    [JsonPropertyName("fofa")]
    public FofaSettings Fofa { get; set; } = new();

    [JsonPropertyName("assetRecon")]
    public AssetReconSettings AssetRecon { get; set; } = new();

    [JsonPropertyName("terminal")]
    public TerminalSettings Terminal { get; set; } = new();

    [JsonPropertyName("general")]
    public GeneralSettings General { get; set; } = new();

    [JsonPropertyName("layout")]
    public LayoutSettings Layout { get; set; } = new();

    [JsonPropertyName("proxy")]
    public ProxySettings Proxy { get; set; } = new();
}

public class AiSettings
{
    [JsonPropertyName("apiBaseUrl")]
    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>已知模型列表（API 拉取 + 手动添加），供设置页与 AI 面板下拉选择。</summary>
    [JsonPropertyName("knownModels")]
    public List<string> KnownModels { get; set; } = new();

    /// <summary>按 API Base URL 分组的模型目录（替换 base url 时各自独立，避免列表叠加）。</summary>
    [JsonPropertyName("modelCatalogs")]
    public Dictionary<string, List<AiModelEntry>> ModelCatalogs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiBaseUrl)
                              && !string.IsNullOrWhiteSpace(ApiKey)
                              && !string.IsNullOrWhiteSpace(Model);

    /// <summary>Bing Web Search API 密钥（Azure Cognitive Services）。未配置时 web_search 回退 RSS/HTML。</summary>
    [JsonPropertyName("bingSearchApiKey")]
    public string BingSearchApiKey { get; set; } = string.Empty;

    /// <summary>AI 面板是否向模型请求思考过程（reasoning_content）。</summary>
    [JsonPropertyName("enableThinking")]
    public bool EnableThinking { get; set; }

    /// <summary>是否在聊天请求中合并 MCP 服务端工具（stdio / HTTP）。</summary>
    [JsonPropertyName("mcpEnabled")]
    public bool McpEnabled { get; set; }

    [JsonPropertyName("mcpServers")]
    public List<AiMcpServerConfig> McpServers { get; set; } = new();
}

/// <summary>单个 MCP 服务端配置（Model Context Protocol）。</summary>
public sealed class AiMcpServerConfig
{
    /// <summary>逻辑标识，用于生成稳定工具名前缀。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "default";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>stdio | http（HTTP 使用 SSE / Streamable HTTP 自动探测）。</summary>
    [JsonPropertyName("transport")]
    public string Transport { get; set; } = "stdio";

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = new();

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    /// <summary>HTTP/SSE 远端 MCP 端点（绝对 URI）。</summary>
    [JsonPropertyName("httpEndpoint")]
    public string? HttpEndpoint { get; set; }

    /// <summary>stdio 子进程环境变量（覆盖同名系统变量）。</summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
}

public class FofaSettings
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://fofa.info";

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(ApiKey);
}

public class AssetReconSettings
{
    [JsonPropertyName("fofaEnabled")] public bool FofaEnabled { get; set; } = true;
    [JsonPropertyName("fofaEmail")] public string FofaEmail { get; set; } = string.Empty;
    [JsonPropertyName("fofaKey")] public string FofaKey { get; set; } = string.Empty;
    [JsonPropertyName("fofaBaseUrl")] public string FofaBaseUrl { get; set; } = "https://fofa.info";

    [JsonPropertyName("hunterEnabled")] public bool HunterEnabled { get; set; } = false;
    [JsonPropertyName("hunterKey")] public string HunterKey { get; set; } = string.Empty;
    [JsonPropertyName("hunterBaseUrl")] public string HunterBaseUrl { get; set; } = "https://hunter.qianxin.com";

    [JsonPropertyName("quakeEnabled")] public bool QuakeEnabled { get; set; } = false;
    [JsonPropertyName("quakeKey")] public string QuakeKey { get; set; } = string.Empty;
    [JsonPropertyName("quakeBaseUrl")] public string QuakeBaseUrl { get; set; } = "https://quake.360.net";

    [JsonPropertyName("shodanEnabled")] public bool ShodanEnabled { get; set; } = false;
    [JsonPropertyName("shodanKey")] public string ShodanKey { get; set; } = string.Empty;
    [JsonPropertyName("shodanBaseUrl")] public string ShodanBaseUrl { get; set; } = "https://api.shodan.io";
}

public class TerminalSettings
{
    [JsonPropertyName("shellPath")]
    public string ShellPath { get; set; } = string.Empty;

    [JsonPropertyName("fontFamily")]
    public string FontFamily { get; set; } = "SimHei, SimSun, NSimSun, monospace";

    [JsonPropertyName("fontSize")]
    public int FontSize { get; set; } = 13;
}

public class GeneralSettings
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh-CN";

    [JsonPropertyName("autoOpenLastProject")]
    public bool AutoOpenLastProject { get; set; } = true;

    [JsonPropertyName("lastProjectPath")]
    public string LastProjectPath { get; set; } = string.Empty;

    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = false;

}

public static class ProxyModes
{
    public const string Direct = "direct";
    public const string System = "system";
    public const string Fixed = "fixed";
    public const string FluxzyGateway = "fluxzy_gateway";
}

public class ProxySettings
{
    /// <summary>代理模式：direct/system/fixed/fluxzy_gateway。</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = ProxyModes.Direct;

    /// <summary>
    /// 固定代理或网关上游代理地址，例如 http://127.0.0.1:7890。
    /// 当 Mode=fixed 时表示直接使用该地址；当 Mode=fluxzy_gateway 时表示 Fluxzy 上游。
    /// </summary>
    [JsonPropertyName("upstreamProxyUrl")]
    public string UpstreamProxyUrl { get; set; } = string.Empty;

    /// <summary>Fluxzy 网关监听主机（默认 127.0.0.1）。</summary>
    [JsonPropertyName("gatewayHost")]
    public string GatewayHost { get; set; } = "127.0.0.1";

    /// <summary>Fluxzy 网关监听端口（默认 18080）。</summary>
    [JsonPropertyName("gatewayPort")]
    public int GatewayPort { get; set; } = 18080;

    /// <summary>是否启用本地 HTTP(S) 拦截代理监听（Burp 风格，流量写入监控表）。</summary>
    [JsonPropertyName("listenerEnabled")]
    public bool ListenerEnabled { get; set; }

    /// <summary>是否启用 HTTPS 解密（MITM）。默认关闭，需用户明确授权。</summary>
    [JsonPropertyName("httpsInterceptionEnabled")]
    public bool HttpsInterceptionEnabled { get; set; }

    [JsonPropertyName("bypassHosts")]
    public List<string> BypassHosts { get; set; } = [];

    /// <summary>代理 Match &amp; Replace 规则（按 host 过滤后对报文头/体做文本或正则替换）。</summary>
    [JsonPropertyName("replaceRules")]
    public List<ProxyReplaceRule> ReplaceRules { get; set; } = [];
}

public class ProxyReplaceRule
{
    [JsonPropertyName("remark")]
    public string Remark { get; set; } = string.Empty;

    /// <summary>主机匹配，支持 * 通配符，如 *.example.com；留空表示任意主机。</summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("match")]
    public string Match { get; set; } = string.Empty;

    [JsonPropertyName("isRegex")]
    public bool IsRegex { get; set; }

    [JsonPropertyName("replacement")]
    public string Replacement { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public class LayoutSettings
{
    [JsonPropertyName("leftPanelVisible")]
    public bool LeftPanelVisible { get; set; } = true;

    [JsonPropertyName("rightPanelVisible")]
    public bool RightPanelVisible { get; set; } = true;

    [JsonPropertyName("bottomPanelVisible")]
    public bool BottomPanelVisible { get; set; } = false;

    [JsonPropertyName("leftSelectedTabId")]
    public string LeftSelectedTabId { get; set; } = string.Empty;

    [JsonPropertyName("rightSelectedTabId")]
    public string RightSelectedTabId { get; set; } = string.Empty;

    [JsonPropertyName("bottomSelectedTabId")]
    public string BottomSelectedTabId { get; set; } = string.Empty;

    [JsonPropertyName("contentSelectedTabId")]
    public string ContentSelectedTabId { get; set; } = string.Empty;
}
