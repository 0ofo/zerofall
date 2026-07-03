using CommunityToolkit.Mvvm.ComponentModel;

namespace ZeroFall.Dock.ViewModels;

public partial class TextInputDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _multiline;

    [ObservableProperty]
    private int _selectionEnd;
}
