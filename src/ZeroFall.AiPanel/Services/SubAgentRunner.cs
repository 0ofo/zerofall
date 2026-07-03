using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Base.AiTools;
using ZeroFall.Base.Diagnostics;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Services;

/// <summary>子 Agent 最终结果。</summary>
public sealed class SubAgentResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? Error { get; init; }
    public int Rounds { get; init; }
    public int ToolCalls { get; init; }

    public static SubAgentResult Ok(string summary, int rounds, int toolCalls) => new()
    {
        Success = true,
        Summary = summary,
        Rounds = rounds,
        ToolCalls = toolCalls
    };

    public static SubAgentResult Failed(string error) => new()
    {
        Success = false,
        Error = error,
        Summary = error
    };
}

/// <summary>子 Agent 进度回调。</summary>
public sealed class SubAgentProgress
{
    public Action<string>? OnRound { get; init; }
    public Action<string, string, string>? OnToolCall { get; init; }
    public Action<string, string, int>? OnToolResult { get; init; }
}

/// <summary>子 Agent 运行器：用相同 ApiConfig + 工具集跑独立的流式对话循环，复用 AiToolRegistry。</summary>
public sealed class SubAgentRunner
{
    private const int MaxRounds = 30;
    private const string SubAgentCorePrompt = """
        你是 ZeroFall 的子 Agent，被主 Agent 派来执行一个独立子任务。

        ## 行为准则
        - 专注完成主 Agent 交给你的任务，不要扩展范围。
        - 充分使用可用工具（终端、文件、浏览器、web 搜索、SQL、资产测绘等）获取信息或执行操作。
        - 工具调用失败时，分析错误并重试或换方案，不要直接放弃。
        - 完成后用**简洁的中文摘要**汇报结果：做了什么、关键发现/产出、是否有未完成事项。
        - 不要寒暄、不要重复任务描述、不要输出与任务无关的内容。

        ## 限制
        - 你**不能**再派生子 Agent（spawn_agent 不可用）。
        - 任务超时或超过最大工具轮次（约 30 轮）会自动终止；尽量合并工具调用、避免无效重试。
        """;

    private const string SubAgentInterruptedSummaryLead = """
        【子 Agent 已终止】因超时或达到轮次上限，执行已被强制停止。请仅根据上方已有 user/assistant/tool 消息写中文摘要交给主 Agent；不要继续执行任务、不要读文件、不要调用任何工具，不要输出 tool_calls / DSML / XML 等工具调用格式。

        """;

    private static readonly AsyncLocal<bool> IsInSubAgent = new();

    private readonly ISettingsService _settingsService;
    private readonly IOutboundHttpClientFactory _httpClientFactory;
    private readonly AiToolRegistry _toolRegistry;
    private readonly SubAgentSessionHub _sessionHub;
    private readonly IAiChatRunContext _runContext;
    private readonly IAiToolResultRuntimeStore _toolResultRuntimeStore;

