using Avalonia.Controls;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Browser.Views;

public partial class HttpIntruderResultsView : UserControl, IDockTabToolPanelProvider
{
    private StackPanel? _dockToolPanel;

    public HttpIntruderResultsView()
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
        var panel = DockTabToolPanelHelper.CreateHorizontalPanel(spacing: 8);
        DockTabToolPanelHelper.AddTextCommandButton(
            panel,
            "停止",
            nameof(HttpIntruderTabViewModel.StopAttackCommand));
        DockTabToolPanelHelper.AddTextCommandButton(
            panel,
            "清空结果",
            nameof(HttpIntruderTabViewModel.ClearResultsCommand));
        return panel;
    }
}
