using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ZeroFall.AiPanel.Models;
using ZeroFall.Base.AiTools;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Services;

/// <summary>
/// 上下文占用达到阈值时在末尾追加压缩 user；会话 <c>api_start_message_id</c> 记录 API 回放起点，UI 用消息 <c>visual</c> 过滤。
/// </summary>
public sealed class ChatContextCompressionService
{
  /// <summary>压缩后消息列表前缀长度：user 压缩指令 + assistant 摘要。</summary>
  public const int CompressedHeadMessageCount = 2;
  public const int DefaultCompressionThresholdPercent = 50;
  public const int MinCompressionThresholdPercent = 40;
  public const int MaxCompressionThresholdPercent = 80;

  /// <summary>压缩 HTTP 请求内单段文本上限；工具 output 进入模型前走 ToolResultContextProjection。</summary>
  private const int CompressionSourceMaxChars = 4096;

  /// <summary>落库/API 回放用的摘要 assistant 正文上限。</summary>
  private const int StoredSummaryMaxChars = 24_000;

  /// <summary>压缩任务 user 消息正文（用户在界面看到并发送的同一条；勿调用工具）。</summary>
  public const string CompressionInstruction = """
      【上下文压缩】
      请把上方对话压缩成后续可以直接接着工作的中文上下文摘要。目标不是写简短概述，而是保留足够的信息，让之后的助手无需读取被压缩的历史也能继续执行。

      必须保留：
      - 用户当前目标、明确要求、偏好和否定过的方案；
      - 已经做出的设计决策、约束、默认值、阈值、兼容性取舍；
      - 涉及的文件路径、类名、方法名、数据库表/字段、配置项、命令和关键参数；
      - 工具调用或命令输出中的结论、错误、警告、测试/构建结果；
      - 已完成事项、未完成事项、下一步计划和需要再次验证的风险；
      - 对行为有影响的细节，例如 UI 表现、持久化语义、token 计算口径、回退/撤销规则。

      可以丢弃寒暄、重复确认、无效尝试和不影响后续工作的中间措辞。
      近期信息优先保留：越靠近当前回合的目标、决策、文件、命令、错误、测试结果和待办越要完整；更早的信息按重要性挑重点保留即可。
      不要编造未出现的信息。禁止调用任何工具，禁止执行任何操作。
      仅输出摘要正文，优先使用清晰分段或短项目符号。
      """;

  public static int NormalizeCompressionThresholdPercent(int percent) =>
      Math.Clamp(percent, MinCompressionThresholdPercent, MaxCompressionThresholdPercent);

  public static double NormalizeCompressionThresholdRatio(int percent) =>
      NormalizeCompressionThresholdPercent(percent) / 100.0;

  public static int FindLastRoundStartIndex(IReadOnlyList<ChatMessage> messages)
  {
    for (var i = messages.Count - 1; i >= 0; i--)
    {
      if (messages[i].Role == ChatRole.User && !string.IsNullOrWhiteSpace(messages[i].Content))
        return i;
    }

    return messages.Count;
  }

  /// <summary>API 回放起始消息下标：已压缩时为 apiStartMessageId 对应消息；否则按 token 预算裁剪。</summary>
  public static int GetApiStartMessageIndex(
      IReadOnlyList<ChatMessage> messages,
      string systemPrompt,
      string modelId,
      int contextLimit,
      long apiStartMessageId)
  {
    if (apiStartMessageId > 0)
      return ChatHistoryMapper.ResolveMessageIndexFromMessageId(messages, apiStartMessageId);

    return ChatContextCompressor.ComputeStartIndex(messages, systemPrompt, modelId, contextLimit);
  }

  /// <summary>未压缩时按预算裁剪的起始下标；已压缩时为 message id 对应消息下标。</summary>
  public static int GetUncompressedApiSkipIndex(
      IReadOnlyList<ChatMessage> messages,
      string systemPrompt,
      string modelId,
      int contextLimit,
      long apiStartMessageId) =>
      GetApiStartMessageIndex(messages, systemPrompt, modelId, contextLimit, apiStartMessageId);

  /// <summary>是否存在值得摘要的对话内容（非「仅摘要对、无新消息」）。</summary>
  public static bool HasCompressibleApiHistory(
      IReadOnlyList<ChatMessage> messages,
      string systemPrompt,
      string modelId,
      int contextLimit,
      long apiStartMessageId)
  {
    if (messages.Count == 0)
      return false;

    if (apiStartMessageId > 0)
    {
      var compressStart = ChatHistoryMapper.ResolveMessageIndexFromMessageId(messages, apiStartMessageId);
      var afterSummary = compressStart + 1;
      while (afterSummary < messages.Count && !messages[afterSummary].Visual.IsVisibleInUi())
        afterSummary++;

      if (afterSummary >= messages.Count)
        return false;

      return HasVisibleApiContent(messages, afterSummary);
    }

    return HasVisibleApiContent(messages, startIndex: 0);
  }

