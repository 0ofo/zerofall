using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZeroFall.Base.Diagnostics;
using ZeroFall.Base.Events;
using ZeroFall.Dock.Services;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Registries;
using ZeroFall.App.ViewModels;
using ZeroFall.Dock.ViewModels;
using ZeroFall.Settings.ViewModels;
using ZeroFall.Settings.Views;
using ZeroFall.AiPanel.Services;
using Microsoft.Extensions.DependencyInjection;
using Ursa.Common;
using Ursa.Controls;

namespace ZeroFall.App.Views;

public partial class MainWindow : Window
{
    private double _rightPanelLastWidth = 300;
    private double _bottomPanelLastHeight = 200;
    private double _sidebarLastWidth = 200;
    private double _contentPanelLastWidth = 600;

    private IEventBus? _eventBus;
    private ISqliteService? _sqliteService;
    private bool _eventsSubscribed;
    private bool _allowCloseWithoutFlush;
    private bool _exitConfirmInFlight;
    private MainContentView? _mainContent;

    public MainWindow()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        DataContextChanged += OnDataContextChanged;
        Opened += OnWindowOpened;
        Closing += OnMainWindowClosing;
    }

    private IEventBus GetEventBus() =>
        _eventBus ??= App.Services.GetRequiredService<IEventBus>();

    private ISqliteService GetSqliteService() =>
        _sqliteService ??= App.Services.GetRequiredService<ISqliteService>();

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        TrySubscribeEvents();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowCloseWithoutFlush)
            return;

        e.Cancel = true;
        if (_exitConfirmInFlight)
            return;

        _exitConfirmInFlight = true;
        // 退出 Closing 回调后再弹窗，避免 Ursa StandardDialog 与 Close 重入导致主窗消失但进程未退出。
        Dispatcher.UIThread.Post(ConfirmAndShutdownAsync);
    }

    private async void ConfirmAndShutdownAsync()
    {
        try
        {
            if (!await ShowExitConfirmDialogAsync())
                return;

            try
            {
                var host = App.Services?.GetService<AiSharedPanelHost>();
                if (host is not null)
                    await host.FlushAllSessionsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Exception("Flush AI sessions before window close failed", ex);
            }

            _allowCloseWithoutFlush = true;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Close();
        }
        finally
        {
            _exitConfirmInFlight = false;
        }
    }

    public void TrySubscribeEvents()
    {
        if (_eventsSubscribed) return;
        if (App.Services == null) return;

        _eventsSubscribed = true;
        var eventBus = GetEventBus();
        eventBus.Subscribe<PanelVisibilityChangedEvent>(OnPanelVisibilityChanged);
        eventBus.Subscribe<SettingsRequestedEvent>(OnSettingsRequested);
        eventBus.Subscribe<ThemeChangedEvent>(OnThemeChanged);
        eventBus.Subscribe<IndexFileRequestedEvent>(OnIndexFileRequested);
    }

    public void InjectMainContent(MainContentView content)
    {
        _mainContent = content;
        var rootGrid = this.FindControl<Grid>("RootGrid");
        if (rootGrid != null)
        {
            rootGrid.Children.Insert(0, content);
        }

        content.Loaded += OnMainContentLoaded;
    }

    /// <summary>按 ViewModel 状态重算 Dock 行列尺寸（RestoreLayout 事件可能早于首帧布局）。</summary>
    public void SyncShellPanelLayout(DockLayoutViewModel dock)
    {
        if (_mainContent == null) return;

        ApplyPanelVisibility(DockPosition.Left, dock.LeftPanel.IsVisible);
        ApplyPanelVisibility(DockPosition.Right, dock.RightPanel.IsVisible);
        ApplyPanelVisibility(DockPosition.Bottom, dock.TopBar.IsTerminalVisible);
    }

    private void OnMainContentLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not MainContentView content)
            return;

        content.Loaded -= OnMainContentLoaded;
        if (DataContext is MainWindowViewModel vm)
            SyncShellPanelLayout(vm.DockLayout);
    }

    private void ApplyPanelVisibility(DockPosition position, bool isVisible) =>
        OnPanelVisibilityChanged(new PanelVisibilityChangedEvent(position, isVisible));

    public void HideLoadingOverlay()
    {
        var overlay = this.FindControl<Border>("LoadingOverlay");
        if (overlay == null)
            return;

        overlay.IsVisible = false;
        if (overlay.Parent is Panel panel)
            panel.Children.Remove(overlay);
    }

    private void OnThemeChanged(ThemeChangedEvent e)
    {
    }

    private void OnSettingsRequested(SettingsRequestedEvent e)
    {
        var settingsRegistry = App.Services.GetRequiredService<ISettingsRegistry>();
        var settingsWindow = new SettingsWindow();
        var viewModel = new SettingsWindowViewModel(() => settingsWindow.Close(), e.TargetTabTitle);
        settingsWindow.SetSettingsRegistry(settingsRegistry);
        settingsWindow.DataContext = viewModel;
        settingsWindow.ShowDialog(this);
    }

    private void OnPanelVisibilityChanged(PanelVisibilityChangedEvent e)
    {
        if (_mainContent == null) return;

        var grid = _mainContent.GetMainGrid();
        if (grid == null) return;

        switch (e.Position)
        {
            case DockPosition.Left:
                var leftTabControl = _mainContent.GetLeftDockTabControl();
                if (leftTabControl?.SidebarMode == true)
                {
                    ToggleSidebarColumn(grid, 0, _mainContent.GetLeftSplitter(), e.IsVisible, ref _sidebarLastWidth);
                }
                else
                {
                    ToggleColumn(grid, 0, _mainContent.GetLeftSplitter(), e.IsVisible, ref _sidebarLastWidth);
                }
                break;
            case DockPosition.Right:
                ToggleColumn(grid, 4, _mainContent.GetRightSplitter(), e.IsVisible, ref _rightPanelLastWidth);
                break;
            case DockPosition.Bottom:
                var centerGrid = grid.Children.FirstOrDefault(c => c is Grid g && Grid.GetColumn(g) == 2) as Grid;
                if (centerGrid != null)
                {
                    ToggleRow(centerGrid, 2, _mainContent.GetBottomSplitter(), e.IsVisible, ref _bottomPanelLastHeight);
                }
                break;
            case DockPosition.Content:
                ToggleColumn(grid, 2, null, e.IsVisible, ref _contentPanelLastWidth);
                break;
        }
    }

    private const double SidebarIconStripWidth = 40;

    private static void ToggleSidebarColumn(Grid grid, int columnIndex, GridSplitter? splitter, bool isVisible, ref double lastSize)
    {
        var col = grid.ColumnDefinitions[columnIndex];
        if (isVisible)
        {
            col.Width = new GridLength(lastSize > SidebarIconStripWidth ? lastSize : 280);
            col.MinWidth = SidebarIconStripWidth + 140;
            col.ClearValue(ColumnDefinition.MaxWidthProperty);
            if (splitter != null) splitter.IsVisible = true;
        }
        else
        {
            lastSize = col.ActualWidth > SidebarIconStripWidth ? col.ActualWidth : 280;
            col.MinWidth = SidebarIconStripWidth;
            col.MaxWidth = SidebarIconStripWidth;
            col.Width = new GridLength(SidebarIconStripWidth);
            if (splitter != null) splitter.IsVisible = false;
        }
    }

    private static void ToggleColumn(Grid grid, int columnIndex, GridSplitter? splitter, bool isVisible, ref double lastSize)
    {
        var col = grid.ColumnDefinitions[columnIndex];
        if (isVisible)
        {
            col.Width = new GridLength(lastSize > 0 ? lastSize : 250);
            col.MinWidth = columnIndex == 0 ? 180 : 220;
            col.ClearValue(ColumnDefinition.MaxWidthProperty);
            if (splitter != null) splitter.IsVisible = true;
        }
        else
        {
            lastSize = col.ActualWidth > 0 ? col.ActualWidth : 250;
            col.MinWidth = 0;
            col.MaxWidth = 0;
            col.Width = new GridLength(0);
            if (splitter != null) splitter.IsVisible = false;
        }
    }

    private static void ToggleRow(Grid grid, int rowIndex, GridSplitter? splitter, bool isVisible, ref double lastSize)
    {
        var row = grid.RowDefinitions[rowIndex];
        if (isVisible)
        {
            row.Height = new GridLength(lastSize > 0 ? lastSize : 200);
            row.MinHeight = 100;
            row.ClearValue(RowDefinition.MaxHeightProperty);
            if (splitter != null) splitter.IsVisible = true;
        }
        else
        {
            lastSize = row.ActualHeight > 0 ? row.ActualHeight : 200;
            row.MinHeight = 0;
            row.MaxHeight = 0;
            row.Height = new GridLength(0);
            if (splitter != null) splitter.IsVisible = false;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ShowOpenFolderDialog = ShowOpenFolderDialogAsync;
            viewModel.ShowOpenFileDialog = ShowOpenFileDialogAsync;
        }
    }

    private async Task<string?> ShowOpenFolderDialogAsync(string? _)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择项目文件夹",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder == null) return null;

        return folder.Path.LocalPath;
    }

    private async Task<string?> ShowOpenFileDialogAsync(string? _)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开文件",
            AllowMultiple = false
        });

        var file = files.FirstOrDefault();
        return file?.Path.LocalPath;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            var localPath = ExtractDroppedPath(e);
            if (localPath != null)
            {
                if (Directory.Exists(localPath))
                {
                    await vm.OpenFolderAsync(localPath);
                }
            }
            e.Handled = true;
        }
    }

    private static string? ExtractDroppedPath(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            if (item.Formats.Contains(DataFormat.File))
            {
                var raw = item.TryGetRaw(DataFormat.File);
                if (raw is IStorageItem storageItem)
                {
                    return storageItem.Path.LocalPath;
                }
            }
        }
        return null;
    }

    private async void OnIndexFileRequested(IndexFileRequestedEvent e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.CurrentProject == null) return;

        var result = await ShowConfirmDialogAsync(
            $"是否将文件 \"{e.FileName}\" 索引到项目数据库？\n索引后可通过SQL查询此数据。");
        if (result != true) return;

        var indexService = new FileIndexService(GetSqliteService());
        var indexResult = await indexService.IndexCsvAsync(e.FilePath, vm.CurrentProject.DatabasePath);

        var eventBus = GetEventBus();
        if (indexResult.IsSuccess)
        {
            eventBus.Publish(new StatusMessageEvent($"索引完成: {indexResult.RowCount} 行已写入表 {indexResult.TableName}"));
        }
        else
        {
            eventBus.Publish(new StatusMessageEvent($"索引失败: {indexResult.Error}"));
        }
    }

    private async Task<bool> ShowExitConfirmDialogAsync() =>
        await AppDialogService.ConfirmAsync(
            this,
            "确定要退出应用程序吗？",
            title: "退出",
            warning: true);

    private async Task<bool> ShowConfirmDialogAsync(string message, string title = "确认") =>
        await AppDialogService.ConfirmAsync(this, message, title);
}
