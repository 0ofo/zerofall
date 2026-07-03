using Avalonia.Controls;
using ZeroFall.Settings.ViewModels;

namespace ZeroFall.Settings;

/// <summary>设置窗口中的一个标签页（构建时缓存，避免 Tab 模板卸掉 Content 后找不到 ViewModel）。</summary>
public sealed class SettingsTabEntry
{
    public required string Title { get; init; }

    public required Control Root { get; init; }

    public ISettingsSaveable? Saveable { get; init; }
}
