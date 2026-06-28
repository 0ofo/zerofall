using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Datafinder.Base.AiTools;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(List<string>))]
public partial class ListStringJsonContext : JsonSerializerContext;
