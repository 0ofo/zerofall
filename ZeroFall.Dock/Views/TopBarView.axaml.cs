using System;
using System.Linq;
using Avalonia.Controls;
using ZeroFall.Dock.ViewModels;

namespace ZeroFall.Dock.Views;

public partial class TopBarView : UserControl
{
    public TopBarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not TopBarViewModel vm) return;
        BuildMenu(vm);
        vm.MenuGroups.CollectionChanged += (_, _) => BuildMenu(vm);
    }

    private void BuildMenu(TopBarViewModel vm)
    {
        var menu = this.FindControl<Menu>("TopBarMenu");
        if (menu == null) return;

        menu.Items.Clear();
        foreach (var group in vm.MenuGroups.OrderBy(g => g.Order))
        {
            var menuItem = new MenuItem { Header = group.Header, Padding = new Avalonia.Thickness(6, 2) };
            foreach (var item in group.Items.OrderBy(i => i.Order))
            {
                if (item.IsSeparator)
                {
                    menuItem.Items.Add(new Separator());
                }
                else
                {
                    menuItem.Items.Add(new MenuItem
                    {
                        Header = item.Header,
                        Command = item.Command,
                        CommandParameter = item.CommandParameter,
                        IsVisible = true
                    });
                }
            }
            menu.Items.Add(menuItem);
        }
    }
}
