using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(ChatSessionDto))]
[JsonSerializable(typeof(ChatMessageDto))]
[JsonSerializable(typeof(List<ChatMessageDto>))]
[JsonSerializable(typeof(ChatToolPayloadDto))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(SessionTokenUsageState))]
public partial class AiPanelJsonContext : JsonSerializerContext;
