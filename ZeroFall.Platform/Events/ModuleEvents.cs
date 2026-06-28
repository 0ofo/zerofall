using System.Collections.Generic;
using ZeroFall.Platform.Services;

namespace ZeroFall.Platform.Events;

public record AssetReconResultEvent(
    string SourceName,
    string Query,
    IReadOnlyList<UnifiedAssetRow> Rows,
    int TotalCount);
