using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ZeroFall.AssetRecon.ViewModels;
using ZeroFall.DataTable.Views;
using ZeroFall.Platform.Registries;

namespace ZeroFall.AssetRecon.Views;

public partial class PortScanView : UserControl, IDockTabToolPanelProvider
{
    private StackPanel? _dockToolPanel;

    public PortScanView()
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
            "开始扫描",
            nameof(PortScanViewModel.StartScanCommand));
        DockTabToolPanelHelper.AddTextCommandButton(
            panel,
            "停止",
            nameof(PortScanViewModel.StopScanCommand));
        DockTabToolPanelHelper.AddTextCommandButton(
            panel,
            "清空结果",
            nameof(PortScanViewModel.ClearResultsCommand));
        return panel;
    }

    private void OnResultsGridLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid dg)
            return;
        DataGridScrollChrome.ApplyDeferred(dg);
        dg.LayoutUpdated += OnResultsGridLayoutUpdated;
    }

    private DateTimeOffset _lastScrollChromeUtc;

    private void OnResultsGridLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not DataGrid dg)
            return;
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastScrollChromeUtc).TotalMilliseconds < 80)
            return;
        _lastScrollChromeUtc = now;
        DataGridScrollChrome.Apply(dg);
    }
}
