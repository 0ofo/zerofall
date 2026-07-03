using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Events;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Services;

namespace ZeroFall.App.ViewModels;

/// <summary>欢迎 / 空 Content 页统一入口（无项目全屏 overlay 与 workspace-guide Tab 共用）。</summary>
public partial class WorkspaceHomeViewModel : ViewModelBase
{
    private readonly IEventBus _eventBus;
    private readonly IUiMenuCommandService _uiMenuCommandService;

    public WorkspaceHomeViewModel(IEventBus eventBus, IUiMenuCommandService uiMenuCommandService)
    {
        _eventBus = eventBus;
        _uiMenuCommandService = uiMenuCommandService;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HintText))]
    private bool _showOpenProjectFolder = true;

    public string HintText => ShowOpenProjectFolder
        ? "打开项目文件夹开始工作，或直接打开单个文件。"
        : "项目已就绪。可从下方入口继续，或左侧打开资源。";

    [RelayCommand]
    private void OpenProjectFolder() =>
        _eventBus.Publish(new OpenFolderRequestedEvent());

    [RelayCommand]
    private void OpenFile() =>
        _eventBus.Publish(new OpenWorkspaceFileRequestedEvent());

    [RelayCommand]
    private void OpenBrowser()
    {
        EnsureWorkspaceShell();
        _uiMenuCommandService.Execute(UiMenuCommandIds.NewBrowser);
    }

    [RelayCommand]
    private void OpenTerminal()
    {
        EnsureWorkspaceShell();
        _uiMenuCommandService.Execute(UiMenuCommandIds.OpenTerminalPanel);
    }

    [RelayCommand]
    private void OpenAssetRecon()
    {
        EnsureWorkspaceShell();
        _uiMenuCommandService.Execute(UiMenuCommandIds.OpenReconPanel);
    }

    [RelayCommand]
    private void OpenSettings() =>
        _eventBus.Publish(new SettingsRequestedEvent());

    private void EnsureWorkspaceShell()
    {
        if (ShowOpenProjectFolder)
            _eventBus.Publish(new WelcomeQuickAccessRequestedEvent());
    }
}
