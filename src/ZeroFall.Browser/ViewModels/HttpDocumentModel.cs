using CommunityToolkit.Mvvm.ComponentModel;

namespace ZeroFall.Browser.ViewModels;

public partial class HttpDocumentModel : ObservableObject
{
    [ObservableProperty]
    private string _requestText = string.Empty;

    [ObservableProperty]
    private string _responseText = string.Empty;

    [ObservableProperty]
    private string _requestContentType = string.Empty;

    [ObservableProperty]
    private string _responseContentType = string.Empty;
}
