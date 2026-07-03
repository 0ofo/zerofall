using System.Text.Json.Nodes;

namespace ZeroFall.AiPanel.Services;

/// <summary>工具结果进入模型上下文前的轻量投影；持久化和 UI 仍保留完整输出。</summary>
public static class ToolResultContextProjection
{
    public const int MaxToolResultApiTokens = 2000;

    public static string ProjectForApi(string? output, string modelId, string path)
    {
        output ??= string.Empty;
        var estimatedTokens = AiChatTokenCounter.CountText(output, modelId);
        if (estimatedTokens <= MaxToolResultApiTokens)
            return output;

        return new JsonObject
        {
            ["ok"] = false,
            ["truncated"] = true,
            ["message"] = "工具结果过长，请使用 look 读取并筛选",
            ["path"] = path,
            ["hint"] = "可配合 grep=正则，或 head/tail/start_line/end_line",
            ["length"] = output.Length
        }.ToJsonString();
    }
}
