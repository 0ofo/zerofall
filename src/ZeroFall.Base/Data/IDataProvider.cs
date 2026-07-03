using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ZeroFall.Base.Data;

public interface IDataProvider : IDisposable
{
    string Title { get; }
    string DatabasePath { get; }
    string QuerySource { get; }
    IReadOnlyList<string> Columns { get; }
    bool SupportsTotalCount { get; }
    bool CanEdit { get; }
    bool CanWriteBack { get; }
    bool IsDirty { get; }

    Task<DataPageResult> GetPageAsync(int offset, int limit);
    Task<long> GetTotalCountAsync();
    Task<int> UpdateRowAsync(long rowId, IReadOnlyList<object?> values);
    Task<int> InsertRowAsync(IReadOnlyList<object?> values);
    Task<int> DeleteRowAsync(long rowId);
    Task WriteBackAsync();
    Task RefreshFromSourceAsync();
}

public class DataPageResult
{
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = Array.Empty<IReadOnlyList<object?>>();
    public int Offset { get; init; }
    public int Count { get; init; }
}

public enum DataTableDisplayMode
{
    VirtualScroll,
    UserPaged,
    /// <summary>
    /// 内存实时列表（如 HTTP 流量），可选文本筛选，无 IDataProvider。
    /// </summary>
    LiveCollection
}
