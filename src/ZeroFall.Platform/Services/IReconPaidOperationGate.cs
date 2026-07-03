using System.Threading;
using System.Threading.Tasks;

namespace ZeroFall.Platform.Services;

/// <summary>
/// 资产测绘等付费外网操作的用户确认门。
/// </summary>
public interface IReconPaidOperationGate
{
    /// <summary>
    /// 展示确认 UI；返回 true 表示用户同意继续。
    /// </summary>
    /// <param name="summary">积分与条数等说明（不含查询语句）。</param>
    /// <param name="query">查询语句，展示在中间输入框。</param>
    Task<bool> ConfirmAsync(string summary, string query, CancellationToken cancellationToken = default);
}
