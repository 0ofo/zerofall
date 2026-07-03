using System;
using System.Collections.Generic;
using Avalonia.Collections;
using Avalonia.Controls;
using ZeroFall.Platform.Registries;
using ZeroFall.Settings.ViewModels;

namespace ZeroFall.Settings.Helpers;

/// <summary>
/// 设置窗口 TabbedPage 宿主：按需加载标签页，关闭时释放 ViewModel 与视图，避免 EventBus 等强引用泄漏。
/// </summary>
internal sealed class SettingsTabsHost : IDisposable
{
    private readonly TabbedPage _tabs;
    private readonly ISettingsRegistry _registry;
    private readonly Dictionary<int, SettingsTabEntry> _entriesByIndex = new();
    private IReadOnlyList<SettingsPageEntry>? _pageDefinitions;
    private AvaloniaList<Page>? _pageList;
    private EventHandler<PageSelectionChangedEventArgs>? _selectionChangedHandler;
    private bool _disposed;

    public SettingsTabsHost(TabbedPage tabs, ISettingsRegistry registry)
    {
        _tabs = tabs;
        _registry = registry;
    }

    public void Build(string? targetTabTitle)
    {
        ReleaseAll();

        _pageDefinitions = _registry.GetPages();
        _pageList = new AvaloniaList<Page>();
        var targetIndex = 0;

        for (var i = 0; i < _pageDefinitions.Count; i++)
        {
            var page = _pageDefinitions[i];
            _pageList.Add(new ContentPage
            {
                Header = page.Title,
                Icon = IconHelper.GetIcon(page.IconKey),
                Content = new ContentControl()
            });

            if (!string.IsNullOrEmpty(targetTabTitle) && page.Title == targetTabTitle)
                targetIndex = i;
        }

        _tabs.Pages = _pageList;

        _selectionChangedHandler = (_, _) =>
        {
            var index = _tabs.SelectedIndex;
            if (index >= 0)
                EnsureTabLoaded(index);
        };
        _tabs.SelectionChanged += _selectionChangedHandler;
        if (_pageList.Count > 0)
        {
            _tabs.SelectedIndex = targetIndex;
            EnsureTabLoaded(targetIndex);
        }
    }

    public bool TrySaveSelected(SettingsWindowViewModel vm)
    {
        var idx = _tabs.SelectedIndex;
        if (idx < 0 || _pageDefinitions is null || idx >= _pageDefinitions.Count)
        {
            vm.SetFooter("未选中设置页", isError: true);
            return false;
        }

        EnsureTabLoaded(idx);

        if (!_entriesByIndex.TryGetValue(idx, out var entry))
        {
            vm.SetFooter("设置页加载失败", isError: true);
            return false;
        }

        SettingsBindingHelper.CommitPendingEdits(entry.Root);

        if (entry.Saveable == null)
        {
            vm.SetFooter(string.Empty, isError: false);
            return true;
        }

        if (entry.Saveable.TrySave())
        {
            vm.SetFooter(string.Empty, isError: false);
            return true;
        }

        vm.SetFooter(entry.Saveable.LastSaveError ?? $"{entry.Title} 保存失败", isError: true);
        return false;
    }

    private void EnsureTabLoaded(int index)
    {
        if (_pageDefinitions is null || _pageList is null || index < 0 || index >= _pageList.Count)
            return;

        if (_entriesByIndex.ContainsKey(index))
            return;

        var page = _pageDefinitions[index];
        var root = page.CreateView() as Control ?? new ContentControl();
        var entry = new SettingsTabEntry
        {
            Title = page.Title,
            Root = root,
            Saveable = root.DataContext as ISettingsSaveable
        };
        _entriesByIndex[index] = entry;

        if (_pageList[index] is ContentPage contentPage)
            contentPage.Content = root;
    }

    public void ReleaseAll()
    {
        if (_selectionChangedHandler != null)
        {
            _tabs.SelectionChanged -= _selectionChangedHandler;
            _selectionChangedHandler = null;
        }

        foreach (var entry in _entriesByIndex.Values)
            ReleaseTabEntry(entry);

        _entriesByIndex.Clear();
        _pageDefinitions = null;
        _pageList = null;
        _tabs.Pages = new AvaloniaList<Page>();
    }

    private static void ReleaseTabEntry(SettingsTabEntry entry)
    {
        if (entry.Root.DataContext is IDisposable disposable)
            disposable.Dispose();

        entry.Root.DataContext = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseAll();
    }
}
