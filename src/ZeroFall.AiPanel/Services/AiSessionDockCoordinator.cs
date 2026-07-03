using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ZeroFall.AiPanel.Views;
using ZeroFall.AiPanel.ViewModels;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;

namespace ZeroFall.AiPanel.Services;

/// <summary>把 AI 会话集合映射为 Right Dock 顶级 Tab；每个 Tab 持有独立 AiPanelView / AiChatWebView。</summary>
public sealed class AiSessionDockCoordinator : IDisposable
{
    private const string TabPrefix = "ai-session:";

    private readonly IEventBus _eventBus;
    private readonly AiPanelViewModel _viewModel;
    private readonly AiSharedPanelHost _sharedHost;
    private readonly HashSet<string> _knownSessionIds = [];
    private readonly HashSet<string> _closeConfirmInFlight = [];
    private readonly Action<TabClosedEvent> _tabClosedHandler;
    private readonly Action<TabCloseRequestedEvent> _tabCloseRequestedHandler;
    private readonly Action<DockTabSelectedEvent> _tabSelectedHandler;
    private bool _disposed;

    public AiSessionDockCoordinator(
        IEventBus eventBus,
        AiPanelViewModel viewModel,
        AiSharedPanelHost sharedHost)
    {
        _eventBus = eventBus;
        _viewModel = viewModel;
        _sharedHost = sharedHost;
        _tabClosedHandler = OnTabClosed;
        _tabCloseRequestedHandler = OnTabCloseRequested;
        _tabSelectedHandler = OnDockTabSelected;

        _viewModel.Sessions.CollectionChanged += OnSessionsCollectionChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _eventBus.Subscribe(_tabClosedHandler);
        _eventBus.Subscribe(_tabCloseRequestedHandler);
        _eventBus.Subscribe(_tabSelectedHandler);
        SyncAllSessions();
    }

    private void OnSessionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(SyncAllSessions);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(AiPanelViewModel.CurrentSessionId), StringComparison.Ordinal))
            Dispatcher.UIThread.Post(SwitchToCurrentSessionTab);
    }

    private void SyncAllSessions()
    {
        if (_disposed)
            return;

        var currentIds = _viewModel.Sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _knownSessionIds.Where(id => !currentIds.Contains(id)).ToList())
        {
            _knownSessionIds.Remove(removed);
            Dispatcher.UIThread.Post(() => _sharedHost.ReleaseSession(removed));
            _eventBus.Publish(new RemoveDockTabEvent(DockPosition.Right, BuildTabId(removed)));
        }

        foreach (var session in _viewModel.Sessions)
        {
            var tabId = BuildTabId(session.Id);
            if (_knownSessionIds.Add(session.Id))
            {
                _eventBus.Publish(new AddDockTabEvent(
                    DockPosition.Right,
                    new DockTabItemViewModel
                    {
                        Id = tabId,
                        Title = string.IsNullOrWhiteSpace(session.Title) ? "新会话" : session.Title,
                        Icon = IconHelper.GetIcon("SemiIconAIFilled"),
                        IsClosable = true,
                        Content = new AiSessionTabShell(_sharedHost, session.Id)
                    },
                    Select: false));
            }
            else
            {
                _eventBus.Publish(new UpdateDockTabTitleEvent(
                    DockPosition.Right,
                    tabId,
                    string.IsNullOrWhiteSpace(session.Title) ? "新会话" : session.Title));
            }
        }

        SwitchToCurrentSessionTab();
    }

    private void SwitchToCurrentSessionTab()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_viewModel.CurrentSessionId))
            return;
        if (!_knownSessionIds.Contains(_viewModel.CurrentSessionId))
            return;

        _eventBus.Publish(new SwitchDockTabRequestedEvent(DockPosition.Right, BuildTabId(_viewModel.CurrentSessionId)));
    }

    private void OnTabCloseRequested(TabCloseRequestedEvent e)
    {
        if (!TryGetSessionId(e.Tab.Id, out var sessionId))
            return;

        e.Cancel = true;
        _ = ConfirmAndCloseSessionAsync(sessionId);
    }

    private async Task ConfirmAndCloseSessionAsync(string sessionId)
    {
        if (_disposed || !_closeConfirmInFlight.Add(sessionId))
            return;

        try
        {
            var message = _viewModel.BuildCloseSessionConfirmMessage(sessionId);
            var confirmed = await AiSessionCloseConfirmDialog.ShowAsync(null, message).ConfigureAwait(true);
            if (!confirmed)
                return;

            await _viewModel.CloseSessionAsync(sessionId).ConfigureAwait(true);
        }
        finally
        {
            _closeConfirmInFlight.Remove(sessionId);
        }
    }

    private void OnTabClosed(TabClosedEvent e)
    {
        if (!TryGetSessionId(e.Tab.Id, out var sessionId))
            return;

        Dispatcher.UIThread.Post(() => _sharedHost.ReleaseSession(sessionId));
        if (_viewModel.DeleteConversationCommand.CanExecute(sessionId))
            _viewModel.DeleteConversationCommand.Execute(sessionId);
    }

    private void OnDockTabSelected(DockTabSelectedEvent e)
    {
        if (e.Region != DockPosition.Right || e.Tab is null)
            return;
        if (!TryGetSessionId(e.Tab.Id, out var sessionId))
            return;

        _viewModel.FocusSessionTab(sessionId);
        _ = _sharedHost.ActivateSessionAsync(sessionId);
    }

    private static string BuildTabId(string sessionId) => $"{TabPrefix}{sessionId}";

    private static bool TryGetSessionId(string tabId, out string sessionId)
    {
        if (tabId.StartsWith(TabPrefix, StringComparison.Ordinal))
        {
            sessionId = tabId[TabPrefix.Length..];
            return !string.IsNullOrWhiteSpace(sessionId);
        }

        sessionId = string.Empty;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _viewModel.Sessions.CollectionChanged -= OnSessionsCollectionChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _eventBus.Unsubscribe(_tabClosedHandler);
        _eventBus.Unsubscribe(_tabCloseRequestedHandler);
        _eventBus.Unsubscribe(_tabSelectedHandler);
    }
}
