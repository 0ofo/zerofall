using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZeroFall.Platform.Registries;

public static class DockTabToolPanelHelper
{
    public static StackPanel CreateHorizontalPanel(double spacing = 4) =>
        new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = spacing,
            VerticalAlignment = VerticalAlignment.Center
        };

    public static StreamGeometry? ResolveIcon(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as StreamGeometry : null;

    public static Button AddIconCommandButton(
        StackPanel panel,
        string iconKey,
        string commandPath,
        object? commandSource = null,
        string? tooltip = null,
        string sizeClass = "Small")
    {
        var button = new Button
        {
            Classes = { sizeClass },
            Padding = new Thickness(2),
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new PathIcon
            {
                Data = ResolveIcon(iconKey),
                Width = 14,
                Height = 14
            }
        };
        button.Bind(Button.CommandProperty, CreateBinding(commandPath, commandSource));
        if (tooltip is not null)
            ToolTip.SetTip(button, tooltip);
        panel.Children.Add(button);
        return button;
    }

    public static Button AddTextCommandButton(
        StackPanel panel,
        string content,
        string commandPath,
        object? commandSource = null,
        string sizeClass = "Small",
        string? isVisiblePath = null)
    {
        var button = new Button
        {
            Content = content,
            Classes = { sizeClass },
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Bind(Button.CommandProperty, CreateBinding(commandPath, commandSource));
        if (isVisiblePath is not null)
            button.Bind(Visual.IsVisibleProperty, CreateBinding(isVisiblePath, commandSource));
        panel.Children.Add(button);
        return button;
    }

    public static ToggleSwitch AddBoundToggle(
        StackPanel panel,
        string propertyPath,
        object? source = null,
        string? onContent = null,
        string? offContent = null,
        string? tooltip = null)
    {
        var toggle = new ToggleSwitch
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        if (onContent is not null)
            toggle.OnContent = onContent;
        if (offContent is not null)
            toggle.OffContent = offContent;
        var binding = CreateBinding(propertyPath, source);
        binding.Mode = BindingMode.TwoWay;
        toggle.Bind(ToggleSwitch.IsCheckedProperty, binding);
        if (tooltip is not null)
            ToolTip.SetTip(toggle, tooltip);
        panel.Children.Add(toggle);
        return toggle;
    }

    public static TPanel BindDataContext<TPanel>(TPanel panel, object? dataContext) where TPanel : Control
    {
        panel.DataContext = dataContext;
        return panel;
    }

    private static Binding CreateBinding(string path, object? source)
    {
        var binding = new Binding(path);
        if (source is not null)
            binding.Source = source;
        return binding;
    }
}
