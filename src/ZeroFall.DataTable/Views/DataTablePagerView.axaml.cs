using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ZeroFall.Base.Data;
using ZeroFall.DataTable.ViewModels;

namespace ZeroFall.DataTable.Views;

public partial class DataTablePagerView : UserControl
{
    private DataTableViewModel? _vm;

    public DataTablePagerView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WireCsvExportIfNeeded();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnwireCsvExportIfNeeded();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        UnwireCsvExportIfNeeded();

        _vm = DataContext as DataTableViewModel;
        if (_vm != null)
            _vm.PropertyChanged += OnVmPropertyChanged;

        UpdateVisibility();
        WireCsvExportIfNeeded();
    }

    private void WireCsvExportIfNeeded()
    {
        if (_vm == null || VisualRoot == null)
            return;

        DataTableCsvExportHost.Attach(_vm, this);
    }

    private void UnwireCsvExportIfNeeded()
    {
        if (_vm == null)
            return;

        DataTableCsvExportHost.Detach(_vm);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DataTableViewModel.DisplayMode)
            or nameof(DataTableViewModel.TotalRows))
            UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        IsVisible = _vm is { DisplayMode: DataTableDisplayMode.UserPaged };
    }
}
