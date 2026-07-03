using System;
using System.Collections.Generic;
using System.Text.Json;
using ZeroFall.AiPanel.Models;
using ZeroFall.AiPanel.Serialization;
using ZeroFall.Base.AiTools;

namespace ZeroFall.AiPanel.Services;

/// <summary>聊天消息与 SQLite 行（全局 id + session_id）互转；一行 = 一条逻辑消息。</summary>
public static class ChatHistoryMapper
{
    public static ChatSessionDto ToSession(IReadOnlyList<ChatMessage> messages)
    {
        var dtos = new List<ChatMessageDto>();
        foreach (var message in messages)
        {
            if (message.IsStreaming || message.IsToolRunning || message.IsThinking)
                continue;
            if (TryToDto(message) is { } dto)
                dtos.Add(dto);
        }

        return new ChatSessionDto { Messages = dtos };
    }

    public static bool TryGetPersistableDtos(ChatMessage message, out List<ChatMessageDto> dtos)
    {
        dtos = [];
        if (message.IsStreaming || message.IsThinking)
            return false;
        if (TryToDto(message) is not { } dto)
            return false;

        dtos.Add(dto);
        return true;
    }

    public static bool TryGetStableDtos(ChatMessage message, out List<ChatMessageDto> dtos) =>
        TryGetPersistableDtos(message, out dtos);

    public static IEnumerable<ChatMessage> FromSession(ChatSessionDto? session)
    {
        if (session?.Messages is not { Count: > 0 } list)
            yield break;

        foreach (var message in DeduplicateByMessageId(FromDtoList(list)))
            yield return message;
    }

    /// <summary>按全局 message id 去重，保留首次出现顺序（修复历史错误 merge 产生的重复行）。</summary>
    public static List<ChatMessage> DeduplicateByMessageId(IEnumerable<ChatMessage> messages)
    {
        var seen = new HashSet<long>();
        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            if (message.Id > 0)
            {
                if (!seen.Add(message.Id))
                    continue;
            }

            result.Add(message);
        }

