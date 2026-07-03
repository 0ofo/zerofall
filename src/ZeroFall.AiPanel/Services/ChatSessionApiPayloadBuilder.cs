using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ZeroFall.AiPanel.Models;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Services;

/// <summary>从 SQLite 重查会话消息并构建 API 载荷（system + messages）。</summary>
public sealed class ChatSessionApiPayloadBuilder
{
    private readonly IAiChatSessionStore _sessionStore;

    public ChatSessionApiPayloadBuilder(IAiChatSessionStore sessionStore) =>
        _sessionStore = sessionStore;

    public async Task<ApiPayloadBuildResult?> BuildFromDatabaseAsync(ApiPayloadBuildRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return null;

        var data = await _sessionStore.LoadSessionAsync(request.SessionId).ConfigureAwait(false);
        if (data is null)
            return null;

        var messages = ChatHistoryMapper
            .FromSession(new ChatSessionDto { Messages = data.Messages })
            .ToList();

        var apiStartMessageId = data.ApiStartMessageId;

        if (request.CompressionRound && request.CompressionPrep is { } prep)
        {
            var apiStart = apiStartMessageId > 0
                ? ChatHistoryMapper.ResolveMessageIndexFromMessageId(messages, apiStartMessageId)
                : 0;
            var end = Math.Min(prep.CompressUserIndex + 1, messages.Count);
            messages = end > apiStart
                ? messages.Skip(apiStart).Take(end - apiStart).ToList()
                : [];
            apiStartMessageId = 0;
        }

        var modelId = !string.IsNullOrWhiteSpace(request.ModelId) ? request.ModelId : request.Config.Model;
        var contextTokens = AiEndpointCatalog.ResolveContextTokens(request.Config, request.Config.ApiBaseUrl, modelId);
        var nodes = ChatContextCompressionService.BuildApiMessageNodes(
            messages,
            request.SystemPrompt,
            modelId,
            contextTokens,
            apiStartMessageId);

        return new ApiPayloadBuildResult(nodes, messages, apiStartMessageId);
    }
}

public sealed record ApiPayloadBuildRequest(
    string SessionId,
    AiSettings Config,
    string ModelId,
    string SystemPrompt,
    bool CompressionRound = false,
    CompressionRoundPrep? CompressionPrep = null);

public sealed record CompressionRoundPrep(int CompressUserIndex);

public sealed record ApiPayloadBuildResult(
    List<JsonNode> ApiMessages,
    IReadOnlyList<ChatMessage> MappedMessages,
    long ApiStartMessageId);
