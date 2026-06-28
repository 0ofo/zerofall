using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZeroFall.Base.Data;

namespace ZeroFall.Platform.Providers;

/// <summary>
/// 内存中的固定结果集分页（如 SQL 查询结果），不关联数据库。
/// </summary>
public sealed class MemoryPagedDataProvider : IDataProvider
{
    private readonly IReadOnlyList<string> _columns;
    private readonly IReadOnlyList<IReadOnlyList<object?>> _rows;
    private bool _disposed;

    public MemoryPagedDataProvider(string title, IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        Title = title;
        _columns = columns;
        _rows = rows;
    }

    public static MemoryPagedDataProvider FromStringRows(string title, IReadOnlyList<string> columns,
        IReadOnlyList<string[]> rows)
    {
        var asObjects = rows.Select(r => (IReadOnlyList<object?>)r.Select(c => (object?)c).ToList()).ToList();
        return new MemoryPagedDataProvider(title, columns, asObjects);
    }

    public string Title { get; }
    public string DatabasePath => string.Empty;
    public string QuerySource => string.Empty;
    public IReadOnlyList<string> Columns => _columns;
    public bool SupportsTotalCount => true;
    public bool CanEdit => false;
    public bool CanWriteBack => false;
    public bool IsDirty => false;

    public Task<DataPageResult> GetPageAsync(int offset, int limit)
    {
        var slice = new List<IReadOnlyList<object?>>();
        var end = Math.Min(offset + limit, _rows.Count);
        for (var i = offset; i < end; i++)
            slice.Add(_rows[i]);

        return Task.FromResult(new DataPageResult
        {
            Rows = slice,
            Offset = offset,
            Count = slice.Count
        });
    }

    public Task<long> GetTotalCountAsync() => Task.FromResult((long)_rows.Count);

    public Task<int> UpdateRowAsync(long rowId, IReadOnlyList<object?> values) => Task.FromResult(0);

    public Task<int> InsertRowAsync(IReadOnlyList<object?> values) => Task.FromResult(0);

    public Task<int> DeleteRowAsync(long rowId) => Task.FromResult(0);

    public Task WriteBackAsync() => Task.CompletedTask;

    public Task RefreshFromSourceAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
