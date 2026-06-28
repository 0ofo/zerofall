using System.Collections.Generic;
using Datafinder.Platform.Services;

namespace Datafinder.Platform.Events;

public record AssetReconResultEvent(
    string SourceName,
    string Query,
    IReadOnlyList<UnifiedAssetRow> Rows,
    int TotalCount);
