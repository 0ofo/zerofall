using System;
using System.Collections.Generic;
using System.Linq;
using ZeroFall.DataTable.ViewModels;
using ZeroFall.Platform.Services;

namespace ZeroFall.DataTable.Services;

public static class DataTableBuilder
{
    public static DataTableViewModel Build(string title, string filePath, string tableName,
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows, long totalRows)
    {
        var dtvm = new DataTableViewModel
        {
            Title = title,
            FilePath = filePath,
            TableName = tableName,
            RowCount = rows.Count,
            ColumnCount = columns.Count,
            TotalRows = totalRows,
            CurrentPage = 1,
            PageSize = 200
        };

        for (var i = 0; i < columns.Count; i++)
        {
            dtvm.Columns.Add(new DataColumnViewModel { Header = columns[i], Index = i });
        }

        var lineIndex = 1;
        foreach (var row in rows)
        {
            var rowVm = new DataRowViewModel { LineNumber = lineIndex++ };
            for (var i = 0; i < columns.Count; i++)
            {
                rowVm.Values.Add(i < row.Count ? row[i]?.ToString() ?? string.Empty : string.Empty);
            }
            dtvm.Rows.Add(rowVm);
        }

        return dtvm;
    }

    public static DataTableViewModel BuildFromStrings(string title, string filePath, string tableName,
        IReadOnlyList<string> columns, IReadOnlyList<string[]> rows, long totalRows)
    {
        var dtvm = new DataTableViewModel
        {
            Title = title,
            FilePath = filePath,
            TableName = tableName,
            RowCount = rows.Count,
            ColumnCount = columns.Count,
            TotalRows = totalRows,
            CurrentPage = 1,
            PageSize = 200
        };

        for (var i = 0; i < columns.Count; i++)
        {
            dtvm.Columns.Add(new DataColumnViewModel { Header = columns[i], Index = i });
        }

        var lineIndex = 1;
        foreach (var row in rows)
        {
            var rowVm = new DataRowViewModel { LineNumber = lineIndex++ };
            for (var i = 0; i < columns.Count; i++)
            {
                rowVm.Values.Add(i < row.Length ? row[i] : string.Empty);
            }
            dtvm.Rows.Add(rowVm);
        }

        return dtvm;
    }

    public static DataTableViewModel BuildFromAssetRecon(string sourceName, string query,
        IReadOnlyList<UnifiedAssetRow> rows, int totalCount)
    {
        var title = $"{sourceName}: {query}";
        if (title.Length > 30) title = title[..30] + "...";

        var objectRows = new List<IReadOnlyList<object?>>();

        foreach (var row in rows)
            objectRows.Add(AssetReconFieldCatalog.ReadDefaultValues(row));

        return Build(title, string.Empty, string.Empty, AssetReconFieldCatalog.DefaultHeaders, objectRows, totalCount);
    }

    public static DataTableViewModel BuildFromSqlResult(string title, string filePath,
        IReadOnlyList<string> columns, IReadOnlyList<string[]> rows, long totalRows)
    {
        if (title.Length > 30) title = title[..30] + "...";

        return BuildFromStrings(title, filePath, string.Empty, columns, rows, totalRows);
    }
}
