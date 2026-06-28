using Datafinder.Platform.Models;

namespace Datafinder.Platform.Services;

public interface ISettingsService
{
    AppSettings Load();
    bool Save(AppSettings settings);
    void InvalidateCache();
    string? LastError { get; }
    /// <summary>最近一次成功读写的配置目录；未解析时为 null。</summary>
    string? SettingsDirectory { get; }
}
