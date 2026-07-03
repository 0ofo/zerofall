using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using ZeroFall.Dock.Controls;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using ZeroFall.Terminal.ViewModels;

namespace ZeroFall.Terminal.Views;

public partial class TerminalHostView : UserControl, IDockTabToolPanelProvider, INonReloadableTabHost
{
    private StackPanel? _dockToolPanel;
    private PersistTabControl? _terminalTabs;

    public TerminalHostView()
    {
        InitializeComponent();
        _terminalTabs = this.FindControl<PersistTabControl>("PART_TerminalTabs");
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is TerminalHostViewModel vm)
            vm.CopyTextAsync = CopyTextToClipboardAsync;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is TerminalHostViewModel vm)
            vm.CopyTextAsync = null;

        base.OnDetachedFromVisualTree(e);
    }

    private async Task CopyTextToClipboardAsync(string text)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not { } clipboard)
            return;

        await clipboard.SetTextAsync(text);
    }

    public Control? GetDockTabToolPanel()
    {
        _dockToolPanel ??= CreateDockToolPanel();
        _dockToolPanel.DataContext = DataContext;
        return _dockToolPanel;
    }

    public void OnTabBecameVisible()
    {
        _terminalTabs ??= this.FindControl<PersistTabControl>("PART_TerminalTabs");
        if (_terminalTabs?.SelectedItem is not DockTabItemViewModel tab)
            return;

        if (tab.Content is INonReloadableTabShell shell)
            shell.OnTabBecameVisible();
    }

    public void OnTabBecameHidden()
    {
        _terminalTabs ??= this.FindControl<PersistTabControl>("PART_TerminalTabs");
        if (_terminalTabs?.SelectedItem is not DockTabItemViewModel tab)
            return;

        if (tab.Content is INonReloadableTabShell shell)
            shell.OnTabBecameHidden();
    }

    private static StackPanel CreateDockToolPanel()
    {
        var panel = DockTabToolPanelHelper.CreateHorizontalPanel();
        var commandBox = new TextBox
        {
            MinWidth = 100,
            MaxWidth = 180,
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = "命令",
            Classes = { "Small" }
        };
        commandBox.Bind(TextBox.TextProperty, new Binding(nameof(TerminalHostViewModel.CommandInput))
        {
            Mode = BindingMode.TwoWay
        });
        commandBox.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter)
                return;

            if (panel.DataContext is TerminalHostViewModel vm && vm.InsertCommandCommand.CanExecute(null))
                vm.InsertCommandCommand.Execute(null);

            e.Handled = true;
        };
        panel.Children.Add(commandBox);
        DockTabToolPanelHelper.AddIconCommandButton(
            panel,
            "SemiIconSend",
            nameof(TerminalHostViewModel.InsertCommandCommand),
            tooltip: "插入并执行命令");
        DockTabToolPanelHelper.AddIconCommandButton(
            panel,
            "SemiIconRefresh",
            nameof(TerminalHostViewModel.RestartSelectedTerminalCommand),
            tooltip: "重启当前终端");
        DockTabToolPanelHelper.AddIconCommandButton(
            panel,
            "SemiIconCopy",
            nameof(TerminalHostViewModel.ReadSessionCommand),
            tooltip: "读取终端内容并复制（与界面一致）");
        DockTabToolPanelHelper.AddIconCommandButton(
            panel,
            "SemiIconPlus",
            nameof(TerminalHostViewModel.NewTerminalCommand),
            tooltip: "新建终端");
        return panel;
    }
}