  private static bool HasVisibleApiContent(IReadOnlyList<ChatMessage> messages, int startIndex)
  {
    for (var i = Math.Max(0, startIndex); i < messages.Count; i++)
    {
      var m = messages[i];
      if (!m.Visual.IsVisibleInUi())
        continue;

      if (m.IsUser && !string.IsNullOrWhiteSpace(m.Content))
        return true;

      if (m.HasToolCall)
        return true;

      if (m.Role == ChatRole.Assistant
          && (!string.IsNullOrWhiteSpace(m.Content) || !string.IsNullOrWhiteSpace(m.ReasoningContent)))
        return true;
    }

    return false;
  }

  public static bool ShouldCompress(
      IReadOnlyList<ChatMessage> messages,
      string systemPrompt,
      string modelId,
      int contextLimit,
      long apiStartMessageId,
      int? measuredPromptTokens = null,
      int compressionThresholdPercent = DefaultCompressionThresholdPercent)
  {
    if (contextLimit <= 0)
      return false;

    if (!HasCompressibleApiHistory(messages, systemPrompt, modelId, contextLimit, apiStartMessageId))
      return false;

    var thresholdRatio = NormalizeCompressionThresholdRatio(compressionThresholdPercent);
    if (measuredPromptTokens is int promptTokens
        && promptTokens >= contextLimit * thresholdRatio)
      return true;

    var (used, _) = EstimateApiContextTokens(messages, systemPrompt, modelId, contextLimit, apiStartMessageId);
    if (used < contextLimit * thresholdRatio)
      return false;

    return true;
  }

  public static (int UsedTokens, int? ContextLimit) EstimateApiContextTokens(
      IReadOnlyList<ChatMessage> messages,
      string systemPrompt,
      string modelId,
      int contextLimit,
      long apiStartMessageId)
  {
    var used = EstimateUsedApiTokens(messages, systemPrompt, modelId, contextLimit, apiStartMessageId);
    return (used, contextLimit > 0 ? contextLimit : null);
  }

  /// <summary>按单条缓存估算 API 载荷 token，避免 <see cref="BuildApiMessageNodes"/> 构建整棵 Json 树。</summary>
  public static int EstimateUsedApiTokens(
      IReadOnlyList<ChatMessage> messages,
      string systemPrompt,
      string modelId,
      int contextLimit,
      long apiStartMessageId)
  {
    var total = ChatMessageTokenEstimator.GetOrComputeSystemApiTokens(systemPrompt, modelId);

    var skip = GetApiStartMessageIndex(messages, systemPrompt, modelId, contextLimit, apiStartMessageId);
    for (var i = skip; i < messages.Count; i++)
    {
      if (ChatApiErrorHelper.IsUiOnlyAssistantMessage(messages[i]))
        continue;

      total += ChatMessageTokenEstimator.GetOrComputeMessageApiTokens(messages[i], modelId);
    }

    return total;
  }

  public static int CountIncludedApiMessages(
      IReadOnlyList<ChatMessage> messages,
      string systemPrompt,
      string modelId,
      int contextLimit,
      long apiStartMessageId)
  {
    var skip = GetApiStartMessageIndex(messages, systemPrompt, modelId, contextLimit, apiStartMessageId);
    var included = 0;
    for (var i = skip; i < messages.Count; i++)
    {
      if (!ChatApiErrorHelper.IsUiOnlyAssistantMessage(messages[i]))
        included++;
    }

    return included;
  }

  public static List<JsonNode> BuildApiMessageNodes(
      IReadOnlyList<ChatMessage> messages,
      string systemPrompt,
      string modelId,
      int contextLimit,
      long apiStartMessageId)
  {
    var list = new List<JsonNode>
    {
      new JsonObject
      {
        ["role"] = "system",
        ["content"] = systemPrompt
      }
    };

    var skip = GetApiStartMessageIndex(messages, systemPrompt, modelId, contextLimit, apiStartMessageId);

    for (var i = skip; i < messages.Count; i++)
      AppendApiMessage(list, messages, modelId, ref i);

    return list;
  }

  private static void AppendApiMessage(
      List<JsonNode> list,
      IReadOnlyList<ChatMessage> messages,
      string modelId,
      ref int i)
  {
    var m = messages[i];
    if (ChatApiErrorHelper.IsUiOnlyAssistantMessage(m))
      return;

    if (m.IsUser)
    {
      TryAppendUserMessage(list, m);
      return;
    }

    string? leadingReasoning = null;
    if (ChatApiErrorHelper.IsReasoningOnlyAssistant(m))
    {
      leadingReasoning = m.ReasoningContent;
      i++;
      if (i >= messages.Count)
        return;
      m = messages[i];
    }

    // 同一轮：正文气泡紧接工具调用 → 合并进同一条 assistant
    string? preambleContent = null;
    if (m.Role == ChatRole.Assistant && !m.HasToolCall && !string.IsNullOrWhiteSpace(m.Content))
    {
      if (i + 1 < messages.Count && messages[i + 1].HasToolCall)
      {
        preambleContent = m.Content;
        leadingReasoning = MergeReasoning(leadingReasoning, m.ReasoningContent);
        i++;
        m = messages[i];
      }
    }

    if (m.HasToolCall)
    {
      var toolRun = new List<(ChatMessage Tool, long MessageId)>();
      while (i < messages.Count && messages[i].HasToolCall)
      {
        var messageId = ChatHistoryMapper.GetToolMessageId(messages, i);
        toolRun.Add((messages[i], messageId));
        i++;
      }

      i--;
      AppendToolRun(list, toolRun, modelId, preambleContent, leadingReasoning);
      return;
    }

    TryAppendAssistantMessage(list, m, leadingReasoning);
  }

