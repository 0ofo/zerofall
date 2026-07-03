using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZeroFall.Platform.Models;

namespace ZeroFall.AiPanel.Services;

/// <summary>AI 聊天发送编排的稳定入口，逐步承接 ViewModel 中的发送状态机。</summary>
public sealed class ChatSendOrchestrator
{
    public const int MaxToolRounds = 100;

    public JsonObject BuildStreamingRequestBody(
        AiSettings config,
        IReadOnlyList<JsonNode> apiMessages,
        IReadOnlyList<string> toolDefinitions,
        bool enableThinking,
        bool disableTools = false)
    {
        var requestBody = new JsonObject { ["model"] = config.Model };

        var messagesArr = new JsonArray();
        foreach (var msg in apiMessages)
            messagesArr.Add(msg.DeepClone());
        requestBody["messages"] = messagesArr;

        if (!disableTools)
        {
            var toolsArr = new JsonArray();
            foreach (var definition in toolDefinitions)
            {
                try
                {
                    var node = JsonNode.Parse(definition);
                    if (node != null)
                        toolsArr.Add(node);
                }
                catch (JsonException)
                {
                    // 跳过损坏的 MCP/工具定义，避免整轮请求失败。
                }
            }

            if (toolsArr.Count > 0)
                requestBody["tools"] = toolsArr;
        }
        else
        {
            requestBody["tool_choice"] = "none";
        }

        ChatCompletionRequestParams.ApplyThinking(requestBody, enableThinking);
        return requestBody;
    }

    public string BuildToolRoundLimitMessage() =>
        $"工具/思考后续轮次已达上限（{MaxToolRounds}），请简化请求。";
}
