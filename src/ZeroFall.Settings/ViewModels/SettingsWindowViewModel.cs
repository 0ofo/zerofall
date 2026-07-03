using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Base.Mvvm;

namespace ZeroFall.Settings.ViewModels;

public partial class SettingsWindowViewModel : ViewModelBase
{
    private readonly Action? _onClose;

    [ObservableProperty]
    private string _footerStatus = string.Empty;

    [ObservableProperty]
    private bool _footerIsError;

    public bool SkipSaveOnClose { get; set; }

    public string? TargetTabTitle { get; }

    public SettingsWindowViewModel(Action? onClose = null, string? targetTabTitle = null)
    {
        _onClose = onClose;
        TargetTabTitle = targetTabTitle;
    }

    public void SetFooter(string message, bool isError)
    {
        FooterStatus = message;
        FooterIsError = isError;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Cancel()
    {
        _onClose?.Invoke();
    }
}
