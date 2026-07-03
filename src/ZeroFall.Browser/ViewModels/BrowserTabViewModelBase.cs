using CommunityToolkit.Mvvm.ComponentModel;

namespace ZeroFall.Browser.ViewModels;

public abstract partial class BrowserTabViewModelBase : ObservableObject
{
    [ObservableProperty]
    private string _title = "标签页";
}
