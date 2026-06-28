using System.Text.Json.Serialization;

namespace Datafinder.Platform.Models;

public sealed class AiModelEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>模型上下文窗口（token）；API 未返回时由本地表推断，可为 null。</summary>
    [JsonPropertyName("contextTokens")]
    public int? ContextTokens { get; set; }
}
