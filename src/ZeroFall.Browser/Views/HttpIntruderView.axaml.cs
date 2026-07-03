using Avalonia.Controls;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Browser.Views;

public partial class HttpIntruderView : UserControl, IDockTabToolPanelProvider
{
    private StackPanel? _dockToolPanel;

    public HttpIntruderView()
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
            "开始攻击",
            nameof(HttpIntruderTabViewModel.StartAttackCommand));
        return panel;
    }
}
