using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZeroFall.Base.Data;
using ZeroFall.Platform.Services;

namespace ZeroFall.AssetRecon.Providers;

/// <summary>
/// 只读：从本地库按 <c>query_task_id</c> 分页还原某次测绘写入的结果行（与 <see cref="AssetReconPagedDataProvider"/> 列序一致）。
/// </summary>
public sealed class AssetReconHistoricalResultsProvider : IDataProvider
{
    private static readonly string[] SqlColumns =
    {
        "source", "ip", "port", "protocol", "title", "domain", "country",
        "province", "city", "os", "server", "icp", "link", "status_code"
    };

    private readonly ISqliteService _sqliteService;
    private readonly string _dbPath;
    private readonly string _queryTaskId;
    private bool _disposed;

    public AssetReconHistoricalResultsProvider(
        ISqliteService sqliteService,
        string dbPath,
        string queryTaskId,
        string queryLabelForTitle)
    {
        _sqliteService = sqliteService;
        _dbPath = dbPath;
        _queryTaskId = queryTaskId;
        var q = queryLabelForTitle.Trim();
        Title = q.Length > 40 ? q[..40] + "…" : q;
    }

    public string Title { get; }

    public string DatabasePath => _dbPath;

    public string QuerySource => "asset_recon_results";

    public IReadOnlyList<string> Columns => AssetReconPagedDataProvider.ColumnHeaders;

    public bool SupportsTotalCount => true;

    public bool CanEdit => false;

    public bool CanWriteBack => false;

    public bool IsDirty => false;

    public async Task<DataPageResult> GetPageAsync(int offset, int limit)
    {
        var endOrder = offset + limit - 1;
        var cols = string.Join(", ", SqlColumns.Select(c => $"\"{c}\""));
        var sql = $"SELECT {cols} FROM \"asset_recon_results\" " +
                  $"WHERE query_task_id = '{_queryTaskId}' " +
                  $"AND sort_order >= {offset} AND sort_order <= {endOrder} " +
                  "ORDER BY sort_order";

        var result = await _sqliteService.ExecuteQueryAsync(_dbPath, sql).ConfigureAwait(false);
        return new DataPageResult
        {
            Rows = result.Rows,
            Offset = offset,
            Count = (int)result.RowCount
        };
    }

    public async Task<long> GetTotalCountAsync()
    {
        var sql = $"SELECT COUNT(*) AS c FROM \"asset_recon_results\" WHERE query_task_id = '{_queryTaskId}'";
        var result = await _sqliteService.ExecuteQueryAsync(_dbPath, sql).ConfigureAwait(false);
        if (result.Rows.Count == 0 || !string.IsNullOrEmpty(result.Error))
            return 0;
        return Convert.ToInt64(result.Rows[0][0] ?? 0L);
    }

    public Task<int> UpdateRowAsync(long rowId, IReadOnlyList<object?> values) => Task.FromResult(0);

    public Task<int> InsertRowAsync(IReadOnlyList<object?> values) => Task.FromResult(0);

    public Task<int> DeleteRowAsync(long rowId) => Task.FromResult(0);

    public Task WriteBackAsync() => Task.CompletedTask;

    public Task RefreshFromSourceAsync()
    {
        // 只读本地库：刷新由 DataTableViewModel 重新 GetTotalCount / GetPage 完成。
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
