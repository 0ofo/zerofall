using System;

namespace ZeroFall.Base.Data;

public sealed class TotalCountEstimateChangedEventArgs : EventArgs
{
    public long NewTotal { get; init; }
}

/// <summary>
/// 由部分 <see cref="IDataProvider"/> 实现：远端合计或缓冲行数变化时通知，用于分页器等 UI 刷新。
/// </summary>
public interface INotifyTotalCountEstimateChanged
{
    event EventHandler<TotalCountEstimateChangedEventArgs>? TotalCountEstimateChanged;
}
