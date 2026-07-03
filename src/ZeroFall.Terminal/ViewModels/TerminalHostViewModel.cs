using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Dock.Controls;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Services;
using ZeroFall.Terminal.Services;
using ZeroFall.Terminal.Views;

namespace ZeroFall.Terminal.ViewModels;

public partial class TerminalHostViewModel : ViewModelBase
{
    private readonly IEventBus _eventBus;
    private readonly ISettingsService _settingsService;
    private readonly ITerminalTranscriptService _transcript;
    private string? _workspaceDirectory;
    private int _sessionCounter;

    public Func<string, Task>? CopyTextAsync { get; set; }

    [ObservableProperty]
    private ObservableCollection<DockTabItemViewModel> _tabs = new();

    [ObservableProperty]
    private DockTabItemViewModel? _selectedTab;

    [ObservableProperty]
    private bool _isPanelVisible;

    [ObservableProperty]
    private bool _canCloseSessions;

    [ObservableProperty]
    private string _commandInput = string.Empty;

    public TerminalHostViewModel(
        IEventBus eventBus,
        ISettingsService settingsService,
        TerminalScreenService screenService,
        TerminalCommandService commandService,
        TerminalSessionStateService sessionStateService,
        ITerminalTranscriptService transcriptService)
    {
        _eventBus = eventBus;
        _settingsService = settingsService;
        _transcript = transcriptService;
        screenService.AttachHost(this);
        screenService.AttachTranscript(transcriptService);
        commandService.AttachHost(this);
        sessionStateService.AttachHost(this);
        sessionStateService.AttachTranscript(transcriptService);
        Tabs.CollectionChanged += OnTabsCollectionChanged;

        SubscribeEvent(eventBus, (TerminalVisibilityChangedEvent e) => IsPanelVisible = e.IsVisible);
        SubscribeEvent(eventBus, (TerminalCommandRequestedEvent e) => SendCommand(e.Command, e.SessionId));
    }

    public void EnsureInitialSession()
    {
        if (Tabs.Count > 0)
            return;

        var tab = CreateSessionTab();
        Tabs.Add(tab);
        SelectedTab = tab;
        RefreshCanCloseSessions();
    }

    public void SetWorkspaceDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        _workspaceDirectory = directory;
        foreach (var tab in Tabs)
        {
            if (TryGetSession(tab, out var session))
                session.CurrentDirectory = directory;
        }
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshCanCloseSessions();

    private void RefreshCanCloseSessions() => CanCloseSessions = Tabs.Count > 1;

    public void SendCommand(string command, string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        if (!IsPanelVisible)
            _eventBus.Publish(new TerminalVisibilityChangedEvent(true));

        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;

        if (tab == null && Tabs.Count > 0)
        {
            tab = Tabs[0];
            SelectedTab = tab;
        }

        if (tab != null && !ReferenceEquals(SelectedTab, tab))
            SelectedTab = tab;

        if (tab != null && TryGetSession(tab, out var session))
            session.PendingCommand = command.Trim();
    }

    public async Task SendCommandAsync(string command, string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        if (!IsPanelVisible)
            _eventBus.Publish(new TerminalVisibilityChangedEvent(true));

        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;

        if (tab == null && Tabs.Count > 0)
        {
            tab = Tabs[0];
            SelectedTab = tab;
        }

        if (tab != null && !ReferenceEquals(SelectedTab, tab))
            SelectedTab = tab;

        if (tab != null && TryGetView(tab, out var view))
            await view.SendCommandAsync(command.Trim());
    }

    public async Task SendInterruptAsync(string? sessionId = null)
    {
        if (!IsPanelVisible)
            _eventBus.Publish(new TerminalVisibilityChangedEvent(true));

        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;

        if (tab == null && Tabs.Count > 0)
        {
            tab = Tabs[0];
            SelectedTab = tab;
        }

        if (tab != null && !ReferenceEquals(SelectedTab, tab))
            SelectedTab = tab;

        if (tab != null && TryGetView(tab, out var view))
            await view.SendInterruptAsync();
    }

    [RelayCommand]
    private void InsertCommand()
    {
        if (string.IsNullOrWhiteSpace(CommandInput))
            return;

        SendCommand(CommandInput);
        CommandInput = string.Empty;
    }

    private DockTabItemViewModel CreateSessionTab()
    {
        _sessionCounter++;
        var session = new TerminalViewModel(_settingsService)
        {
            Title = $"终端 {_sessionCounter}",
            TranscriptService = _transcript
        };

        _transcript.RegisterSession(session.Id, session.Title);

        if (!string.IsNullOrWhiteSpace(_workspaceDirectory))
            session.CurrentDirectory = _workspaceDirectory;

        var view = new TerminalView
        {
            DataContext = session,
            Tag = session
        };

        return new DockTabItemViewModel
        {
            Id = session.Id,
            Title = session.Title,
            IsClosable = true,
            Content = TabContent.NonReloadable(view)
        };
    }

    [RelayCommand]
    private void NewTerminal()
    {
        var tab = CreateSessionTab();
        Tabs.Add(tab);
        SelectedTab = tab;

        if (!IsPanelVisible)
            _eventBus.Publish(new TerminalVisibilityChangedEvent(true));
    }

