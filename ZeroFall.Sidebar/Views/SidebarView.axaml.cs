using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;
using ZeroFall.Platform.Models;
using ZeroFall.Sidebar.ViewModels;
using ZeroFall.Sidebar.Views;
using Ursa.Controls;

namespace ZeroFall.Sidebar.Views;

public partial class SidebarView : UserControl, IDockTabToolPanelProvider
{
    private IEventBus? _eventBus;
    private StackPanel? _dockToolPanel;

    public SidebarView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void SetEventBus(IEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    public Control? GetDockTabToolPanel()
    {
        _dockToolPanel ??= CreateDockToolPanel();
        _dockToolPanel.DataContext = DataContext;
        return _dockToolPanel;
    }

    private StackPanel CreateDockToolPanel()
    {
        var panel = DockTabToolPanelHelper.CreateHorizontalPanel();
        DockTabToolPanelHelper.AddIconCommandButton(
            panel,
            "SemiIconRefresh",
            nameof(SidebarViewModel.RefreshCommand),
            tooltip: "刷新");

        var openFolder = new Button
        {
            Classes = { "Small" },
            Padding = new Thickness(2),
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new PathIcon
            {
                Data = DockTabToolPanelHelper.ResolveIcon("SemiIconFolderOpen"),
                Width = 14,
                Height = 14
            }
        };
        ToolTip.SetTip(openFolder, "打开文件夹");
        openFolder.Click += (_, _) => _eventBus?.Publish(new OpenFolderRequestedEvent());
        panel.Children.Add(openFolder);

        var newMySql = new Button
        {
            Classes = { "Small" },
            Padding = new Thickness(2),
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new PathIcon
            {
                Data = DockTabToolPanelHelper.ResolveIcon("SemiIconServer"),
                Width = 14,
                Height = 14
            }
        };
        ToolTip.SetTip(newMySql, "新建 MySQL 连接");
        newMySql.Click += OnCreateMySqlConnectionClick;
        panel.Children.Add(newMySql);

        return panel;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SidebarViewModel viewModel && viewModel.SelectedTreeNode != null)
        {
            viewModel.TreeNodeSelectedCommand.Execute(viewModel.SelectedTreeNode);
        }
    }

