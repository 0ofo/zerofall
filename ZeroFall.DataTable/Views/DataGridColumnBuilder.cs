using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using ZeroFall.DataTable.ViewModels;

namespace ZeroFall.DataTable.Views;

public static class DataGridColumnBuilder
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(DataRowViewModel))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(DataColumnViewModel))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ObservableCollection<string>))]
    public static void EnsureAotCompatibility() { }

    private static readonly HashSet<string> UrlColumnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "url", "host", "link", "href", "website", "uri", "链接"
    };

    public static bool IsUrlHeader(string? header) =>
        !string.IsNullOrEmpty(header) && UrlColumnNames.Contains(header);

    public static DataGridTextColumn CreateLineNumberColumn()
    {
        return new DataGridTextColumn
        {
            Header = "#",
            Binding = new Binding("LineNumber"),
            IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Auto)
        };
    }

    private static DataGridColumn CreateColumn(string header, int columnIndex, bool isReadOnly, DataTableViewModel? viewModel)
    {
        if (IsUrlHeader(header) && viewModel is not null)
            return CreateUrlColumn(header, columnIndex, interactive: !viewModel.DisableUrlColumns);

        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding($"Values[{columnIndex}]"),
            IsReadOnly = isReadOnly,
            Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells),
            MinWidth = 52
        };
    }

    /// <summary>超长 URL：省略显示，悬停 ToolTip 展示全文；<paramref name="interactive"/> 为 true 时可点击打开链接。</summary>
    private static DataGridColumn CreateUrlColumn(string header, int columnIndex, bool interactive)
    {
        var column = new DataGridTemplateColumn
        {
            Header = header,
            IsReadOnly = true,
            // 默认列宽会随模板内容变宽（等价 Auto），超长 URL 会把列撑满视口，CharacterEllipsis 永远不触发。
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 80
        };

        column.CellTemplate = new FuncDataTemplate<DataRowViewModel>((row, _) =>
        {
            if (row == null || columnIndex >= row.Values.Count)
                return new TextBlock();

            var urlValue = row.Values[columnIndex]?.ToString() ?? string.Empty;
            urlValue = urlValue.Trim();
            if (string.IsNullOrEmpty(urlValue))
                return new TextBlock();

            // 列宽为 * 后仍需在单元格内裁切：Clip + * 列，避免 TextBlock 按字符串无限测量。
            var root = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ClipToBounds = true,
                Child = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                }
            };
            var grid = (Grid)root.Child;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var text = new TextBlock
            {
                Text = urlValue,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            };
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);
            ToolTip.SetTip(root, urlValue);
            if (interactive)
            {
                text.TextDecorations = TextDecorations.Underline;
                text.Cursor = new Cursor(StandardCursorType.Hand);
                text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SemiColorLink");
            }

            return root;
        });

        return column;
    }

    public static bool ColumnsMatchDataTable(DataGrid dataGrid, DataTableViewModel? dataTable)
    {
        if (dataTable is null)
            return dataGrid.Columns.Count == 0 && dataGrid.ItemsSource is null;

        var expectedCount = dataTable.Columns.Count + (dataTable.ShowLineNumberColumn ? 1 : 0);
        if (dataGrid.Columns.Count != expectedCount)
            return false;

        var isReadOnly = !dataTable.CanEdit;
        if (dataGrid.IsReadOnly != isReadOnly)
            return false;

        var colIndex = 0;
        if (dataTable.ShowLineNumberColumn)
        {
            if (dataGrid.Columns[colIndex].Header?.ToString() != "#")
                return false;
            colIndex++;
        }

        for (var i = 0; i < dataTable.Columns.Count; i++, colIndex++)
        {
            var expected = dataTable.Columns[i].Header;
            if (!string.Equals(dataGrid.Columns[colIndex].Header?.ToString(), expected, StringComparison.Ordinal))
                return false;
        }

        return ReferenceEquals(dataGrid.ItemsSource, dataTable.Rows);
    }

    public static void RebuildFromDataTable(DataGrid dataGrid, DataTableViewModel? dataTable)
    {
        EnsureAotCompatibility();

        if (ColumnsMatchDataTable(dataGrid, dataTable))
            return;

        dataGrid.Columns.Clear();

        if (dataTable == null)
        {
            dataGrid.ItemsSource = null;
            return;
        }

        var isReadOnly = !dataTable.CanEdit;
        dataGrid.IsReadOnly = isReadOnly;

        if (dataTable.ShowLineNumberColumn)
            dataGrid.Columns.Add(CreateLineNumberColumn());

        for (var i = 0; i < dataTable.Columns.Count; i++)
        {
            dataGrid.Columns.Add(CreateColumn(dataTable.Columns[i].Header, i, isReadOnly, dataTable));
        }

        // ItemsSource 由 DataTableView.axaml 的 {Binding Rows} 承载；勿在此赋值以免覆盖绑定。
        if (!ReferenceEquals(dataGrid.ItemsSource, dataTable.Rows))
            dataGrid.ItemsSource = dataTable.Rows;
    }

    public static void RebuildFromColumns(DataGrid dataGrid, List<DataColumnViewModel> columns,
        ObservableCollection<DataRowViewModel> rows, bool canEdit = false)
    {
        EnsureAotCompatibility();

        dataGrid.Columns.Clear();

        if (columns.Count == 0)
        {
            dataGrid.ItemsSource = null;
            return;
        }

        var isReadOnly = !canEdit;
        dataGrid.IsReadOnly = isReadOnly;

        dataGrid.Columns.Add(CreateLineNumberColumn());

        for (var i = 0; i < columns.Count; i++)
        {
            var columnIndex = i;
            dataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = columns[i].Header,
                Binding = new Binding($"Values[{columnIndex}]"),
                IsReadOnly = isReadOnly
            });
        }

        dataGrid.ItemsSource = rows;
    }
}