    [RelayCommand]
    private void CloseSession(DockTabItemViewModel? tab)
    {
        if (tab == null || Tabs.Count <= 1)
            return;

        var index = Tabs.IndexOf(tab);
        if (TryGetSession(tab, out var session))
        {
            _transcript.UnregisterSession(session.Id);
            session.Dispose();
        }

        TabContentLifetime.Release(tab.Content);
        tab.Content = null;
        Tabs.Remove(tab);

        if (ReferenceEquals(SelectedTab, tab))
            SelectedTab = Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)];
    }

    [RelayCommand]
    private void RestartSelectedTerminal()
    {
        if (SelectedTab != null && TryGetSession(SelectedTab, out var session))
            session.RestartTerminalCommand.Execute(null);
    }

    [RelayCommand]
    private async Task ReadSession(DockTabItemViewModel? tab)
    {
        tab ??= SelectedTab;
        var text = ReadTerminalContent(tab?.Id);
        if (string.IsNullOrEmpty(text) || CopyTextAsync is null)
            return;

        await CopyTextAsync(text);
    }

    public string? ReadTerminalContent(string? sessionId = null)
    {
        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;
        if (tab == null || !TryGetView(tab, out var view))
            return null;

        return view.ReadTerminalContent();
    }

    public string? ReadVisibleScreen(string? sessionId = null) => ReadTerminalContent(sessionId);

    public string? ReadSinceLastCommand(string? sessionId = null)
    {
        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;
        if (tab == null && Tabs.Count > 0)
            tab = Tabs[0];

        if (tab != null && TryGetView(tab, out var view))
            return view.ReadSinceLastCommand();

        return null;
    }

    public string? ReadSinceLastAiToolRead(string? sessionId = null)
    {
        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;
        if (tab == null && Tabs.Count > 0)
            tab = Tabs[0];

        if (tab != null && TryGetView(tab, out var view))
            return view.ReadSinceLastAiToolRead();

        return null;
    }

    public string? ReadLastLines(string? sessionId = null, int lineCount = 50)
    {
        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;
        if (tab == null && Tabs.Count > 0)
            tab = Tabs[0];

        if (tab != null && TryGetView(tab, out var view))
        {
            view.PrepareTranscriptForAiRead();
            return view.ReadLastLines(lineCount);
        }

        return null;
    }

    public void CommitAiReadCursor(string? sessionId = null)
    {
        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;
        if (tab == null && Tabs.Count > 0)
            tab = Tabs[0];

        if (tab != null && TryGetView(tab, out var view))
            view.CommitAiReadCursor();
    }

    public void PrepareTranscriptForAiRead(string? sessionId = null)
    {
        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;
        if (tab == null && Tabs.Count > 0)
            tab = Tabs[0];

        if (tab != null && TryGetView(tab, out var view))
            view.PrepareTranscriptForAiRead();
    }

    /// <summary>PTY 输出流切片：自上次 AI 工具 commit 后的增量。</summary>
    public string? ReadSinceLastAiToolReadPtySlice(string? sessionId = null)
    {
        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;
        if (tab == null && Tabs.Count > 0)
            tab = Tabs[0];

        return tab != null && TryGetView(tab, out var view)
            ? view.ReadSinceLastAiToolReadPtySlice()
            : null;
    }

    /// <summary>自上次 send 起 PTY 原始输出切片（不依赖 XTerm buffer / transcript 行模型）。</summary>
    public string? ReadSinceLastCommandPtySlice(string? sessionId = null)
    {
        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;
        if (tab == null && Tabs.Count > 0)
            tab = Tabs[0];

        return tab != null && TryGetView(tab, out var view)
            ? view.ReadSinceLastCommandPtySlice()
            : null;
    }

    internal string? ResolveSessionId(string? sessionId)
    {
        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;
        if (tab == null && Tabs.Count > 0)
            tab = Tabs[0];
        return tab?.Id;
    }

    public TerminalCommandPhase GetCommandPhase(string? sessionId = null)
    {
        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;
        if (tab == null || !TryGetSession(tab, out var session))
            return TerminalCommandPhase.Unknown;

        return session.CommandPhase;
    }

    public double? GetSecondsSinceLastOutput(string? sessionId = null)
    {
        var tab = sessionId != null
            ? Tabs.FirstOrDefault(t => string.Equals(t.Id, sessionId, StringComparison.Ordinal))
            : SelectedTab;
        if (tab == null && Tabs.Count > 0)
            tab = Tabs[0];

        return tab != null && TryGetView(tab, out var view)
            ? view.GetSecondsSinceLastOutput()
            : null;
    }

    public static bool TryGetView(DockTabItemViewModel tab, out TerminalView view)
    {
        view = null!;
        if (NonReloadableTabContent.Resolve<TerminalView>(tab.Content) is not { } resolved)
            return false;

        view = resolved;
        return true;
    }

    public static bool TryGetSession(DockTabItemViewModel tab, out TerminalViewModel session)
    {
        session = null!;
        if (NonReloadableTabContent.Resolve<TerminalView>(tab.Content) is not { } view)
            return false;

        if (view.Tag is TerminalViewModel tagVm)
        {
            session = tagVm;
            return true;
        }

        if (view.DataContext is TerminalViewModel dcVm)
        {
            session = dcVm;
            return true;
        }

        return false;
    }
}
