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

    private static readonly string[] AllAssetColumnHeaders =
    {
        "URL", "IP", "Port", "Protocol", "Title", "Domain",
        "Country", "CountryCode", "Province", "City", "Org", "ISP", "OS",
        "Server", "Banner", "StatusCode", "Product", "ProductCategory", "Version",
        "CertIssuer", "CertIssuerOrg", "CertSubject", "CertSubjectOrg",
        "CertSn", "CertNotBefore", "CertNotAfter", "CertDomain",
        "ICP", "ASNumber",
        "Header", "HeaderHash", "BannerHash", "BannerFid",
        "JARM", "TlsJa3s", "TlsVersion",
        "CNAME", "Vuln", "BaseProtocol", "UpdatedAt",
        "Link", "Cert", "Longitude", "Latitude",
        "IpTag", "IsRiskProtocol", "IcpException", "IsWeb", "CertSha256", "AssetTag",
        "Company"
    };

    private static readonly int[] DefaultDisplayIndices =
    {
        1, 2, 3, 6, 8, 9, 0, 5, 12, 13, 27, 4, 40, 15
    };

    public static DataTableViewModel BuildFromAssetRecon(string sourceName, string query,
        IReadOnlyList<UnifiedAssetRow> rows, int totalCount)
    {
        var title = $"{sourceName}: {query}";
        if (title.Length > 30) title = title[..30] + "...";

        var columns = new string[DefaultDisplayIndices.Length];
        for (var i = 0; i < DefaultDisplayIndices.Length; i++)
            columns[i] = AllAssetColumnHeaders[DefaultDisplayIndices[i]];

        var objectRows = new List<IReadOnlyList<object?>>();

        foreach (var row in rows)
        {
            var allValues = new object?[]
            {
                row.Url, row.Ip, row.Port, row.Protocol, row.Title, row.Domain,
                row.Country, row.CountryCode, row.Province, row.City, row.Org, row.Isp, row.Os,
                row.Server, row.Banner, row.StatusCode, row.Product, row.ProductCategory, row.Version,
                row.CertIssuer, row.CertIssuerOrg, row.CertSubject, row.CertSubjectOrg,
                row.CertSn, row.CertNotBefore, row.CertNotAfter, row.CertDomain,
                row.Icp, row.AsNumber,
                row.Header, row.HeaderHash, row.BannerHash, row.BannerFid,
                row.Jarm, row.TlsJa3s, row.TlsVersion,
                row.Cname, row.Vuln, row.BaseProtocol, row.UpdatedAt,
                row.Link, row.Cert, row.Longitude, row.Latitude,
                row.IpTag, row.IsRiskProtocol, row.IcpException, row.IsWeb, row.CertSha256, row.AssetTag,
                row.Company
            };

            var displayValues = new object?[DefaultDisplayIndices.Length];
            for (var i = 0; i < DefaultDisplayIndices.Length; i++)
                displayValues[i] = allValues[DefaultDisplayIndices[i]];

            objectRows.Add(displayValues);
        }

        return Build(title, string.Empty, string.Empty, columns, objectRows, totalCount);
    }

    public static DataTableViewModel BuildFromSqlResult(string title, string filePath,
        IReadOnlyList<string> columns, IReadOnlyList<string[]> rows, long totalRows)
    {
        if (title.Length > 30) title = title[..30] + "...";

        return BuildFromStrings(title, filePath, string.Empty, columns, rows, totalRows);
    }
}
