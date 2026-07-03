using Avalonia.Controls;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Browser.Views;

public partial class HttpReplayHistoryView : UserControl, IDockTabToolPanelProvider
{
    private StackPanel? _dockToolPanel;

    public HttpReplayHistoryView()
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
        var panel = DockTabToolPanelHelper.CreateHorizontalPanel();
        DockTabToolPanelHelper.AddIconCommandButton(
            panel,
            "SemiIconDelete",
            nameof(HttpReplayTabViewModel.ClearCommand),
            tooltip: "清空重放历史");
        return panel;
    }
}
