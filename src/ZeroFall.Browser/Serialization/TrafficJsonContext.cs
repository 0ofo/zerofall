using System.Text.Json.Serialization;

namespace ZeroFall.Browser.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(TrafficSelectionPayloadDto))]
internal partial class TrafficJsonContext : JsonSerializerContext;
