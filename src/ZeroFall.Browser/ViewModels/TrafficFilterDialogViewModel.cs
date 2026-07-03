using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZeroFall.Browser.ViewModels;

public sealed partial class TrafficFilterDialogViewModel : ObservableObject
{
    private readonly TrafficMonitorTabViewModel _monitor;
    private TrafficFilterSpec _snapshot;

    [ObservableProperty]
    private bool _showOnlyInScope;

    [ObservableProperty]
    private bool _hideWithoutResponse;

    [ObservableProperty]
    private bool _showOnlyParameterized;

    [ObservableProperty]
    private bool _mimeHtml = true;

    [ObservableProperty]
    private bool _mimeOtherText = true;

    [ObservableProperty]
    private bool _mimeScript = true;

    [ObservableProperty]
    private bool _mimeImages;

    [ObservableProperty]
    private bool _mimeXml = true;

    [ObservableProperty]
    private bool _mimeFlash = true;

    [ObservableProperty]
    private bool _mimeCss;

    [ObservableProperty]
    private bool _mimeOtherBinary = true;

    [ObservableProperty]
    private bool _status2xx = true;

    [ObservableProperty]
    private bool _status3xx = true;

    [ObservableProperty]
    private bool _status4xx = true;

    [ObservableProperty]
    private bool _status5xx = true;

    [ObservableProperty]
    private string _searchTerm = string.Empty;

    [ObservableProperty]
    private bool _searchRegex;

    [ObservableProperty]
    private bool _searchCaseSensitive;

    [ObservableProperty]
    private bool _searchNegative;

    [ObservableProperty]
    private bool _extensionShowOnlyEnabled;

    [ObservableProperty]
    private string _extensionShowOnly = "asp,aspx,jsp,php";

    [ObservableProperty]
    private bool _extensionHideEnabled = true;

    [ObservableProperty]
    private string _extensionHide = "png,ico,css,woff,woff2,ttf,svg";

    [ObservableProperty]
    private bool _showOnlyWithNotes;

    [ObservableProperty]
    private bool _showOnlyHighlighted;

    [ObservableProperty]
    private string _listenerPort = string.Empty;

    public TrafficFilterDialogViewModel(TrafficMonitorTabViewModel monitor)
    {
        _monitor = monitor;
        LoadFrom(monitor.AppliedFilter);
        _snapshot = ToSpec();
    }

    public void LoadFrom(TrafficFilterSpec spec)
    {
        ShowOnlyInScope = spec.ShowOnlyInScope;
        HideWithoutResponse = spec.HideWithoutResponse;
        ShowOnlyParameterized = spec.ShowOnlyParameterized;
        MimeHtml = spec.MimeHtml;
        MimeOtherText = spec.MimeOtherText;
        MimeScript = spec.MimeScript;
        MimeImages = spec.MimeImages;
        MimeXml = spec.MimeXml;
        MimeFlash = spec.MimeFlash;
        MimeCss = spec.MimeCss;
        MimeOtherBinary = spec.MimeOtherBinary;
        Status2xx = spec.Status2xx;
        Status3xx = spec.Status3xx;
        Status4xx = spec.Status4xx;
        Status5xx = spec.Status5xx;
        SearchTerm = spec.SearchTerm;
        SearchRegex = spec.SearchRegex;
        SearchCaseSensitive = spec.SearchCaseSensitive;
        SearchNegative = spec.SearchNegative;
        ExtensionShowOnlyEnabled = spec.ExtensionShowOnlyEnabled;
        ExtensionShowOnly = spec.ExtensionShowOnly;
        ExtensionHideEnabled = spec.ExtensionHideEnabled;
        ExtensionHide = spec.ExtensionHide;
        ShowOnlyWithNotes = spec.ShowOnlyWithNotes;
        ShowOnlyHighlighted = spec.ShowOnlyHighlighted;
        ListenerPort = spec.ListenerPort;
    }

    public TrafficFilterSpec ToSpec() =>
        new()
        {
            ShowOnlyInScope = ShowOnlyInScope,
            HideWithoutResponse = HideWithoutResponse,
            ShowOnlyParameterized = ShowOnlyParameterized,
            MimeHtml = MimeHtml,
            MimeOtherText = MimeOtherText,
            MimeScript = MimeScript,
            MimeImages = MimeImages,
            MimeXml = MimeXml,
            MimeFlash = MimeFlash,
            MimeCss = MimeCss,
            MimeOtherBinary = MimeOtherBinary,
            Status2xx = Status2xx,
            Status3xx = Status3xx,
            Status4xx = Status4xx,
            Status5xx = Status5xx,
            SearchTerm = SearchTerm,
            SearchRegex = SearchRegex,
            SearchCaseSensitive = SearchCaseSensitive,
            SearchNegative = SearchNegative,
            ExtensionShowOnlyEnabled = ExtensionShowOnlyEnabled,
            ExtensionShowOnly = ExtensionShowOnly,
            ExtensionHideEnabled = ExtensionHideEnabled,
            ExtensionHide = ExtensionHide,
            ShowOnlyWithNotes = ShowOnlyWithNotes,
            ShowOnlyHighlighted = ShowOnlyHighlighted,
            ListenerPort = ListenerPort
        };

    [RelayCommand]
    private void ShowAll()
    {
        MimeHtml = true;
        MimeOtherText = true;
        MimeScript = true;
        MimeImages = true;
        MimeXml = true;
        MimeFlash = true;
        MimeCss = true;
        MimeOtherBinary = true;
        Status2xx = true;
        Status3xx = true;
        Status4xx = true;
        Status5xx = true;
    }

    [RelayCommand]
    private void HideAll()
    {
        MimeHtml = false;
        MimeOtherText = false;
        MimeScript = false;
        MimeImages = false;
        MimeXml = false;
        MimeFlash = false;
        MimeCss = false;
        MimeOtherBinary = false;
        Status2xx = false;
        Status3xx = false;
        Status4xx = false;
        Status5xx = false;
    }

    [RelayCommand]
    private void RevertChanges() => LoadFrom(_snapshot);

    [RelayCommand]
    private async Task Apply()
    {
        _monitor.AppliedFilter = ToSpec();
        await _monitor.ApplyFilterAsync();
        _snapshot = ToSpec();
    }

    [RelayCommand]
    private async Task ApplyAndClose(object? parameter)
    {
        await Apply();
        if (parameter is Avalonia.Controls.Window window)
            window.Close(true);
    }

    [RelayCommand]
    private void Cancel(object? parameter)
    {
        if (parameter is Avalonia.Controls.Window window)
            window.Close(false);
    }
}
