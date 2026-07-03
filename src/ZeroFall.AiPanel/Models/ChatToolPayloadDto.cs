using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ZeroFall.AiPanel.Models;

public sealed class ChatToolPayloadDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("callId")]
    public string CallId { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public string Args { get; set; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("output")]
    public JsonNode? Output { get; set; }

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}
