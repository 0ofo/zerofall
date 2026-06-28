using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroFall.Platform.Services;

/// <summary>
/// 资产测绘等付费外网操作的用户确认门（通常由 AI 面板的 ask 对话框实现）。
/// </summary>
public interface IReconPaidOperationGate
{
    /// <summary>
    /// 展示确认 UI；返回 true 表示用户同意继续。
    /// </summary>
    Task<bool> ConfirmAsync(string question, IReadOnlyList<string> options, CancellationToken cancellationToken = default);
}
