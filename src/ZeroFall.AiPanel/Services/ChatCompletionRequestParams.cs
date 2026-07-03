using System.Text.Json.Nodes;
using ZeroFall.Platform.Models;

namespace ZeroFall.AiPanel.Services;

/// <summary>主会话与子 Agent 共用的 chat/completions 请求参数。</summary>
public static class ChatCompletionRequestParams
{
    public static string ResolveModel(AiSettings config, string? modelOverride) =>
        !string.IsNullOrEmpty(modelOverride) ? modelOverride : config.Model;

    public static void ApplyThinking(JsonObject requestBody, bool enableThinking)
    {
        if (!enableThinking)
            return;

        // Qwen / 部分 OpenAI 兼容网关
        requestBody["enable_thinking"] = true;
        // DeepSeek thinking 模式
        requestBody["thinking"] = new JsonObject { ["type"] = "enabled" };
    }
}
