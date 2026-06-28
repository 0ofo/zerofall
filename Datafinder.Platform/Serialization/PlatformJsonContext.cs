using System.Text.Json;
using System.Text.Json.Serialization;
using Datafinder.Platform.Services;

namespace Datafinder.Platform.Serialization;

public sealed class ProxySwitchResultDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("effectiveMode")]
    public string EffectiveMode { get; init; } = "";

    [JsonPropertyName("effectiveEndpoint")]
    public string? EffectiveEndpoint { get; init; }

    [JsonPropertyName("isDegraded")]
    public bool IsDegraded { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UiContextSnapshot))]
[JsonSerializable(typeof(UiLayoutSnapshot))]
[JsonSerializable(typeof(UiLayoutMenuItem))]
[JsonSerializable(typeof(UiLayoutTabItem))]
[JsonSerializable(typeof(UiLayoutMenuSection))]
[JsonSerializable(typeof(UiLayoutSidebarSection))]
[JsonSerializable(typeof(UiLayoutContentSection))]
[JsonSerializable(typeof(UiLayoutBottomSection))]
[JsonSerializable(typeof(UiLayoutRightSection))]
[JsonSerializable(typeof(UiLayoutActiveSection))]
[JsonSerializable(typeof(UiLayoutTabFocus))]
[JsonSerializable(typeof(TerminalUiLayoutExtra))]
[JsonSerializable(typeof(TerminalSessionLayoutItem))]
[JsonSerializable(typeof(BrowserContentTabUiLayoutExtra))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ProxyRuntimeState))]
[JsonSerializable(typeof(ProxySwitchResultDto))]
public partial class PlatformJsonContext : JsonSerializerContext;
