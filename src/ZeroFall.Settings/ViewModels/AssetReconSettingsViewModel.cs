using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.Settings.ViewModels;

public partial class AssetReconSettingsViewModel : ViewModelBase, ISettingsSaveable
{
    private readonly ISettingsService _settingsService;

    [ObservableProperty] private bool _fofaEnabled;
    [ObservableProperty] private string _fofaEmail = string.Empty;
    [ObservableProperty] private string _fofaKey = string.Empty;
    [ObservableProperty] private string _fofaBaseUrl = "https://fofa.info";

    [ObservableProperty] private bool _hunterEnabled;
    [ObservableProperty] private string _hunterKey = string.Empty;
    [ObservableProperty] private string _hunterBaseUrl = "https://hunter.qianxin.com";

    [ObservableProperty] private bool _quakeEnabled;
    [ObservableProperty] private string _quakeKey = string.Empty;
    [ObservableProperty] private string _quakeBaseUrl = "https://quake.360.net";

    [ObservableProperty] private bool _shodanEnabled;
    [ObservableProperty] private string _shodanKey = string.Empty;
    [ObservableProperty] private string _shodanBaseUrl = "https://api.shodan.io";

    [ObservableProperty] private string _statusMessage = string.Empty;

    public string? LastSaveError => string.IsNullOrWhiteSpace(StatusMessage) || StatusMessage == "保存成功"
        ? null
        : StatusMessage;

    public AssetReconSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadConfig();
    }

    private void LoadConfig()
    {
        var config = _settingsService.Load().AssetRecon;
        FofaEnabled = config.FofaEnabled;
        FofaEmail = config.FofaEmail;
        FofaKey = config.FofaKey;
        FofaBaseUrl = config.FofaBaseUrl;

        HunterEnabled = config.HunterEnabled;
        HunterKey = config.HunterKey;
        HunterBaseUrl = config.HunterBaseUrl;

        QuakeEnabled = config.QuakeEnabled;
        QuakeKey = config.QuakeKey;
        QuakeBaseUrl = config.QuakeBaseUrl;

        ShodanEnabled = config.ShodanEnabled;
        ShodanKey = config.ShodanKey;
        ShodanBaseUrl = config.ShodanBaseUrl;
    }

    public bool TrySave() => SaveCore();

    [RelayCommand]
    private void Save() => SaveCore();

    private bool SaveCore()
    {
        try
        {
            var settings = _settingsService.Load();
            settings.AssetRecon.FofaEnabled = FofaEnabled;
            settings.AssetRecon.FofaEmail = FofaEmail;
            settings.AssetRecon.FofaKey = FofaKey;
            settings.AssetRecon.FofaBaseUrl = FofaBaseUrl;
            settings.AssetRecon.HunterEnabled = HunterEnabled;
            settings.AssetRecon.HunterKey = HunterKey;
            settings.AssetRecon.HunterBaseUrl = HunterBaseUrl;
            settings.AssetRecon.QuakeEnabled = QuakeEnabled;
            settings.AssetRecon.QuakeKey = QuakeKey;
            settings.AssetRecon.QuakeBaseUrl = QuakeBaseUrl;
            settings.AssetRecon.ShodanEnabled = ShodanEnabled;
            settings.AssetRecon.ShodanKey = ShodanKey;
            settings.AssetRecon.ShodanBaseUrl = ShodanBaseUrl;

            if (_settingsService.Save(settings))
            {
                StatusMessage = "保存成功";
                return true;
            }

            StatusMessage = _settingsService.LastError ?? "保存失败";
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
            return false;
        }
    }
}
