using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ZeroFall.Browser.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PageNavigateParams))]
[JsonSerializable(typeof(PageReloadParams))]
[JsonSerializable(typeof(NetworkGetCookiesParams))]
[JsonSerializable(typeof(EmulationSetDeviceMetricsOverrideParams))]
[JsonSerializable(typeof(EmulationSetEmulatedMediaParams))]
[JsonSerializable(typeof(EmulatedMediaFeature))]
[JsonSerializable(typeof(PageNavigateToHistoryEntryParams))]
[JsonSerializable(typeof(RuntimeEvaluateParams))]
[JsonSerializable(typeof(DomGetDocumentParams))]
[JsonSerializable(typeof(DomGetOuterHtmlParams))]
[JsonSerializable(typeof(PageSetDocumentContentParams))]
[JsonSerializable(typeof(HttpFetchConfig))]
[JsonSerializable(typeof(BrowserTabListItemDto))]
[JsonSerializable(typeof(BrowserTabListResponseDto))]
[JsonSerializable(typeof(List<BrowserTabListItemDto>))]
[JsonSerializable(typeof(string))]
internal partial class BrowserJsonContext : JsonSerializerContext;