  private static void TryAppendUserMessage(List<JsonNode> list, ChatMessage m)
  {
    if (string.IsNullOrWhiteSpace(m.Content))
      return;

    list.Add(new JsonObject
    {
      ["role"] = "user",
      ["content"] = m.Content
    });
  }

  private static void AppendToolRun(
      List<JsonNode> list,
      IReadOnlyList<(ChatMessage Tool, long MessageId)> tools,
      string modelId,
      string? preambleContent,
      string? leadingReasoning)
  {
    if (tools.Count == 0)
      return;

    var reasoning = leadingReasoning ?? string.Empty;
    foreach (var item in tools)
      reasoning = MergeReasoning(reasoning, item.Tool.ReasoningContent);

    var content = !string.IsNullOrWhiteSpace(preambleContent)
        ? preambleContent
        : tools.Select(item => item.Tool.Content).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

    var assistant = new JsonObject { ["role"] = "assistant" };
    if (!string.IsNullOrWhiteSpace(content))
      assistant["content"] = content;
    else
      assistant["content"] = JsonValue.Create((string?)null);

    if (!string.IsNullOrEmpty(reasoning))
      assistant["reasoning_content"] = reasoning;

    var toolCallsArr = new JsonArray();
    var callIds = new string[tools.Count];
    for (var ti = 0; ti < tools.Count; ti++)
    {
      var tool = tools[ti].Tool;
      callIds[ti] = !string.IsNullOrEmpty(tool.ToolCallId) ? tool.ToolCallId : $"call_hist_{ti}";
      var args = !string.IsNullOrEmpty(tool.ToolArgumentsJson) ? tool.ToolArgumentsJson : (tool.ToolCommand ?? "{}");
      toolCallsArr.Add(new JsonObject
      {
        ["id"] = callIds[ti],
        ["type"] = "function",
        ["function"] = new JsonObject
        {
          ["name"] = tool.ToolName,
          ["arguments"] = args
        }
      });
    }

    assistant["tool_calls"] = toolCallsArr;
    list.Add(assistant);

    for (var ti = 0; ti < tools.Count; ti++)
    {
      var (tool, messageId) = tools[ti];
      if (tool.IsToolRunning)
        continue;

      var path = BuildToolResultPath(messageId);
      list.Add(new JsonObject
      {
        ["role"] = "tool",
        ["tool_call_id"] = callIds[ti],
        ["content"] = ToolResultContextProjection.ProjectForApi(tool.ToolOutput, modelId, path)
      });
    }
  }

  public static string BuildToolResultPath(long messageId) =>
      messageId > 0 ? $"@tool_result:{messageId}" : "@tool_result:unknown";

  private static void TryAppendAssistantMessage(List<JsonNode> list, ChatMessage m, string? leadingReasoning = null)
  {
    var reasoning = MergeReasoning(leadingReasoning, m.ReasoningContent);
    if (string.IsNullOrEmpty(m.Content) && string.IsNullOrEmpty(reasoning))
      return;

    var msgObj = new JsonObject { ["role"] = "assistant" };
    if (string.IsNullOrEmpty(m.Content))
      msgObj["content"] = JsonValue.Create((string?)null);
    else
      msgObj["content"] = m.Content;
    if (!string.IsNullOrEmpty(reasoning))
      msgObj["reasoning_content"] = reasoning;
    list.Add(msgObj);
  }

  public static string SlimStoredSummary(string text) =>
      SlimCompressionText(text, StoredSummaryMaxChars);

  private static string SlimCompressionText(string? text, int maxChars = CompressionSourceMaxChars)
  {
    if (string.IsNullOrEmpty(text))
      return string.Empty;

    if (text.Length <= maxChars)
      return text;

    const string marker = "…[压缩载荷已截断]";
    var keep = maxChars - marker.Length - 8;
    if (keep < 64)
      keep = 64;

    return text[..keep] + marker;
  }

  private static string MergeReasoning(string? leading, string? existing)
  {
    if (string.IsNullOrWhiteSpace(leading))
      return existing ?? string.Empty;
    if (string.IsNullOrWhiteSpace(existing))
      return leading;
    if (string.Equals(leading, existing, StringComparison.Ordinal))
      return existing;
    return leading + "\n" + existing;
  }
}
