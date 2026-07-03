using Avalonia.Controls;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Browser.Views;

public partial class HttpReplayView : UserControl, IDockTabToolPanelProvider
{
    private StackPanel? _dockToolPanel;

    public HttpReplayView()
    {
        InitializeComponent();
    }

    public Control? GetDockTabToolPanel()
    {
        _dockToolPanel ??= CreateDockToolPanel();
        _dockToolPanel.DataContext = DataContext;
        return _dockToolPanel;
    }

    private static StackPanel CreateDockToolPanel()
    {
        var panel = DockTabToolPanelHelper.CreateHorizontalPanel(spacing: 6);
        DockTabToolPanelHelper.AddTextCommandButton(
            panel,
            "手动重放",
            nameof(HttpReplayTabViewModel.ReplaySelectedCommand));
        DockTabToolPanelHelper.AddTextCommandButton(
            panel,
            "清空历史",
            nameof(HttpReplayTabViewModel.ClearCommand));
        return panel;
    }
}
