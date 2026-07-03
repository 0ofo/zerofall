using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZeroFall.AssetRecon.ViewModels;

namespace ZeroFall.AssetRecon.Views;

public partial class AssetReconLeftPanelView : UserControl
{
    public AssetReconLeftPanelView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        HistoryList.AddHandler(InputElement.PointerPressedEvent, OnHistoryPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (this.FindControl<TextBox>("QueryBox") is { } tb)
            tb.AddHandler(InputElement.KeyDownEvent, OnQueryBoxKeyDown, RoutingStrategies.Tunnel);

        if (DataContext is AssetReconPanelHostViewModel host)
            host.Recon.EnsureQuotaColumnsLoaded();
    }

    private void OnQueryBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;
        if (DataContext is not AssetReconPanelHostViewModel { Recon: { } recon })
            return;
        if (!recon.QueryCommand.CanExecute(null))
            return;

        recon.QueryCommand.Execute(null);
        e.Handled = true;
    }

    private void OnHistoryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var task = GetTaskFromPointer(e);
        if (task is null)
            return;

        var point = e.GetCurrentPoint(HistoryList);
        if (point.Properties.IsRightButtonPressed)
        {
            HistoryList.SelectedItem = task;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;
        if (DataContext is not AssetReconPanelHostViewModel { History: { } history })
            return;

        history.OpenHistoryResultsCommand.Execute(task);
    }

    private async void OnDeleteMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssetReconPanelHostViewModel { History: { } history })
            return;

        var task = GetTaskFromContextMenu(sender) ?? HistoryList.SelectedItem as SearchTaskItem;
        if (task is null)
            return;

        await history.DeleteTaskCommand.ExecuteAsync(task);
    }

    private SearchTaskItem? GetTaskFromContextMenu(object? sender)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: Control target } })
            return null;

        return GetTaskFromControl(target);
    }

    private SearchTaskItem? GetTaskFromPointer(PointerEventArgs e)
    {
        if (e.Source is not Control source)
            return null;

        var control = source;
        while (control != null && control != HistoryList)
        {
            if (control.DataContext is SearchTaskItem item)
                return item;
            control = control.Parent as Control;
        }

        return null;
    }

    private static SearchTaskItem? GetTaskFromControl(Control? control)
    {
        while (control is not null)
        {
            if (control.DataContext is SearchTaskItem item)
                return item;
            control = control.Parent as Control;
        }

        return null;
    }
}
