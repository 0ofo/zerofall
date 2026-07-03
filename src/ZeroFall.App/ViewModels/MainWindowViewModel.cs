using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Dock.Controls;
using ZeroFall.Dock.Services;
using ZeroFall.Dock.ViewModels;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;
using ZeroFall.Terminal.ViewModels;
using ZeroFall.Terminal.Views;

namespace ZeroFall.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private DockLayoutViewModel _dockLayout;

    [ObservableProperty]
    private Workspace? _currentProject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWorkspaceChrome))]
    private bool _hasProject;

    /// <summary>未打开项目时从欢迎页进入终端/侦察等工作区。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWorkspaceChrome))]
    private bool _welcomeQuickAccessActive;

    public bool ShowWorkspaceChrome => HasProject || WelcomeQuickAccessActive;

    public WorkspaceHomeViewModel Home { get; }

    public Func<string?, Task<string?>>? ShowOpenFolderDialog { get; set; }
    public Func<string?, Task<string?>>? ShowOpenFileDialog { get; set; }

    private readonly IEventBus _eventBus;
    private readonly ISettingsService _settingsService;
    private readonly IWorkspaceService _workspaceService;
    private readonly UiLayoutService _uiLayoutService;
    private readonly UiMenuCommandService _uiMenuCommandService;

    public MainWindowViewModel(IEventBus eventBus, IDockLayoutRegistry registry,
        IMenuRegistry menuRegistry, ContentCreationService contentCreation,
        ISettingsService settingsService, IWorkspaceService workspaceService,
        UiLayoutService uiLayoutService, UiMenuCommandService uiMenuCommandService,
        WorkspaceHomeViewModel home)
    {
        _eventBus = eventBus;
        _settingsService = settingsService;
        _workspaceService = workspaceService;
        _uiLayoutService = uiLayoutService;
        _uiMenuCommandService = uiMenuCommandService;
        Home = home;
        _dockLayout = new DockLayoutViewModel(registry, eventBus, menuRegistry, contentCreation, settingsService);
        _uiLayoutService.Attach(_dockLayout);
        _uiMenuCommandService.Attach(_dockLayout);
        SyncHomeProjectMode();

        SubscribeEvent(eventBus, (ExitRequestedEvent e) => OnExitRequested(e));
        SubscribeEvent(eventBus, (OpenFolderRequestedEvent e) => OnOpenFolderRequested(e));
        SubscribeEvent(eventBus, (WelcomeQuickAccessRequestedEvent e) => ActivateWelcomeQuickAccess());
        SubscribeEvent(eventBus, (NewTerminalSessionRequestedEvent e) => OnNewTerminalSessionRequested(e));
        SubscribeEvent(eventBus, (OpenWorkspaceFileRequestedEvent e) => _ = OnOpenWorkspaceFileRequestedAsync(e));
    }

    partial void OnHasProjectChanged(bool value)
    {
        SyncHomeProjectMode();
        if (value)
            WelcomeQuickAccessActive = false;
    }

    public async Task InitializeDockLayoutAsync()
    {
        await DockLayout.ApplyRegistrationsAsync(restoreSavedLayout: HasProject, materializeSelections: HasProject);
        DockLayout.TopBar.ApplyMenuRegistrations();
        if (!HasProject)
            DockLayout.EnterNoProjectMode();
    }

    public void CompleteStartupLayout()
    {
        if (!HasProject)
            return;
        DockLayout.CompleteStartupLayout();
    }

    private async void OnOpenFolderRequested(OpenFolderRequestedEvent e)
    {
        await OpenFolderAsync(null);
    }

    public async Task OpenFolderAsync(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            if (ShowOpenFolderDialog == null) return;
            path = await ShowOpenFolderDialog(null);
            if (string.IsNullOrEmpty(path)) return;
        }

        if (!Directory.Exists(path)) return;

        var project = Workspace.FromDirectory(path);
        CurrentProject = project;
        HasProject = true;
        WelcomeQuickAccessActive = false;

        DockLayout.TopBar.WindowTitle = $"烬 - {project.Name}";
        DockLayout.StatusBar.ConnectionStatus = "已连接";

        if (!File.Exists(project.DatabasePath))
        {
            var dir = Path.GetDirectoryName(project.DatabasePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        SaveLastProjectPath(path);
        _ = _workspaceService.OpenWorkspaceAsync(path);
        ActivateProjectWorkspace();
    }

    public void TryRestoreLastProject()
    {
        var settings = _settingsService.Load();
        if (!settings.General.AutoOpenLastProject)
            return;
        if (string.IsNullOrEmpty(settings.General.LastProjectPath)) return;
        if (!Directory.Exists(settings.General.LastProjectPath)) return;

        var project = Workspace.FromDirectory(settings.General.LastProjectPath);
        CurrentProject = project;
        HasProject = true;
        WelcomeQuickAccessActive = false;

        DockLayout.TopBar.WindowTitle = $"烬 - {project.Name}";
        DockLayout.StatusBar.ConnectionStatus = "已连接";

        if (!File.Exists(project.DatabasePath))
        {
            var dir = Path.GetDirectoryName(project.DatabasePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        _ = _workspaceService.OpenWorkspaceAsync(settings.General.LastProjectPath);
        ActivateProjectWorkspace();
        SyncHomeProjectMode();
    }

    private void SyncHomeProjectMode() =>
        Home.ShowOpenProjectFolder = !HasProject;

    private void ActivateWelcomeQuickAccess()
    {
        if (HasProject)
            return;

        WelcomeQuickAccessActive = true;
        DockLayout.ExitNoProjectMode();
        DockLayout.SyncShellPanelVisibility();
    }

    private void ActivateProjectWorkspace()
    {
        DockLayout.ExitNoProjectMode();
        NotifyProjectOpened();
    }

    private void SaveLastProjectPath(string path)
    {
        try
        {
            var settings = _settingsService.Load();
            settings.General.LastProjectPath = path;
            if (!_settingsService.Save(settings))
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] SaveLastProjectPath failed: {_settingsService.LastError}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] SaveLastProjectPath exception: {ex}");
        }
    }

    private void NotifyProjectOpened()
    {
        if (CurrentProject == null) return;
        _eventBus.Publish(new ProjectOpenedEvent(CurrentProject.DirectoryPath, CurrentProject.DatabasePath));
        InitializeTerminal();
    }

    private void InitializeTerminal()
    {
        if (CurrentProject == null)
            return;

        var terminalTab = DockLayout.BottomPanel.Tabs.FirstOrDefault(t => t.Id == "terminal");
        if (NonReloadableTabContent.Resolve<TerminalHostView>(terminalTab?.Content) is not { DataContext: TerminalHostViewModel hostVm })
            return;

        hostVm.SetWorkspaceDirectory(CurrentProject.DirectoryPath);
    }

    private void OnNewTerminalSessionRequested(NewTerminalSessionRequestedEvent e)
    {
        var terminalTab = DockLayout.BottomPanel.Tabs.FirstOrDefault(t => t.Id == "terminal");
        if (NonReloadableTabContent.Resolve<TerminalHostView>(terminalTab?.Content) is not { DataContext: TerminalHostViewModel hostVm })
            return;

        hostVm.NewTerminalCommand.Execute(null);
    }

    private async Task OnOpenWorkspaceFileRequestedAsync(OpenWorkspaceFileRequestedEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.FilePath))
        {
            var resolved = ResolveWorkspaceFilePath(e.FilePath.Trim());
            if (resolved == null)
            {
                _eventBus.Publish(new StatusMessageEvent("未打开工作区，无法打开相对路径文件"));
                return;
            }

            if (!File.Exists(resolved))
            {
                _eventBus.Publish(new StatusMessageEvent($"文件不存在: {e.FilePath.Trim()}"));
                return;
            }

            _eventBus.Publish(new OpenWorkspaceFileInEditorEvent(resolved));
            return;
        }

        if (ShowOpenFileDialog == null)
            return;

        var path = await ShowOpenFileDialog(null);
        if (string.IsNullOrEmpty(path))
            return;

        if (!HasProject)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                return;
            await OpenFolderAsync(dir);
        }

        if (HasProject)
            _eventBus.Publish(new OpenWorkspaceFileInEditorEvent(path));
    }

    private string? ResolveWorkspaceFilePath(string path) =>
        WorkspacePathHelper.ResolveFilePath(
            path,
            CurrentProject?.DirectoryPath ?? _workspaceService.CurrentWorkspace?.DirectoryPath);

    private void OnExitRequested(ExitRequestedEvent e)
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is { } window)
                window.Close();
            else
                desktop.Shutdown();
        }
    }

}
