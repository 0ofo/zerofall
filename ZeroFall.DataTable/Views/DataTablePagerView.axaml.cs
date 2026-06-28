using System;
using System.ComponentModel;
using Avalonia.Controls;
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

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as DataTableViewModel;
        if (_vm != null)
            _vm.PropertyChanged += OnVmPropertyChanged;
        UpdateVisibility();
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
