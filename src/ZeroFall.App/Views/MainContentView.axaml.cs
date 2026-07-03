using Avalonia.Controls;
using ZeroFall.Dock.Views;

namespace ZeroFall.App.Views;

public partial class MainContentView : UserControl
{
    public MainContentView()
    {
        InitializeComponent();
    }

    public Grid? GetMainGrid() => this.FindControl<Grid>("MainGrid");
    public GridSplitter? GetLeftSplitter() => this.FindControl<GridSplitter>("LeftSplitter");
    public GridSplitter? GetBottomSplitter() => this.FindControl<GridSplitter>("BottomSplitter");
    public GridSplitter? GetRightSplitter() => this.FindControl<GridSplitter>("RightSplitter");
    public DockTabControl? GetLeftDockTabControl() => this.FindControl<DockTabControl>("LeftDockTab");
}
