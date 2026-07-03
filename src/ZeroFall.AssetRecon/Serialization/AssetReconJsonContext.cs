using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroFall.AssetRecon.Models;

namespace ZeroFall.AssetRecon.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(FofaApiResponse))]
[JsonSerializable(typeof(HunterApiResponse))]
[JsonSerializable(typeof(QuakeApiResponse))]
[JsonSerializable(typeof(ShodanApiResponse))]
internal partial class AssetReconJsonContext : JsonSerializerContext;