    private void OnTreeNodeExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem item && item.DataContext is TreeNodeViewModel node)
        {
            if (DataContext is SidebarViewModel viewModel)
            {
                viewModel.TreeNodeExpandedCommand.Execute(node);
            }
        }
    }

    #region Right-click menu

    private void OnTreeContextMenuOpening(object? sender, EventArgs e)
    {
        if (DataContext is not SidebarViewModel viewModel) return;
        if (SidebarTree.ContextMenu is not ContextMenu menu) return;

        var items = BuildContextMenuItems(viewModel);
        menu.ItemsSource = items;
    }

    private List<Control> BuildContextMenuItems(SidebarViewModel viewModel)
    {
        var node = viewModel.SelectedTreeNode;
        var items = new List<Control>();

        if (node == null)
        {
            items.Add(NewMenuItem("新建文件", (_, _) => viewModel.CreateFileCommand.Execute(null)));
            items.Add(NewMenuItem("新建文件夹", (_, _) => viewModel.CreateFolderCommand.Execute(null)));
            items.Add(NewMenuItem("新建 MySQL 连接", OnCreateMySqlConnectionClick));
            items.Add(new Separator());
            items.Add(NewMenuItem("刷新", (_, _) => viewModel.RefreshCommand.Execute(null)));
            return items;
        }

        switch (node.NodeType)
        {
            case TreeNodeType.Folder:
                items.Add(NewMenuItem("新建文件", (_, _) => viewModel.CreateFileCommand.Execute(node)));
                items.Add(NewMenuItem("新建文件夹", (_, _) => viewModel.CreateFolderCommand.Execute(node)));
                items.Add(NewMenuItem("新建 MySQL 连接", OnCreateMySqlConnectionClick));
                items.Add(new Separator());
                items.Add(NewMenuItem("重命名", OnRenameClick));
                items.Add(NewMenuItem("删除", OnDeleteClick));
                items.Add(new Separator());
                items.Add(NewMenuItem("刷新", (_, _) => viewModel.RefreshCommand.Execute(null)));
                break;

            case TreeNodeType.DataSource:
                if (node.DataSourceType is DataSourceType.Sqlite or DataSourceType.MySql)
                {
                    items.Add(NewMenuItem("新建查询", OnNewQueryClick));
                    items.Add(new Separator());
                }
                if (node.DataSourceType == DataSourceType.MySql)
                {
                    items.Add(NewMenuItem("编辑连接", (_, _) => viewModel.OpenConnectionConfigCommand.Execute(node)));
                    items.Add(NewMenuItem("测试连接", (_, _) => viewModel.TestConnectionCommand.Execute(node)));
                    items.Add(NewMenuItem("刷新表列表", (_, _) => viewModel.RefreshConnectionTablesCommand.Execute(node)));
                    items.Add(new Separator());
                }
                items.Add(NewMenuItem("重命名", OnRenameClick));
                items.Add(NewMenuItem("删除", OnDeleteClick));
                break;

            case TreeNodeType.Table:
                if (node.Parent != null)
                {
                    items.Add(NewMenuItem("新建查询", OnNewQueryClick));
                    items.Add(new Separator());
                }
                break;

            case TreeNodeType.File:
                if (node.DataSourceType == DataSourceType.Csv)
                {
                    items.Add(NewMenuItem("索引到数据库", OnIndexCsvClick));
                    items.Add(new Separator());
                }
                items.Add(NewMenuItem("重命名", OnRenameClick));
                items.Add(NewMenuItem("删除", OnDeleteClick));
                break;
        }

        if (node.NodeType != TreeNodeType.Folder || !string.IsNullOrEmpty(node.FilePath))
        {
            items.Add(new Separator());
            items.Add(NewMenuItem("打开所在文件夹", OnOpenContainingFolderClick));
        }

        return items;
    }

    private static MenuItem NewMenuItem(string header, EventHandler<RoutedEventArgs> click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        return item;
    }

    #endregion

    #region Rename dialog

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SidebarViewModel viewModel) return;
        var node = viewModel.SelectedTreeNode;
        if (node == null || node.NodeType is TreeNodeType.Table or TreeNodeType.Database || string.IsNullOrEmpty(node.FilePath)) return;

        var oldName = node.Name;
        var newName = await ShowRenameDialogAsync(oldName);
        if (string.IsNullOrEmpty(newName) || string.Equals(oldName, newName, StringComparison.Ordinal)) return;

        node.Name = newName;
        await viewModel.CommitRenameCommand.ExecuteAsync(node);
    }

    private async Task<string?> ShowRenameDialogAsync(string currentName)
    {
        var dialog = new Window
        {
            Title = "重命名",
            Width = 320,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var textBox = new TextBox
        {
            Text = currentName,
            Margin = new Thickness(16, 16, 16, 8),
            SelectionStart = 0,
            SelectionEnd = currentName.Contains('.') ? currentName.LastIndexOf('.') : currentName.Length
        };

        var okButton = new Button { Content = "确定", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 16, 16) };
        var cancelButton = new Button { Content = "取消", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 16, 16) };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 16, 16),
            Children = { okButton, cancelButton }
        };

        var panel = new StackPanel
        {
            Children = { textBox, buttonPanel }
        };

        dialog.Content = panel;

        string? result = null;
        okButton.Click += (_, _) =>
        {
            result = textBox.Text;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
        {
            await dialog.ShowDialog(owner);
        }

        return result;
    }

    private async void OnCreateMySqlConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SidebarViewModel viewModel) return;
        if (!viewModel.HasProject || string.IsNullOrEmpty(viewModel.ProjectDirectory)) return;

        var node = viewModel.SelectedTreeNode;
        var targetDir = node?.NodeType == TreeNodeType.Folder && !string.IsNullOrEmpty(node.FilePath)
            ? node.FilePath
            : viewModel.ProjectDirectory;

        var config = await ShowCreateMySqlDialogAsync();
        if (config == null) return;

        await viewModel.CreateMySqlConnectionAsync(targetDir, config);
    }

    private async Task<MySqlConnectionConfig?> ShowCreateMySqlDialogAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
            return null;

        var vm = new MySqlConnectionDialogViewModel();
        var result = await Dialog.ShowStandardAsync<MySqlConnectionDialog, MySqlConnectionDialogViewModel>(
            vm,
            owner,
            new DialogOptions
            {
                Title = "新建 MySQL 连接",
                Button = DialogButton.OKCancel,
                CanResize = false,
                StartupLocation = WindowStartupLocation.CenterOwner,
            });

        return result == DialogResult.OK ? vm.ToConfig() : null;
    }

    #endregion

    #region Right-click menu actions

    private void OnNewQueryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SidebarViewModel viewModel && viewModel.SelectedTreeNode != null)
        {
            var node = viewModel.SelectedTreeNode;

            if (node.NodeType == TreeNodeType.DataSource &&
                node.DataSourceType is DataSourceType.Sqlite or DataSourceType.MySql)
            {
                _eventBus?.Publish(new NewQueryRequestedEvent(node.FilePath, node.Name));
            }
            else if (node.NodeType == TreeNodeType.Table)
            {
                var connectionNode = FindDataSourceAncestor(node);
                if (connectionNode != null)
                    _eventBus?.Publish(new NewQueryRequestedEvent(node.FilePath, connectionNode.Name));
            }
        }
    }

    private void OnIndexCsvClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SidebarViewModel viewModel && viewModel.SelectedTreeNode != null)
        {
            var node = viewModel.SelectedTreeNode;
            _eventBus?.Publish(new IndexFileRequestedEvent(node.FilePath, node.Name));
        }
    }

    private void OnOpenContainingFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SidebarViewModel viewModel) return;
        var node = viewModel.SelectedTreeNode;
        if (node == null || string.IsNullOrEmpty(node.FilePath)) return;

        var path = node.NodeType == TreeNodeType.Folder
            ? node.FilePath
            : Path.GetDirectoryName(node.FilePath);

        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch
        {
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SidebarViewModel viewModel) return;
        var node = viewModel.SelectedTreeNode;
        if (node == null || node.NodeType is TreeNodeType.Table or TreeNodeType.Database) return;

        await viewModel.DeleteNodeCommand.ExecuteAsync(node);
    }

    #endregion

    #region Drag and Drop

    private static readonly DataFormat<string> TreeNodeFormat = DataFormat.CreateInProcessFormat<string>("ZeroFall.TreeNode");

    private TreeNodeViewModel? _dragSourceNode;
    private bool _isDragging;
    private Point _dragStartPoint;
    private PointerPressedEventArgs? _dragStartArgs;
    private TreeViewItem? _currentDropTarget;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SidebarTree.ContextMenu = new ContextMenu();
        SidebarTree.ContextMenu.Opening += OnTreeContextMenuOpening;
        DragDrop.AddDragOverHandler(SidebarTree, OnDragOver);
        DragDrop.AddDropHandler(SidebarTree, OnDrop);
        SidebarTree.AddHandler(TreeViewItem.ExpandedEvent, OnTreeNodeExpanded);
        SidebarTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        SidebarTree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        SidebarTree.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
        SidebarTree.AddHandler(InputElement.DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Tunnel);
    }

    private async void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SidebarViewModel viewModel)
            return;

        var node = GetTreeNodeFromSource(e.Source);
        if (node is null)
            return;

        if (node.NodeType != TreeNodeType.DataSource
            || node.DataSourceType is not (DataSourceType.MySql or DataSourceType.Sqlite))
            return;

        e.Handled = true;
        await viewModel.TreeNodeDoubleClickedCommand.ExecuteAsync(node);
    }

    private TreeNodeViewModel? GetTreeNodeFromSource(object? source)
    {
        if (source is not Control control)
            return null;

        var current = control;
        while (current != null && current != SidebarTree)
        {
            if (current.DataContext is TreeNodeViewModel node)
                return node;
            current = current.Parent as Control;
        }

        return null;
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isDragging = false;
        _dragSourceNode = null;
        _dragStartArgs = null;

        var point = e.GetCurrentPoint(SidebarTree);
        if (!point.Properties.IsLeftButtonPressed) return;

        _dragStartPoint = e.GetPosition(SidebarTree);
        _dragStartArgs = e;
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging || _dragSourceNode != null) return;
        if (_dragStartArgs == null) return;

        var point = e.GetCurrentPoint(SidebarTree);
        if (!point.Properties.IsLeftButtonPressed) return;

        var currentPos = e.GetPosition(SidebarTree);
        var delta = currentPos - _dragStartPoint;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;

        if (DataContext is not SidebarViewModel viewModel) return;
        if (viewModel.SelectedTreeNode == null) return;

        var node = viewModel.SelectedTreeNode;
        if (node.NodeType is TreeNodeType.Table or TreeNodeType.Database) return;

        _dragSourceNode = node;
        _isDragging = true;

        ShowDragGhost(node.Name, e.GetPosition(this));

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(TreeNodeFormat, node.Id));

        await DragDrop.DoDragDropAsync(_dragStartArgs, data, DragDropEffects.Move);

        _isDragging = false;
        _dragSourceNode = null;
        ClearDropHighlight();
        HideDragGhost();
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        _dragSourceNode = null;
        _dragStartArgs = null;
        ClearDropHighlight();
        HideDragGhost();
    }

    private TreeNodeViewModel? FindNodeById(ObservableCollection<TreeNodeViewModel> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id) return node;
            var found = FindNodeById(node.Children, id);
            if (found != null) return found;
        }
        return null;
    }

    private void SetDropHighlight(TreeViewItem? tvi)
    {
        if (_currentDropTarget == tvi) return;

        ClearDropHighlight();

        _currentDropTarget = tvi;
        if (tvi != null)
        {
            tvi.Background = new SolidColorBrush(Color.FromArgb(40, 0, 120, 212));
            tvi.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
            tvi.BorderThickness = new Thickness(2);
            tvi.Classes.Add("drag-over");
        }
    }

    private void ClearDropHighlight()
    {
        if (_currentDropTarget != null)
        {
            _currentDropTarget.Background = Brushes.Transparent;
            _currentDropTarget.BorderBrush = Brushes.Transparent;
            _currentDropTarget.BorderThickness = new Thickness(0);
            _currentDropTarget.Classes.Remove("drag-over");
            _currentDropTarget = null;
        }
    }

    private void ShowDragGhost(string name, Point position)
    {
        DragGhostText.Text = name;
        DragGhost.IsVisible = true;
        DragGhost.Margin = new Thickness(position.X + 12, position.Y + 12, 0, 0);
    }

    private void UpdateDragGhostPosition(Point position)
    {
        if (DragGhost.IsVisible)
        {
            DragGhost.Margin = new Thickness(position.X + 12, position.Y + 12, 0, 0);
        }
    }

    private void HideDragGhost()
    {
        DragGhost.IsVisible = false;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        UpdateDragGhostPosition(e.GetPosition(this));

        if (DataContext is not SidebarViewModel viewModel) return;

        if (e.DataTransfer.Contains(TreeNodeFormat))
        {
            var sourceId = e.DataTransfer.TryGetValue(TreeNodeFormat);
            if (sourceId == null) return;

            var sourceNode = FindNodeById(viewModel.RootChildren, sourceId);
            if (sourceNode == null) return;

            var (targetNode, targetTvi) = GetTreeViewItemNodeWithItem(e);

            if (viewModel.CanDropOnNode(sourceNode, targetNode))
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
                SetDropHighlight(targetTvi);
            }
            else
            {
                ClearDropHighlight();
            }
            return;
        }

        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        ClearDropHighlight();

        if (DataContext is not SidebarViewModel viewModel) return;

        if (e.DataTransfer.Contains(TreeNodeFormat))
        {
            var sourceId = e.DataTransfer.TryGetValue(TreeNodeFormat);
            if (sourceId == null) return;

            var sourceNode = FindNodeById(viewModel.RootChildren, sourceId);
            if (sourceNode == null) return;

            var (targetNode, _) = GetTreeViewItemNodeWithItem(e);

            if (viewModel.CanDropOnNode(sourceNode, targetNode))
            {
                viewModel.MoveNodeToFolderCommand.Execute((sourceNode, targetNode));
            }

            e.Handled = true;
            return;
        }

        if (e.DataTransfer.Contains(DataFormat.File))
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            var filePaths = files
                .Where(f => f.Path is not null)
                .Select(f => f.Path.LocalPath)
                .ToList();

            if (filePaths.Count > 0)
            {
                viewModel.DropFilesCommand.Execute(filePaths);
            }

            e.Handled = true;
        }
    }

    private (TreeNodeViewModel? Node, TreeViewItem? Item) GetTreeViewItemNodeWithItem(DragEventArgs e)
    {
        var position = e.GetPosition(SidebarTree);
        var hitResult = SidebarTree.InputHitTest(position) as Visual;

        while (hitResult != null)
        {
            if (hitResult is TreeViewItem tvi && tvi.DataContext is TreeNodeViewModel node)
                return (node, tvi);
            hitResult = hitResult.GetVisualParent();
        }

        return (null, null);
    }

    private static TreeNodeViewModel? FindDataSourceAncestor(TreeNodeViewModel node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current.NodeType == TreeNodeType.DataSource)
                return current;
            current = current.Parent;
        }

        return null;
    }

    #endregion
}
