using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using ZeroFall.Browser.ViewModels;

namespace ZeroFall.Browser.Views;

/// <summary>HTTP 流量详情 Flyout（网络监控表、网站树共用）。</summary>
internal sealed class TrafficHttpDetailFlyoutHost
{
    public const string HttpDetailFlyoutPresenterClass = "TrafficHttpDetailFlyoutPresenter";
    public const double HttpDetailFlyoutWidth = 720;
    public const double HttpDetailFlyoutHeight = 360;

    private const double HttpDetailFlyoutHostLayoutInset = 6;

    private readonly TrafficMonitorTabViewModel _vm;
    private Flyout? _flyout;
    private TrafficDetailView? _detailView;
    private Border? _host;

    public TrafficHttpDetailFlyoutHost(TrafficMonitorTabViewModel vm) => _vm = vm;

    public void Hide() => _flyout?.Hide();

    public void Sync(Control anchor, bool showAtPointer)
    {
        if (!anchor.IsLoaded)
            return;

        var entry = _vm.SelectedEntry;
        if (entry is null)
        {
            Hide();
            return;
        }

        EnsureFlyout();
        if (_flyout is null || _host is null)
            return;

        var innerW = HttpDetailFlyoutWidth - HttpDetailFlyoutHostLayoutInset;
        var innerH = HttpDetailFlyoutHeight - HttpDetailFlyoutHostLayoutInset;
        _host.MinWidth = innerW;
        _host.MinHeight = innerH;
        _host.MaxWidth = innerW;
        _host.MaxHeight = innerH;
        _host.Width = innerW;
        _host.Height = innerH;

        _flyout.ShowAt(anchor, showAtPointer);
        _detailView?.RefreshFromSelectedEntry();
        Dispatcher.UIThread.Post(
            () => _detailView?.RefreshFromSelectedEntry(),
            DispatcherPriority.Loaded);
    }

    private void EnsureFlyout()
    {
        if (_flyout is not null)
            return;

        _detailView = new TrafficDetailView { DataContext = _vm };
        _host = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            ClipToBounds = true,
            Child = _detailView
        };

        _flyout = new Flyout
        {
            Content = _host,
            Placement = PlacementMode.Top,
            ShowMode = FlyoutShowMode.Transient
        };
        _flyout.FlyoutPresenterClasses.Add(HttpDetailFlyoutPresenterClass);
    }
}
