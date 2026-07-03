using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.Settings.ViewModels;

public partial class TerminalSettingsViewModel : ViewModelBase, ISettingsSaveable
{
    private readonly ISettingsService _settingsService;

    private string? _lastSaveError;

    public string? LastSaveError => _lastSaveError;

    [ObservableProperty]
    private string _shellPath = string.Empty;

    [ObservableProperty]
    private string _fontFamily = "SimHei, SimSun, NSimSun, monospace";

    [ObservableProperty]
    private int _fontSize = 13;

    public TerminalSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadConfig();
    }

    private void LoadConfig()
    {
        var config = _settingsService.Load().Terminal;
        ShellPath = config.ShellPath;
        FontFamily = config.FontFamily;
        FontSize = config.FontSize;
    }

    public bool TrySave() => SaveCore();

    [RelayCommand]
    private void Save() => SaveCore();

    private bool SaveCore()
    {
        try
        {
            var settings = _settingsService.Load();
            settings.Terminal.ShellPath = ShellPath.Trim();
            settings.Terminal.FontFamily = FontFamily.Trim();
            settings.Terminal.FontSize = FontSize;
            if (_settingsService.Save(settings))
                return true;

            _lastSaveError = _settingsService.LastError ?? "终端设置保存失败";
            return false;
        }
        catch (Exception ex)
        {
            _lastSaveError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[TerminalSettings] Save failed: {ex}");
            return false;
        }
    }
}
