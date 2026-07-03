using CommunityToolkit.Mvvm.ComponentModel;

namespace ZeroFall.Dock.ViewModels;

public partial class ConfirmDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _message = string.Empty;
}
