using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Services;

namespace ZeroFall.Settings.ViewModels;

public partial class GeneralSettingsViewModel : ViewModelBase, ISettingsSaveable
{
    private readonly ISettingsService _settingsService;

    private string? _lastSaveError;

    public string? LastSaveError => _lastSaveError;

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private int _languageIndex;

    [ObservableProperty]
    private bool _autoOpenLastProject;

    [ObservableProperty]
    private bool _minimizeToTray;

    public GeneralSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadConfig();
    }

    private void LoadConfig()
    {
        var settings = _settingsService.Load();
        var config = settings.General;
        ThemeIndex = config.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
        LanguageIndex = config.Language == "en" ? 1 : 0;
        AutoOpenLastProject = config.AutoOpenLastProject;
        MinimizeToTray = config.MinimizeToTray;
    }

    public bool TrySave() => SaveCore();

    [RelayCommand]
    private void Save() => SaveCore();

    private bool SaveCore()
    {
        _lastSaveError = null;
        try
        {
            var settings = _settingsService.Load();
            settings.General.Theme = ThemeIndex switch
            {
                1 => "light",
                2 => "dark",
                _ => "system"
            };
            settings.General.Language = LanguageIndex == 1 ? "en" : "zh-CN";
            settings.General.AutoOpenLastProject = AutoOpenLastProject;
            settings.General.MinimizeToTray = MinimizeToTray;
            if (_settingsService.Save(settings))
                return true;

            _lastSaveError = _settingsService.LastError ?? "常规设置保存失败";
            System.Diagnostics.Debug.WriteLine($"[GeneralSettings] Save failed: {_lastSaveError}");
            return false;
        }
        catch (Exception ex)
        {
            _lastSaveError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[GeneralSettings] Save failed: {ex}");
            return false;
        }
    }
}
