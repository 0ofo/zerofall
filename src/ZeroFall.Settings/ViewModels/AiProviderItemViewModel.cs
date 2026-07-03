using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Platform.Services;

namespace ZeroFall.Settings.ViewModels;

public partial class AiProviderItemViewModel : ObservableObject
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string BaseUrl { get; init; }

    [ObservableProperty]
    private bool _isConfigured;

    public static AiProviderItemViewModel FromPreset(AiProviderPreset preset) => new()
    {
        Id = preset.Id,
        DisplayName = preset.DisplayName,
        BaseUrl = preset.BaseUrl
    };
}