    public SubAgentRunner(
        ISettingsService settingsService,
        IOutboundHttpClientFactory httpClientFactory,
        AiToolRegistry toolRegistry,
        SubAgentSessionHub sessionHub,
        IAiChatRunContext runContext,
        IAiToolResultRuntimeStore toolResultRuntimeStore)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _toolRegistry = toolRegistry;
        _sessionHub = sessionHub;
        _runContext = runContext;
        _toolResultRuntimeStore = toolResultRuntimeStore;
    }

    /// <summary>当前是否运行在子 Agent 上下文中（用于防止递归 spawn）。</summary>
    public static bool CurrentlyInSubAgent => IsInSubAgent.Value;

    public async Task<SubAgentResult> RunAsync(
        string prompt,
        string sessionId,
        SubAgentProgress? progress,
        CancellationToken cancellationToken)
    {
        if (IsInSubAgent.Value)
            return SubAgentResult.Failed("子 Agent 不能再派生子 Agent（避免递归）");

        var config = _settingsService.Load().Ai;
        if (!config.IsConfigured)
            return SubAgentResult.Failed("AI 未配置，请在设置中填写 API 地址/密钥/模型");

        IsInSubAgent.Value = true;
        try
        {
            using var http = _httpClientFactory.CreateClient("ai-subagent", TimeSpan.FromSeconds(180));
            return await RunLoopAsync(http, config, prompt, sessionId, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsInSubAgent.Value = false;
        }
    }

    private async Task<SubAgentResult> RunLoopAsync(
        HttpClient http,
        ZeroFall.Platform.Models.AiSettings config,
        string prompt,
        string sessionId,
        SubAgentProgress? progress,
        CancellationToken ct)
    {
        var baseUrl = config.ApiBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/chat/completions";

        var messages = new List<JsonObject>
        {
            new() { ["role"] = "system", ["content"] = BuildSubAgentSystemPrompt() },
            new() { ["role"] = "user", ["content"] = prompt }
        };

        var toolDefs = _toolRegistry.GetToolDefinitions();
        var totalToolCalls = 0;
        var nextToolResultSeq = 0L;
        var enableThinking = _runContext.EnableThinking;
        var model = ChatCompletionRequestParams.ResolveModel(config, _runContext.ModelOverride);
        var round = 0;

        try
        {
        for (round = 0; round < MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.OnRound?.Invoke($"第 {round + 1} 轮");

            var requestBody = new JsonObject { ["model"] = model };

            var messagesArr = new JsonArray();
            foreach (var m in messages)
                messagesArr.Add(m.DeepClone());
            requestBody["messages"] = messagesArr;

            var toolsArr = new JsonArray();
            foreach (var td in toolDefs)
            {
                try
                {
                    var node = JsonNode.Parse(td);
                    if (node is null)
                        continue;
                    var fn = node["function"]?.AsObject();
                    var name = fn?["name"]?.GetValue<string>();
                    if (string.Equals(name, "spawn_agent", StringComparison.Ordinal))
                        continue;
                    toolsArr.Add(node);
                }
                catch (JsonException)
                {
                }
            }
            if (toolsArr.Count > 0)
                requestBody["tools"] = toolsArr;

            ChatCompletionRequestParams.ApplyThinking(requestBody, enableThinking);

            HttpResponseMessage response;
            try
            {
                response = await SendStreamingChatCompletionAsync(
                    http, config, url, requestBody, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return SubAgentResult.Failed(ex.Message);
            }

            using (response)
            {
            StreamingRoundResult roundResult;
            try
            {
                roundResult = await ParseStreamingRoundAsync(
                    response,
                    sessionId,
                    enableThinking,
                    ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return SubAgentResult.Failed(ex.Message);
            }

            if (roundResult.ToolCalls is null || roundResult.ToolCalls.Count == 0)
            {
                var summary = string.IsNullOrEmpty(roundResult.Content) ? "（子 Agent 未返回正文）" : roundResult.Content;
                return SubAgentResult.Ok(summary, round + 1, totalToolCalls);
            }

            var assistantMsg = new JsonObject { ["role"] = "assistant" };
            if (!string.IsNullOrEmpty(roundResult.Content))
                assistantMsg["content"] = roundResult.Content;
            else
                assistantMsg["content"] = JsonValue.Create((string?)null);
            if (!string.IsNullOrEmpty(roundResult.ReasoningContent))
                assistantMsg["reasoning_content"] = roundResult.ReasoningContent;

            var toolCallsArr = new JsonArray();
            foreach (var tc in roundResult.ToolCalls)
            {
                toolCallsArr.Add(new JsonObject
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = tc.Arguments
                    }
                });
            }
            assistantMsg["tool_calls"] = toolCallsArr;
            messages.Add(assistantMsg);

            var browserToolRound = roundResult.ToolCalls.Any(tc => BrowserUiGate.IsBrowserStackTool(tc.Name));
            if (browserToolRound)
                BrowserUiGate.EnterBrowserToolRound();

            try
            {
            foreach (var tc in roundResult.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();

                if (!_sessionHub.TryGetSession(sessionId, out var live) || live is null
                    || !live.TryGetToolMessage(tc.Id, out _))
                {
                    progress?.OnToolCall?.Invoke(tc.Id, tc.Name, tc.Arguments);
                }

                totalToolCalls++;

                ToolCallResult result;
                try
                {
                    result = await AiToolUiDispatcher.ExecuteAsync(_toolRegistry, tc.Name, tc.Arguments, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = new ToolCallResult(ToolResultJson.Error($"工具执行异常: {ex.Message}"), tc.Name, exitCode: 1);
                }

                var exitCode = result.ExitCode;
                if (exitCode == 0 && ToolCallDisplayHelper.LooksLikeErrorResult(result.ResultText))
                    exitCode = 1;
                progress?.OnToolResult?.Invoke(tc.Id, result.ResultText ?? "", exitCode);

                var output = result.ResultText ?? string.Empty;
                var messageId = nextToolResultSeq++;
                _toolResultRuntimeStore.Upsert(messageId, output);
                var path = ChatContextCompressionService.BuildToolResultPath(messageId);
                var toolContentForApi = ToolResultContextProjection.ProjectForApi(output, model, path);

                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = tc.Id,
                    ["content"] = toolContentForApi
                });
            }
            }
            finally
            {
                if (browserToolRound)
                    BrowserUiGate.ExitBrowserToolRound();
            }
            } // response
        }

        var maxRoundSummary = await RequestCompressionSummaryAsync(
            http, config, messages, sessionId, enableThinking, CancellationToken.None).ConfigureAwait(false);
        return new SubAgentResult
        {
            Success = false,
            Error = $"子 Agent 达到最大轮次（{MaxRounds}）",
            Summary = maxRoundSummary,
            Rounds = MaxRounds,
            ToolCalls = totalToolCalls
        };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested
                                                  && !_runContext.CancellationToken.IsCancellationRequested)
        {
            _sessionHub.EndAssistantStreaming(sessionId, removeIfEmpty: false);
            var timeoutSummary = await RequestCompressionSummaryAsync(
                http, config, messages, sessionId, enableThinking, CancellationToken.None).ConfigureAwait(false);
            return new SubAgentResult
            {
                Success = false,
                Error = "子 Agent 已超时",
                Summary = timeoutSummary,
                Rounds = round,
                ToolCalls = totalToolCalls
            };
        }
    }

    private async Task<string> RequestCompressionSummaryAsync(
        HttpClient http,
        ZeroFall.Platform.Models.AiSettings config,
        List<JsonObject> messages,
        string sessionId,
        bool enableThinking,
        CancellationToken ct)
    {
        _sessionHub.EndAssistantStreaming(sessionId, removeIfEmpty: false);

        var instruction = SubAgentInterruptedSummaryLead + ChatContextCompressionService.CompressionInstruction;
        _sessionHub.AppendUserMessage(sessionId, instruction);
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = instruction });

        var baseUrl = config.ApiBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/chat/completions";
        var model = ChatCompletionRequestParams.ResolveModel(config, _runContext.ModelOverride);

        var messagesArr = new JsonArray();
        foreach (var m in messages)
            messagesArr.Add(m.DeepClone());

        var requestBody = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messagesArr,
            ["tool_choice"] = "none"
        };
        ChatCompletionRequestParams.ApplyThinking(requestBody, enableThinking);

        using var response = await SendStreamingChatCompletionAsync(http, config, url, requestBody, ct)
            .ConfigureAwait(false);
        var roundResult = await ParseStreamingRoundAsync(response, sessionId, enableThinking, ct)
            .ConfigureAwait(false);

        if (roundResult.ToolCalls is { Count: > 0 })
            return "（子 Agent 停止后未能生成摘要，模型仍尝试调用工具）";

        if (string.IsNullOrWhiteSpace(roundResult.Content)
            || LooksLikeDsmlToolLeak(roundResult.Content))
            return "（子 Agent 停止后未能生成摘要）";

        return ChatContextCompressionService.SlimStoredSummary(roundResult.Content.Trim());
    }

    private static bool LooksLikeDsmlToolLeak(string content) =>
        content.Contains("DSML", StringComparison.OrdinalIgnoreCase)
        && (content.Contains("tool_calls", StringComparison.OrdinalIgnoreCase)
            || content.Contains("invoke name=", StringComparison.OrdinalIgnoreCase));

    private async Task<StreamingRoundResult> ParseStreamingRoundAsync(
        HttpResponseMessage response,
        string sessionId,
        bool enableThinking,
        CancellationToken ct)
    {
        var contentSb = new StringBuilder();
        var reasoningSb = new StringBuilder();
        var nonSseBuffer = new StringBuilder();
        var sawSsePayload = false;
        var toolCallBuilders = new Dictionary<int, SubAgentToolCallBuilder>();
        var toolCallList = new List<SubAgentToolCallInfo>();
        var announcedToolIndices = new HashSet<int>();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                break;
            if (string.IsNullOrEmpty(line))
                continue;

            if (!TryGetSseDataPayload(line, out var data))
            {
                nonSseBuffer.AppendLine(line);
                continue;
            }

            sawSsePayload = true;

            if (string.Equals(data.Trim(), "[DONE]", StringComparison.Ordinal))
                break;

            data = data.TrimStart();

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errEl))
                {
                    var errMsg = errEl.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String
                        ? em.GetString()
                        : errEl.GetRawText();
                    throw new InvalidOperationException($"API 错误: {errMsg}");
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;

                var choice = choices[0];

                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (enableThinking && delta.TryGetProperty("reasoning_content", out var reasoningProp))
                    {
                        var reasoningChunk = reasoningProp.GetString();
                        if (!string.IsNullOrEmpty(reasoningChunk))
                        {
                            _sessionHub.EnsureAssistantStreaming(sessionId, isThinking: true);
                            var reasoningSuffix = ChatCompletionStreamText.AppendSuffix(reasoningSb, reasoningChunk);
                            if (reasoningSuffix is not null)
                                _sessionHub.AppendAssistantStreamDelta(sessionId, null, reasoningSuffix);
                        }
                    }

                    string? textChunk = null;
                    if (delta.TryGetProperty("content", out var contentProp))
                        textChunk = contentProp.GetString();
                    else if (delta.TryGetProperty("text", out var textProp))
                        textChunk = textProp.GetString();

                    if (!string.IsNullOrEmpty(textChunk))
                    {
                        _sessionHub.EnsureAssistantStreaming(sessionId, isThinking: false);
                        var contentSuffix = ChatCompletionStreamText.AppendSuffix(contentSb, textChunk);
                        if (contentSuffix is not null)
                            _sessionHub.AppendAssistantStreamDelta(sessionId, contentSuffix, null);
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCallsDelta))
                    {
                        foreach (var tc in toolCallsDelta.EnumerateArray())
                        {
                            var callIndex = 0;
                            if (tc.TryGetProperty("index", out var indexProp) && indexProp.ValueKind == JsonValueKind.Number)
                                callIndex = indexProp.GetInt32();

                            if (!toolCallBuilders.TryGetValue(callIndex, out var builder))
                            {
                                builder = new SubAgentToolCallBuilder();
                                toolCallBuilders[callIndex] = builder;
                            }

                            if (tc.TryGetProperty("id", out var idProp))
                            {
                                var id = idProp.GetString();
                                if (!string.IsNullOrWhiteSpace(id))
                                    builder.Id = id;
                            }

                            if (tc.TryGetProperty("function", out var funcProp))
                            {
                                if (funcProp.TryGetProperty("name", out var nameProp))
                                {
                                    var fn = nameProp.GetString();
                                    if (!string.IsNullOrWhiteSpace(fn))
                                        builder.Name = fn;
                                }

                                if (funcProp.TryGetProperty("arguments", out var argsProp))
                                    builder.Arguments += argsProp.GetString() ?? "";
                            }

                            if (!announcedToolIndices.Contains(callIndex)
                                && !string.IsNullOrWhiteSpace(builder.Name))
                            {
                                var callId = string.IsNullOrWhiteSpace(builder.Id)
                                    ? $"call_{callIndex}"
                                    : builder.Id;
                                if (_sessionHub.TryBeginStreamingToolCall(sessionId, callId, builder.Name, builder.Arguments))
                                    announcedToolIndices.Add(callIndex);
                            }
                        }
                    }
                }

                string? finishReason = null;
                if (choice.TryGetProperty("finish_reason", out var finishProp))
                    finishReason = finishProp.GetString();

                if (IsStreamingToolFinishReason(finishReason))
                    AppendToolCallsFromBuilders(toolCallBuilders, toolCallList);

                if (choice.TryGetProperty("message", out var messageEl)
                    && messageEl.ValueKind == JsonValueKind.Object
                    && messageEl.TryGetProperty("content", out var msgContentEl))
                {
                    var msgText = msgContentEl.GetString();
                    if (!string.IsNullOrEmpty(msgText))
                    {
                        _sessionHub.EnsureAssistantStreaming(sessionId, isThinking: false);
                        var contentSuffix = ChatCompletionStreamText.AppendSuffix(contentSb, msgText);
                        if (contentSuffix is not null)
                            _sessionHub.AppendAssistantStreamDelta(sessionId, contentSuffix, null);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException)
            {
            }
            catch (InvalidOperationException)
            {
                throw;
            }
        }

        if (!sawSsePayload && nonSseBuffer.Length > 0)
        {
            var fullJson = nonSseBuffer.ToString().Trim();
            try
            {
                using var doc = JsonDocument.Parse(fullJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errEl))
                {
                    var errMsg = errEl.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String
                        ? em.GetString()
                        : errEl.GetRawText();
                    throw new InvalidOperationException($"API 错误: {errMsg}");
                }

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.Object)
                    {
                        if (messageEl.TryGetProperty("content", out var msgContentEl))
                        {
                            var msgText = msgContentEl.GetString();
                            if (!string.IsNullOrEmpty(msgText))
                            {
                                _sessionHub.EnsureAssistantStreaming(sessionId, isThinking: false);
                                var contentSuffix = ChatCompletionStreamText.AppendSuffix(contentSb, msgText);
                                if (contentSuffix is not null)
                                    _sessionHub.AppendAssistantStreamDelta(sessionId, contentSuffix, null);
                            }
                        }

                        if (enableThinking
                            && messageEl.TryGetProperty("reasoning_content", out var msgReasoningEl))
                        {
                            var msgReasoning = msgReasoningEl.GetString();
                            if (!string.IsNullOrEmpty(msgReasoning))
                            {
                                _sessionHub.EnsureAssistantStreaming(sessionId, isThinking: true);
                                var reasoningSuffix = ChatCompletionStreamText.AppendSuffix(reasoningSb, msgReasoning);
                                if (reasoningSuffix is not null)
                                    _sessionHub.AppendAssistantStreamDelta(sessionId, null, reasoningSuffix);
                            }
                        }

                        if (messageEl.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
                        {
                            var fallbackToolIndex = 0;
                            foreach (var tc in toolCallsEl.EnumerateArray())
                            {
                                var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                                if (!tc.TryGetProperty("function", out var fnEl) || fnEl.ValueKind != JsonValueKind.Object)
                                {
                                    fallbackToolIndex++;
                                    continue;
                                }
                                var name = fnEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                                if (string.IsNullOrWhiteSpace(name))
                                {
                                    fallbackToolIndex++;
                                    continue;
                                }
                                var args = fnEl.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() ?? "" : "";
                                toolCallList.Add(new SubAgentToolCallInfo
                                {
                                    Id = string.IsNullOrWhiteSpace(id) ? BuildFallbackToolCallId(fallbackToolIndex) : id!,
                                    Name = name!,
                                    Arguments = args
                                });
                                fallbackToolIndex++;
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        if (toolCallList.Count == 0 && toolCallBuilders.Count > 0)
            AppendToolCallsFromBuilders(toolCallBuilders, toolCallList);

        var finalContent = contentSb.ToString();
        var finalReasoning = reasoningSb.ToString();

        var removeEmptyShell = toolCallList.Count > 0
            && string.IsNullOrWhiteSpace(finalContent)
            && string.IsNullOrWhiteSpace(finalReasoning);
        _sessionHub.EndAssistantStreaming(sessionId, removeEmptyShell);

        return new StreamingRoundResult(finalContent, finalReasoning, toolCallList.Count > 0 ? toolCallList : null);
    }

    private static bool IsStreamingToolFinishReason(string? fr)
    {
        if (string.IsNullOrWhiteSpace(fr))
            return false;
        return fr.Equals("tool_calls", StringComparison.OrdinalIgnoreCase)
               || fr.Equals("function_call", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetSseDataPayload(string line, out string payload)
    {
        payload = "";
        var s = line.TrimStart();
        if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        payload = s.Length <= 5 ? "" : s[5..].TrimStart();
        return true;
    }

    private static void AppendToolCallsFromBuilders(
        Dictionary<int, SubAgentToolCallBuilder> builders,
        List<SubAgentToolCallInfo> target)
    {
        foreach (var kv in builders.OrderBy(x => x.Key))
        {
            var b = kv.Value;
            if (string.IsNullOrWhiteSpace(b.Name))
                continue;
            target.Add(new SubAgentToolCallInfo
            {
                Id = string.IsNullOrWhiteSpace(b.Id) ? BuildFallbackToolCallId(kv.Key) : b.Id,
                Name = b.Name,
                Arguments = b.Arguments
            });
        }
    }

    private static string BuildFallbackToolCallId(int index) => $"call_{Math.Max(0, index)}";

    private static string BuildSubAgentSystemPrompt() =>
        SubAgentCorePrompt + "\n\n" + ChatSystemPrompt.BuildEnvironmentSection();

    private sealed class StreamingRoundResult(string content, string reasoningContent, List<SubAgentToolCallInfo>? toolCalls)
    {
        public string Content { get; } = content;
        public string ReasoningContent { get; } = reasoningContent;
        public List<SubAgentToolCallInfo>? ToolCalls { get; } = toolCalls;
    }

    private sealed class SubAgentToolCallInfo
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string Arguments { get; init; } = string.Empty;
    }

    private sealed class SubAgentToolCallBuilder
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
    }

    private static async Task<HttpResponseMessage> SendStreamingChatCompletionAsync(
        HttpClient http,
        AiSettings config,
        string url,
        JsonObject requestBody,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var body = requestBody.DeepClone() as JsonObject
                       ?? throw new InvalidOperationException("无法构建 API 请求。");
            ChatCompletionStreamOptions.Apply(body, config.ApiBaseUrl);

            var requestJson = body.ToJsonString();
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return response;

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            response.Dispose();

            if (attempt == 0
                && ChatCompletionStreamOptions.TryDisableUsageForBaseUrl(config.ApiBaseUrl, statusCode, errorBody))
                continue;

            throw new InvalidOperationException(
                ChatApiErrorHelper.FormatHttpError(statusCode, errorBody));
        }

        throw new InvalidOperationException("无法完成 API 请求。");
    }
}