        return result;
    }

    public static IEnumerable<ChatMessage> BuildVisibleShells(IReadOnlyList<ChatMessageDto> dtos)
    {
        foreach (var dto in dtos)
        {
            if (!dto.Visual.IsVisibleInUi())
                continue;

            var message = FromDto(dto);
            if (message is null)
                continue;

            message.IsArchiveShell = true;
            if (message.HasToolCall)
            {
                message.Content = string.Empty;
                ClearToolPayload(message);
            }

            yield return message;
        }
    }

    public static IEnumerable<ChatMessage> FromDtoList(IReadOnlyList<ChatMessageDto> list)
    {
        foreach (var dto in list)
        {
            var message = FromDto(dto);
            if (message is not null)
                yield return message;
        }
    }

    public static IReadOnlyList<ChatMessageDto> FilterFromMessageId(
        IReadOnlyList<ChatMessageDto> dtos,
        long apiStartMessageId)
    {
        if (apiStartMessageId <= 0)
            return dtos;

        var filtered = new List<ChatMessageDto>();
        foreach (var dto in dtos)
        {
            if (dto.Id >= apiStartMessageId)
                filtered.Add(dto);
        }

        return filtered;
    }

    public static int ResolveMessageIndexFromMessageId(IReadOnlyList<ChatMessage> messages, long messageId)
    {
        if (messageId <= 0 || messages.Count == 0)
            return 0;

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Id >= messageId)
                return i;
        }

        return messages.Count;
    }

    public static long GetToolMessageId(IReadOnlyList<ChatMessage> messages, int messageIndex)
    {
        if (messageIndex < 0 || messageIndex >= messages.Count)
            return -1;

        var message = messages[messageIndex];
        return message.HasToolCall ? message.Id : -1;
    }

    public static ChatMessageDto? TryToDto(ChatMessage message)
    {
        if (ShouldSkip(message))
            return null;

        if (message.IsUser)
        {
            return new ChatMessageDto
            {
                Id = message.Id,
                Role = "user",
                Type = ChatMessageDto.TypeText,
                Content = message.Content,
                ContextTokenCount = message.ContextTokenCount,
                Visual = message.Visual
            };
        }

        if (message.HasToolCall)
        {
            return new ChatMessageDto
            {
                Id = message.Id,
                Role = "assistant",
                Type = ChatMessageDto.TypeTool,
                Content = SerializeToolPayload(message),
                ReasoningContent = message.ReasoningContent ?? string.Empty,
                ContextTokenCount = message.ContextTokenCount,
                Visual = message.Visual
            };
        }

        return new ChatMessageDto
        {
            Id = message.Id,
            Role = "assistant",
            Type = ChatMessageDto.TypeBody,
            Content = message.Content,
            ReasoningContent = message.ReasoningContent ?? string.Empty,
            ContentHtml = string.IsNullOrWhiteSpace(message.ContentHtml) ? null : message.ContentHtml,
            ContextTokenCount = message.ContextTokenCount,
            Visual = message.Visual
        };
    }

    private static ChatMessage? FromDto(ChatMessageDto dto)
    {
        var role = dto.Role.ToLowerInvariant();
        if (role == "user")
            return FromUserDto(dto);

        if (string.Equals(dto.EffectiveType, ChatMessageDto.TypeTool, StringComparison.Ordinal))
            return FromToolDto(dto);

        return FromAssistantDto(dto);
    }

    private static ChatMessage? FromUserDto(ChatMessageDto dto)
    {
        var content = dto.ResolveContent();
        if (string.IsNullOrWhiteSpace(content))
            return null;

        return new ChatMessage
        {
            Id = dto.Id,
            Role = ChatRole.User,
            Content = content,
            ContextTokenCount = dto.ContextTokenCount,
            Visual = dto.Visual
        };
    }

    private static ChatMessage? FromAssistantDto(ChatMessageDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Content)
            && string.IsNullOrWhiteSpace(dto.ReasoningContent)
            && string.IsNullOrWhiteSpace(dto.ContentHtml))
            return null;

        var message = new ChatMessage
        {
            Id = dto.Id,
            Role = ChatRole.Assistant,
            Content = dto.Content,
            ReasoningContent = dto.ReasoningContent ?? string.Empty,
            ContextTokenCount = dto.ContextTokenCount,
            Visual = dto.Visual
        };

        if (!string.IsNullOrWhiteSpace(dto.ContentHtml))
            message.RestoreRenderedHtml(dto.ContentHtml);

        return message;
    }

    private static ChatMessage? FromToolDto(ChatMessageDto dto)
    {
        if (!TryDeserializeToolPayload(dto.Content, out var payload))
            return null;

        var message = new ChatMessage
        {
            Id = dto.Id,
            Role = ChatRole.Assistant,
            ToolName = payload.Name,
            ToolCommand = payload.Command,
            ToolArgumentsJson = payload.Args,
            ToolOutput = ToolResultJson.FromPersistedNode(payload.Output),
            ToolExitCode = payload.ExitCode,
            ToolCallId = payload.CallId,
            Content = payload.Body,
            ReasoningContent = string.IsNullOrWhiteSpace(dto.ReasoningContent)
                ? string.Empty
                : dto.ReasoningContent,
            ContextTokenCount = dto.ContextTokenCount,
            Visual = dto.Visual
        };

        return message;
    }

    private static bool ShouldSkip(ChatMessage message)
    {
        if (message.IsUser)
            return string.IsNullOrWhiteSpace(message.Content);

        if (message.HasToolCall)
            return false;

        return string.IsNullOrWhiteSpace(message.Content) && string.IsNullOrWhiteSpace(message.ReasoningContent);
    }

    private static void ClearToolPayload(ChatMessage tool)
    {
        tool.ToolOutput = string.Empty;
        tool.ToolArgumentsJson = string.Empty;
    }

    private static string SerializeToolPayload(ChatMessage message)
    {
        var payload = new ChatToolPayloadDto
        {
            Name = message.ToolName,
            CallId = message.ToolCallId,
            Args = message.ToolArgumentsJson,
            Command = message.ToolCommand,
            Output = ToolResultJson.ToPersistedOutput(message.ToolOutput),
            ExitCode = message.ToolExitCode,
            Body = message.Content
        };

        return JsonSerializer.Serialize(payload, AiPanelJsonContext.Default.ChatToolPayloadDto);
    }

    private static bool TryDeserializeToolPayload(string content, out ChatToolPayloadDto payload)
    {
        payload = new ChatToolPayloadDto();
        if (string.IsNullOrWhiteSpace(content))
            return false;

        try
        {
            payload = JsonSerializer.Deserialize(content, AiPanelJsonContext.Default.ChatToolPayloadDto)
                      ?? new ChatToolPayloadDto();
            return !string.IsNullOrWhiteSpace(payload.Name);
        }
        catch
        {
            return false;
        }
    }
}
