using System;
using System.Linq;
using System.Text.Json;
using Avalonia.Threading;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Serialization;
using ZeroFall.Platform.Services;
using ZeroFall.Terminal.ViewModels;
using ZeroFall.Terminal.Views;

namespace ZeroFall.Terminal.Services;

public sealed class TerminalUiLayoutTabExtraProvider : IUiLayoutTabExtraProvider
{
    public bool TryGetExtra(DockTabItemViewModel tab, DockPosition region, out JsonElement extra)
    {
        extra = default;
        if (region != DockPosition.Bottom
            || !string.Equals(tab.Id, "terminal", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            var result = Dispatcher.UIThread.Invoke(() =>
            {
                var built = TryBuildExtra(tab, region, out var element);
                return (built, element);
            });
            extra = result.element;
            return result.built;
        }

        return TryBuildExtra(tab, region, out extra);
    }

    private static bool TryBuildExtra(DockTabItemViewModel tab, DockPosition region, out JsonElement extra)
    {
        extra = default;
        if (region != DockPosition.Bottom
            || !string.Equals(tab.Id, "terminal", StringComparison.Ordinal))
        {
            return false;
        }

        if (NonReloadableTabContent.Resolve<TerminalHostView>(tab.Content) is not { DataContext: TerminalHostViewModel host })
            return false;

        var selectedId = host.SelectedTab?.Id;
        var sessions = host.Tabs
            .Select(t => new TerminalSessionLayoutItem(
                t.Id,
                t.Title,
                string.Equals(t.Id, selectedId, StringComparison.Ordinal),
                FormatPhase(host.GetCommandPhase(t.Id))))
            .ToArray();

        var dto = new TerminalUiLayoutExtra(sessions.Length, selectedId, sessions);
        extra = JsonSerializer.SerializeToElement(dto, PlatformJsonContext.Default.TerminalUiLayoutExtra);
        return true;
    }

    private static string FormatPhase(TerminalCommandPhase phase) => phase switch
    {
        TerminalCommandPhase.Idle => "idle",
        TerminalCommandPhase.Executing => "executing",
        _ => "unknown"
    };
}
