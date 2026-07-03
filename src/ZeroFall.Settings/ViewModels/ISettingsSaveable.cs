namespace ZeroFall.Settings.ViewModels;

/// <summary>设置页 ViewModel：由设置窗口「保存」按钮调用。</summary>
public interface ISettingsSaveable
{
    /// <summary>将当前 UI 状态写入 <see cref="Platform.Services.ISettingsService"/>。</summary>
    /// <returns>是否成功（校验失败或磁盘写入失败时为 false）。</returns>
    bool TrySave();

    /// <summary>最近一次 <see cref="TrySave"/> 失败时的说明（成功时为 null 或空）。</summary>
    string? LastSaveError { get; }
}
