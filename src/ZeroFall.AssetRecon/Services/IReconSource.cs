using System.Collections.Generic;
using System.Threading.Tasks;

namespace ZeroFall.AssetRecon.Services;

public interface IReconSource
{
    string Name { get; }
    string DisplayName { get; }
    bool IsConfigured { get; }
    int MaxPageSize { get; }
    string TranslateQuery(UnifiedQuery query);
    Task<ReconResult> QueryAsync(string target, int page = 1, int pageSize = 100);
}

public class ReconResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public List<UnifiedAssetRow> Rows { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool HasMore => Page * PageSize < TotalCount;
}
