using CommunityToolkit.Mvvm.ComponentModel;

namespace ZeroFall.Settings.ViewModels;

public partial class AiModelRowViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;

    /// <summary>API 返回的上下文大小；仅展示用。</summary>
    public int? ApiContextTokens { get; init; }

    /// <summary>用户填写的上下文（tokens）；0 表示保存为 null，运行时按模型名推断或默认 100k。</summary>
    [ObservableProperty]
    private int _contextTokens;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _alreadyInCatalog;

    partial void OnAlreadyInCatalogChanged(bool value) => OnPropertyChanged(nameof(FetchedHint));

    public string FetchedHint =>
        AlreadyInCatalog
            ? "已添加"
            : ApiContextTokens is > 0
                ? $"上下文 {ApiContextTokens:N0}"
                : "上下文未知";
}
