using CommunityToolkit.Mvvm.ComponentModel;

namespace ZeroFall.AiPanel.ViewModels;

public partial class ReconPaidConfirmDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _query = string.Empty;
}
