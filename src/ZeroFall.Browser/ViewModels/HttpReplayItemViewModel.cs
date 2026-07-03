using CommunityToolkit.Mvvm.ComponentModel;

namespace ZeroFall.Browser.ViewModels;

public sealed partial class HttpReplayItemViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string Time { get; init; }
    public required string SourceEntryId { get; init; }
    public required string Method { get; init; }
    public required string OriginalUrl { get; init; }

    [ObservableProperty]
    private string _status = "待重放";

    [ObservableProperty]
    private string _resultSummary = "已预填，等待手动重放";

    [ObservableProperty]
    private string _requestText = string.Empty;

    [ObservableProperty]
    private string _responseText = string.Empty;

    [ObservableProperty]
    private string _realHost = string.Empty;

    [ObservableProperty]
    private bool _isHttps;

    [ObservableProperty]
    private string _responseLatencyText = "—";
}
