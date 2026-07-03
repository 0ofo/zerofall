using Avalonia.Controls;
using Avalonia.Interactivity;
using ZeroFall.Settings.Helpers;
using ZeroFall.Settings.ViewModels;

namespace ZeroFall.Settings.Views.Settings;

public partial class McpSettingsView : UserControl
{
    public McpSettingsView()
    {
        InitializeComponent();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not McpSettingsViewModel vm)
            return;

        SettingsBindingHelper.CommitPendingEdits(this);
        vm.TrySave();
    }

    private void OnTestClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not McpSettingsViewModel vm)
            return;

        SettingsBindingHelper.CommitPendingEdits(this);
        if (vm.TestSelectedServerCommand.CanExecute(null))
            vm.TestSelectedServerCommand.Execute(null);
    }
}
