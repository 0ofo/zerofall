using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ZeroFall.Base.AiTools;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonValue))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
public partial class ToolResultJsonContext : JsonSerializerContext;
