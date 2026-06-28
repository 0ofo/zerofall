using System;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;

namespace ZeroFall.Platform.Services;

public sealed record UiContextSnapshot(
    string ActiveTabId,
    string ActiveTabTitle,
    string SelectionType,
    string SelectionSummary,
    string SelectionPayloadJson,
    DateTimeOffset UpdatedAtUtc);

public interface IUiContextService
{
    UiContextSnapshot GetSnapshot();
}

public sealed class UiContextService : IUiContextService, IDisposable
{
    private readonly IDisposable _activeTabSub;
    private readonly IDisposable _selectionSub;
    private readonly object _gate = new();
    private UiContextSnapshot _snapshot = new(
        ActiveTabId: string.Empty,
        ActiveTabTitle: string.Empty,
        SelectionType: string.Empty,
        SelectionSummary: string.Empty,
        SelectionPayloadJson: "{}",
        UpdatedAtUtc: DateTimeOffset.UtcNow);

    public UiContextService(IEventBus eventBus)
    {
        _activeTabSub = eventBus.SubscribeDisposable<ActiveContentTabChangedEvent>(OnActiveTabChanged);
        _selectionSub = eventBus.SubscribeDisposable<UiSelectionChangedEvent>(OnSelectionChanged);
    }

    public UiContextSnapshot GetSnapshot()
    {
        lock (_gate)
            return _snapshot;
    }

    private void OnActiveTabChanged(ActiveContentTabChangedEvent e)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                ActiveTabId = e.TabId,
                ActiveTabTitle = e.Title,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }
    }

    private void OnSelectionChanged(UiSelectionChangedEvent e)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                SelectionType = e.SelectionType,
                SelectionSummary = e.Summary,
                SelectionPayloadJson = e.PayloadJson,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }
    }

    public void Dispose()
    {
        _activeTabSub.Dispose();
        _selectionSub.Dispose();
    }
}
