using CommunityToolkit.Mvvm.ComponentModel;

namespace ZeroFall.Browser.ViewModels;

public sealed partial class HttpIntruderResultViewModel : ObservableObject
{
    public required int Index { get; init; }
    public required string PayloadLabel { get; init; }

    [ObservableProperty]
    private string _status = "—";

    [ObservableProperty]
    private string _length = "—";

    [ObservableProperty]
    private string _latency = "—";

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private string _requestText = string.Empty;

    [ObservableProperty]
    private string _responseText = string.Empty;
}
