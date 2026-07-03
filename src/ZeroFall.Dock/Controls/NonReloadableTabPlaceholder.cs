using Avalonia;
using Avalonia.Controls;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;

namespace ZeroFall.Dock.Controls;

/// <summary>不可重载 Tab 的标准占位壳（空面板）。</summary>
public sealed class NonReloadableTabPlaceholder : Panel, INonReloadableTabShell, IDockTabToolPanelProvider
{
    public static readonly StyledProperty<Control?> PersistedContentProperty =
        AvaloniaProperty.Register<NonReloadableTabPlaceholder, Control?>(nameof(PersistedContent));

    public Control? PersistedContent
    {
        get => GetValue(PersistedContentProperty);
        set => SetValue(PersistedContentProperty, value);
    }

    public static NonReloadableTabPlaceholder Wrap(Control content) =>
        new() { PersistedContent = content };

    public void OnTabBecameVisible()
    {
        if (PersistedContent is INonReloadableTabHost host)
            host.OnTabBecameVisible();
    }

    public void OnTabBecameHidden()
    {
        if (PersistedContent is INonReloadableTabHost host)
            host.OnTabBecameHidden();
    }

    public Control? GetDockTabToolPanel()
    {
        if (PersistedContent is IDockTabToolPanelProvider provider)
            return provider.GetDockTabToolPanel();
        return null;
    }
}
