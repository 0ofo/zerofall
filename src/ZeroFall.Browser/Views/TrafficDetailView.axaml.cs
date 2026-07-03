using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using ZeroFall.Browser.ViewModels;

namespace ZeroFall.Browser.Views;

public partial class TrafficDetailView : UserControl
{
    private TrafficMonitorTabViewModel? _viewModel;
    private TrafficLogEntryViewModel? _observedEntry;
    private TrafficLogEntryViewModel? _pendingEntry;
    private bool _updateScheduled;

    public TrafficDetailView()
    {
        InitializeComponent();
        MessagePair.Loaded += OnMessagePairLoaded;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void RefreshFromSelectedEntry()
    {
        UpdateEditors(_viewModel?.SelectedEntry);
        ScheduleEditorSync();
    }

    private void OnLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is TrafficMonitorTabViewModel vm)
            AttachViewModel(vm);
        else
            RefreshFromSelectedEntry();
    }

    private void OnUnloaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        MessagePair.Loaded -= OnMessagePairLoaded;
        DetachViewModel();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        DetachViewModel();

        if (DataContext is TrafficMonitorTabViewModel vm)
            AttachViewModel(vm);
    }

    private void AttachViewModel(TrafficMonitorTabViewModel vm)
    {
        if (ReferenceEquals(_viewModel, vm))
        {
            RefreshFromSelectedEntry();
            return;
        }

        DetachViewModel();
        _viewModel = vm;
        MessagePair.SendToReplayCommand = vm.SendSelectedToReplayCommand;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshFromSelectedEntry();
    }

    private void DetachViewModel()
    {
        DetachObservedEntry();

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        MessagePair.SendToReplayCommand = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TrafficMonitorTabViewModel.SelectedEntry))
            return;

        _pendingEntry = _viewModel?.SelectedEntry;
        if (!_updateScheduled)
        {
            _updateScheduled = true;
            Dispatcher.UIThread.Post(FlushPendingUpdate, DispatcherPriority.Loaded);
        }
    }

    private void OnMessagePairLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) =>
        RefreshFromSelectedEntry();

    private void FlushPendingUpdate()
    {
        _updateScheduled = false;
        UpdateEditors(_pendingEntry);
        ScheduleEditorSync();
    }

    private void ScheduleEditorSync()
    {
        if (!MessagePair.IsLoaded)
            return;

        MessagePair.SyncEditorsFromProperties();
        Dispatcher.UIThread.Post(
            () => MessagePair.SyncEditorsFromProperties(),
            DispatcherPriority.Loaded);
    }

    private void UpdateEditors(TrafficLogEntryViewModel? entry)
    {
        SwitchObservedEntry(entry);

        if (entry is null)
        {
            MessagePair.RequestText = string.Empty;
            MessagePair.ResponseText = string.Empty;
            MessagePair.ResponseBodyRaw = null;
            MessagePair.ReplayRealHost = string.Empty;
            MessagePair.ReplayIsHttps = false;
            MessagePair.ResponseHeaderRightText = "—";
            return;
        }

        MessagePair.RequestText = entry.HttpRequestText;
        MessagePair.ResponseText = entry.HttpResponseText;
        MessagePair.ResponseBodyRaw = entry.ResponseBodyRaw;
        MessagePair.ReplayRealHost = ResolveRequestHost(entry);
        MessagePair.ReplayIsHttps = IsHttps(entry.Url);
        MessagePair.ResponseHeaderRightText = ResolveResponseHeaderRightText(entry);
    }

    private static string ResolveRequestHost(TrafficLogEntryViewModel entry)
    {
        if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri))
            return uri.Authority;

        return string.Empty;
    }

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string ResolveResponseHeaderRightText(TrafficLogEntryViewModel entry) =>
        entry.ResponseLatencyText;

    private void SwitchObservedEntry(TrafficLogEntryViewModel? entry)
    {
        if (ReferenceEquals(_observedEntry, entry))
            return;

        DetachObservedEntry();
        _observedEntry = entry;
        if (_observedEntry is not null)
            _observedEntry.PropertyChanged += OnObservedEntryPropertyChanged;
    }

    private void DetachObservedEntry()
    {
        if (_observedEntry is null)
            return;

        _observedEntry.PropertyChanged -= OnObservedEntryPropertyChanged;
        _observedEntry = null;
    }

    private void OnObservedEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var selected = _viewModel?.SelectedEntry;
        if (!ReferenceEquals(sender, selected))
            return;

        if (e.PropertyName != nameof(TrafficLogEntryViewModel.ResponseBody)
            && e.PropertyName != nameof(TrafficLogEntryViewModel.ResponseBodyRaw)
            && e.PropertyName != nameof(TrafficLogEntryViewModel.RequestBody)
            && e.PropertyName != nameof(TrafficLogEntryViewModel.RequestBodyRaw)
            && e.PropertyName != nameof(TrafficLogEntryViewModel.HttpResponseText)
            && e.PropertyName != nameof(TrafficLogEntryViewModel.HttpRequestText))
            return;

        _pendingEntry = selected;
        if (!_updateScheduled)
        {
            _updateScheduled = true;
            Dispatcher.UIThread.Post(FlushPendingUpdate, DispatcherPriority.Loaded);
        }
    }
}
