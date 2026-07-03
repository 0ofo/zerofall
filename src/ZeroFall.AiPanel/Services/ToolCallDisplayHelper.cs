using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using ZeroFall.Base.AiTools;

namespace ZeroFall.AiPanel.Services;

/// <summary>将工具名与 JSON 参数格式化为聊天面板可读的摘要。</summary>
public static class ToolCallDisplayHelper
{
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.Ordinal)
    {
        ["look"] = "查看文件",
        ["write"] = "写入文件",
        ["move"] = "移动文件",
        ["sql"] = "SQL 查询",
        ["ask"] = "询问用户",
        ["ui_context"] = "UI 上下文",
        ["get_ui_context"] = "UI 上下文",
        ["get_ui_layout"] = "UI 布局",
        ["invoke_ui_menu"] = "菜单命令",
        ["switch_ui_tab"] = "切换 Tab",
        ["web_search"] = "网络搜索",
        ["fetch"] = "抓取/发包",
        ["page_content"] = "页面内容",
        ["browser_tab"] = "浏览器标签",
        ["browser_open_tab"] = "打开浏览器标签",
        ["browser_tabs"] = "浏览器标签",
        ["browser_navigate"] = "浏览器导航",
        ["browser_reload"] = "刷新页面",
        ["browser_cookies"] = "读取 Cookie",
        ["browser_website_tree"] = "网站树",
        ["browser_cdp"] = "浏览器 CDP",
        ["http"] = "HTTP 请求",
        ["send_terminal_command"] = "终端命令",
        ["asset_recon"] = "资产测绘",
        ["asset_recon_query"] = "资产测绘",
        ["asset_recon_fetch_more"] = "测绘追加拉取",
        ["interrupt_terminal"] = "终端 Ctrl+C",
        ["read_terminal"] = "读取终端",
        ["get_terminal_state"] = "终端状态",
        ["proxy"] = "代理",
        ["todo"] = "待办",
        ["spawn_agent"] = "子 Agent",
    };

    public static string GetDisplayName(string toolName)
    {
        if (string.IsNullOrEmpty(toolName))
            return "工具";

        if (DisplayNames.TryGetValue(toolName, out var name))
            return name;

        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = toolName.Split("__", StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3
                ? $"MCP · {parts[^1]}"
                : "MCP 工具";
        }

        return toolName;
    }

    public static string FormatCommandSummary(string toolName, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return PrettyJson(argumentsJson);

            var oneLine = TryOneLineSummary(toolName, root);
            if (!string.IsNullOrEmpty(oneLine))
                return oneLine;

            return PrettyJsonElement(root);
        }
        catch
        {
            return argumentsJson.Trim();
        }
    }

    private static string? TryOneLineSummary(string toolName, JsonElement root)
    {
        return toolName switch
        {
            "send_terminal_command" => GetString(root, "command"),
            "asset_recon" => JoinParts(GetString(root, "action"), GetString(root, "query") ?? GetString(root, "queryTaskId"), GetString(root, "source")),
            "asset_recon_query" => JoinParts(GetString(root, "query"), GetString(root, "source")),
            "asset_recon_fetch_more" => GetString(root, "queryTaskId"),
            "look" => FormatLookArgs(root),
            "write" => JoinParts(GetString(root, "path"), GetString(root, "append") == "true" ? "append" : null),
            "move" => JoinParts(GetString(root, "source"), GetString(root, "destination")),
            "sql" => JoinParts(GetString(root, "path"), Truncate(GetString(root, "sql"), 200)),
            "ask" => GetString(root, "question"),
            "web_search" => JoinParts(GetString(root, "query"), FormatCount(root, "count")),
            "fetch" => FormatFetchArgs(root),
            "http" => FormatFetchArgs(root),
            "web_fetch" => FormatWebFetchArgs(root),
            "page_content" => root.TryGetProperty("isMd", out var isMdEl) && isMdEl.ValueKind == JsonValueKind.False
                ? "raw HTML"
                : "Markdown",
            "browser_tab" => JoinParts(GetString(root, "action"), GetString(root, "url") ?? GetString(root, "tabId")),
            "browser_open_tab" => JoinParts(GetString(root, "url"), GetString(root, "title")),
            "browser_tabs" => JoinParts(GetString(root, "action"), GetString(root, "tabId")),
            "browser_navigate" => JoinParts(
                GetString(root, "url"),
                root.TryGetProperty("newTab", out var nt) && nt.ValueKind == JsonValueKind.True ? "新标签" : GetString(root, "tabId")),
            "browser_cookies" => JoinParts(GetString(root, "url"), GetString(root, "domain")),
            "browser_website_tree" => GetString(root, "site") ?? "(当前站点)",
            "browser_cdp" => JoinParts(GetString(root, "method"), Truncate(GetString(root, "parameters") ?? GetString(root, "parametersJson"), 120)),
            "ui_context" => JoinParts(FormatUiLayoutScope(root), root.TryGetProperty("includeSelection", out var inc) && inc.ValueKind == JsonValueKind.False ? "no selection" : null),
            "get_ui_context" => "(当前界面)",
            "get_ui_layout" => FormatUiLayoutScope(root),
            "invoke_ui_menu" => JoinParts(GetString(root, "commandId"), GetString(root, "path"), GetString(root, "tabId")),
            "switch_ui_tab" => GetString(root, "tabId"),
            "proxy" => JoinParts(GetString(root, "action"), GetString(root, "mode")),
            "todo" => root.TryGetProperty("markdown", out _) ? "更新" : "读取",
            "read_terminal" => TryPositiveInt(root, "tail") is { } tailLines ? $"tail {tailLines}" : "tail 50",
            "spawn_agent" => Truncate(GetString(root, "task"), 160),
            _ when toolName.StartsWith("mcp__", StringComparison.Ordinal) => PrettyJsonElement(root, maxDepth: 1),
            _ => null
        };
    }

    public static bool LooksLikeErrorResult(string? text) => ToolResultJson.IsErrorJson(text);

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? FormatInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return null;
        return el.GetRawText();
    }

    private static string? FormatCount(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return null;
        return $"条数 {el.GetRawText()}";
    }

    private static string? FormatLookArgs(JsonElement root)
    {
        var find = GetString(root, "find");
        var grep = GetString(root, "grep");
        var path = GetString(root, "path");
        if (string.IsNullOrEmpty(path))
            path = ".";

        var slice = TryPositiveInt(root, "head") is { } h ? $"head {h}"
            : TryPositiveInt(root, "tail") is { } t ? $"tail {t}"
            : TryPositiveInt(root, "start_line") is { } s
                ? TryPositiveInt(root, "end_line") is { } e ? $"L{s}-{e}" : $"L{s}+"
                : TryPositiveInt(root, "end_line") is { } eOnly ? $"L1-{eOnly}"
                : null;

        return JoinParts(
            !string.IsNullOrWhiteSpace(grep) ? "grep" : !string.IsNullOrWhiteSpace(find) ? "find" : null,
            !string.IsNullOrWhiteSpace(grep) ? Truncate(grep, 120)
                : !string.IsNullOrWhiteSpace(find) ? Truncate(find, 120)
                : null,
            slice is null ? path : JoinParts(path, slice));
    }

    private static string? TryPositiveInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return null;
        if (!el.TryGetInt32(out var n) || n <= 0)
            return null;
        return n.ToString();
    }

    private static string? FormatFetchArgs(JsonElement root) => GetString(root, "url");

    private static string? FormatWebFetchArgs(JsonElement root)
    {
        if (root.TryGetProperty("urls", out var urlsEl) && urlsEl.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in urlsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        parts.Add(s);
                }
            }

            if (parts.Count > 0)
                return string.Join(", ", parts);
        }

        return GetString(root, "url");
    }

    private static string JoinParts(params string?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (string.IsNullOrWhiteSpace(p))
                continue;
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append(p);
        }

        return sb.ToString();
    }

    private static string FormatUiLayoutScope(JsonElement root)
    {
        var scope = GetString(root, "scope");
        if (string.IsNullOrWhiteSpace(scope) || string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
            return "(完整布局)";

        return scope.Trim().ToLowerInvariant() switch
        {
            "active" or "menu" or "sidebar" or "content" or "bottom" or "right" => scope,
            _ => scope.Trim()
        };
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        text = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return PrettyJsonElement(doc.RootElement);
        }
        catch
        {
            return json;
        }
    }

    private static string PrettyJsonElement(JsonElement el, int maxDepth = 3, int depth = 0)
    {
        var raw = el.GetRawText();
        if (depth >= maxDepth && el.ValueKind == JsonValueKind.Object)
        {
            var lines = raw.Split('\n');
            if (lines.Length > 12)
                return string.Join('\n', lines.AsSpan(0, 12).ToArray()) + "\n  …";
        }

        if (raw.Length > 4000)
            return raw[..4000] + "\n…";
        return raw;
    }
}
