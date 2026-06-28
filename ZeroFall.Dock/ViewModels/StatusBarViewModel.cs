using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Events;

namespace ZeroFall.Dock.ViewModels;

public partial class StatusBarViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    public StatusBarViewModel(IEventBus eventBus)
    {
        SubscribeEvent(eventBus, (StatusMessageEvent e) => StatusMessage = e.Message);
    }
}
