using Avalonia;
using Avalonia.Controls;
using ZeroFall.Dock.ViewModels;

namespace ZeroFall.Dock.Views;

public partial class TextInputDialogView : UserControl
{
    public TextInputDialogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is TextInputDialogViewModel vm)
            InputBox.MinHeight = vm.Multiline ? 120 : 0;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is not TextInputDialogViewModel vm || vm.SelectionEnd <= 0)
            return;

        InputBox.SelectionStart = 0;
        InputBox.SelectionEnd = vm.SelectionEnd;
        InputBox.Focus();
    }
}
