using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Datafinder.Platform.Services;

public interface IApiIndexService
{
    Task<ApiIndexResult> IndexAssetReconAsync(
        string projectDatabasePath,
        string queryTaskId,
        string source,
        string query,
        IReadOnlyList<UnifiedAssetRow> rows,
        int totalCount);
}

public class UnifiedAssetRow
{
    public string Url { get; init; } = string.Empty;
    public string Ip { get; init; } = string.Empty;
    public string Port { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string CountryCode { get; init; } = string.Empty;
    public string Province { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Org { get; init; } = string.Empty;
    public string Isp { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public string Server { get; init; } = string.Empty;
    public string Banner { get; init; } = string.Empty;
    public string StatusCode { get; init; } = string.Empty;
    public string Product { get; init; } = string.Empty;
    public string ProductCategory { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string CertIssuer { get; init; } = string.Empty;
    public string CertIssuerOrg { get; init; } = string.Empty;
    public string CertSubject { get; init; } = string.Empty;
    public string CertSubjectOrg { get; init; } = string.Empty;
    public string CertSn { get; init; } = string.Empty;
    public string CertNotBefore { get; init; } = string.Empty;
    public string CertNotAfter { get; init; } = string.Empty;
    public string CertDomain { get; init; } = string.Empty;
    public string Icp { get; init; } = string.Empty;
    public string AsNumber { get; init; } = string.Empty;
    public string Header { get; init; } = string.Empty;
    public string HeaderHash { get; init; } = string.Empty;
    public string BannerHash { get; init; } = string.Empty;
    public string BannerFid { get; init; } = string.Empty;
    public string Jarm { get; init; } = string.Empty;
    public string TlsJa3s { get; init; } = string.Empty;
    public string TlsVersion { get; init; } = string.Empty;
    public string Cname { get; init; } = string.Empty;
    public string Vuln { get; init; } = string.Empty;
    public string BaseProtocol { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    public string Link { get; init; } = string.Empty;
    public string Cert { get; init; } = string.Empty;
    public string Longitude { get; init; } = string.Empty;
    public string Latitude { get; init; } = string.Empty;
    public string IpTag { get; init; } = string.Empty;
    public string IsRiskProtocol { get; init; } = string.Empty;
    public string IcpException { get; init; } = string.Empty;
    public string IsWeb { get; init; } = string.Empty;
    public string CertSha256 { get; init; } = string.Empty;
    public string AssetTag { get; init; } = string.Empty;
    public string Company { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string Scene { get; init; } = string.Empty;
    public string District { get; init; } = string.Empty;
    public string XPoweredBy { get; init; } = string.Empty;
    public string QuakeId { get; init; } = string.Empty;
    public int SortOrder { get; set; }
}

public class ApiIndexResult
{
    public bool IsSuccess { get; init; }
    public string? TableName { get; init; }
    public int RowCount { get; init; }
    public string? Error { get; init; }

    public static ApiIndexResult Success(string tableName, int rowCount) => new()
    {
        IsSuccess = true,
        TableName = tableName,
        RowCount = rowCount
    };

    public static ApiIndexResult Failure(string error) => new()
    {
        IsSuccess = false,
        Error = error
    };
}

public class ApiIndexService : IApiIndexService
{
    private const string TableName = "asset_recon_results";

    public async Task<ApiIndexResult> IndexAssetReconAsync(
        string projectDatabasePath,
        string queryTaskId,
        string source,
        string query,
        IReadOnlyList<UnifiedAssetRow> rows,
        int totalCount)
    {
        if (string.IsNullOrEmpty(projectDatabasePath))
            return ApiIndexResult.Failure("项目数据库路径为空");

        try
        {
            await EnsureTableAsync(projectDatabasePath);
            await InsertRowsAsync(projectDatabasePath, queryTaskId, source, query, rows);
            return ApiIndexResult.Success(TableName, rows.Count);
        }
        catch (Exception ex)
        {
            return ApiIndexResult.Failure($"索引失败: {ex.Message}");
        }
    }

    private static readonly string[] NewColumns =
    {
        "url", "province", "isp", "server", "banner", "status_code", "product", "version",
        "cert_issuer", "cert_subject", "icp", "as_number", "header", "jarm",
        "cname", "vuln", "base_protocol", "updated_at", "link", "cert",
        "longitude", "latitude",
        "country_code", "product_category", "cert_issuer_org", "cert_subject_org",
        "cert_sn", "cert_not_before", "cert_not_after", "cert_domain",
        "header_hash", "banner_hash", "banner_fid", "tls_ja3s", "tls_version",
        "ip_tag", "is_risk_protocol", "icp_exception", "is_web", "cert_sha256", "asset_tag",
        "company", "service_name", "scene", "district", "x_powered_by", "quake_id", "sort_order"
    };

    private static async Task EnsureTableAsync(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE IF NOT EXISTS ""{TableName}"" (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                query_task_id TEXT NOT NULL,
                source TEXT NOT NULL,
                query TEXT NOT NULL,
                url TEXT NOT NULL DEFAULT '',
                ip TEXT NOT NULL DEFAULT '',
                port TEXT NOT NULL DEFAULT '',
                protocol TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                domain TEXT NOT NULL DEFAULT '',
                country TEXT NOT NULL DEFAULT '',
                country_code TEXT NOT NULL DEFAULT '',
                province TEXT NOT NULL DEFAULT '',
                city TEXT NOT NULL DEFAULT '',
                org TEXT NOT NULL DEFAULT '',
                isp TEXT NOT NULL DEFAULT '',
                os TEXT NOT NULL DEFAULT '',
                server TEXT NOT NULL DEFAULT '',
                banner TEXT NOT NULL DEFAULT '',
                status_code TEXT NOT NULL DEFAULT '',
                product TEXT NOT NULL DEFAULT '',
                product_category TEXT NOT NULL DEFAULT '',
                version TEXT NOT NULL DEFAULT '',
                cert_issuer TEXT NOT NULL DEFAULT '',
                cert_issuer_org TEXT NOT NULL DEFAULT '',
                cert_subject TEXT NOT NULL DEFAULT '',
                cert_subject_org TEXT NOT NULL DEFAULT '',
                cert_sn TEXT NOT NULL DEFAULT '',
                cert_not_before TEXT NOT NULL DEFAULT '',
                cert_not_after TEXT NOT NULL DEFAULT '',
                cert_domain TEXT NOT NULL DEFAULT '',
                icp TEXT NOT NULL DEFAULT '',
                as_number TEXT NOT NULL DEFAULT '',
                header TEXT NOT NULL DEFAULT '',
                header_hash TEXT NOT NULL DEFAULT '',
                banner_hash TEXT NOT NULL DEFAULT '',
                banner_fid TEXT NOT NULL DEFAULT '',
                jarm TEXT NOT NULL DEFAULT '',
                tls_ja3s TEXT NOT NULL DEFAULT '',
                tls_version TEXT NOT NULL DEFAULT '',
                cname TEXT NOT NULL DEFAULT '',
                vuln TEXT NOT NULL DEFAULT '',
                base_protocol TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT '',
                link TEXT NOT NULL DEFAULT '',
                cert TEXT NOT NULL DEFAULT '',
                longitude TEXT NOT NULL DEFAULT '',
                latitude TEXT NOT NULL DEFAULT '',
                ip_tag TEXT NOT NULL DEFAULT '',
                is_risk_protocol TEXT NOT NULL DEFAULT '',
                icp_exception TEXT NOT NULL DEFAULT '',
                is_web TEXT NOT NULL DEFAULT '',
                cert_sha256 TEXT NOT NULL DEFAULT '',
                asset_tag TEXT NOT NULL DEFAULT '',
                company TEXT NOT NULL DEFAULT '',
                service_name TEXT NOT NULL DEFAULT '',
                scene TEXT NOT NULL DEFAULT '',
                district TEXT NOT NULL DEFAULT '',
                x_powered_by TEXT NOT NULL DEFAULT '',
                quake_id TEXT NOT NULL DEFAULT '',
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            )";
        await command.ExecuteNonQueryAsync();

        await MigrateAsync(connection);

        try
        {
            var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = $"CREATE INDEX IF NOT EXISTS idx_asset_recon_task ON \"{TableName}\" (query_task_id)";
            await idxCmd.ExecuteNonQueryAsync();

            var srcIdx = connection.CreateCommand();
            srcIdx.CommandText = $"CREATE INDEX IF NOT EXISTS idx_asset_recon_source ON \"{TableName}\" (source)";
            await srcIdx.ExecuteNonQueryAsync();

            var ipIdx = connection.CreateCommand();
            ipIdx.CommandText = $"CREATE INDEX IF NOT EXISTS idx_asset_recon_ip ON \"{TableName}\" (ip)";
            await ipIdx.ExecuteNonQueryAsync();

            var domainIdx = connection.CreateCommand();
            domainIdx.CommandText = $"CREATE INDEX IF NOT EXISTS idx_asset_recon_domain ON \"{TableName}\" (domain)";
            await domainIdx.ExecuteNonQueryAsync();

            var pageIdx = connection.CreateCommand();
            pageIdx.CommandText = $"CREATE INDEX IF NOT EXISTS idx_asset_recon_task_sort ON \"{TableName}\" (query_task_id, sort_order)";
            await pageIdx.ExecuteNonQueryAsync();
        }
        catch
        {
        }
    }

    private static async Task MigrateAsync(SqliteConnection connection)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var colCmd = connection.CreateCommand();
        colCmd.CommandText = $"PRAGMA table_info(\"{TableName}\")";
        using var reader = await colCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            existingColumns.Add(reader.GetString(1));
        }

        var addedUrl = false;
        foreach (var col in NewColumns)
        {
            if (!existingColumns.Contains(col))
            {
                var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE \"{TableName}\" ADD COLUMN \"{col}\" TEXT NOT NULL DEFAULT ''";
                await alterCmd.ExecuteNonQueryAsync();
                if (string.Equals(col, "url", StringComparison.OrdinalIgnoreCase))
                    addedUrl = true;
            }
        }

        if (addedUrl)
        {
            var delCmd = connection.CreateCommand();
            delCmd.CommandText = $"DELETE FROM \"{TableName}\"";
            await delCmd.ExecuteNonQueryAsync();
        }

        if (existingColumns.Contains("raw_data"))
        {
            try
            {
                var dropCmd = connection.CreateCommand();
                dropCmd.CommandText = $"ALTER TABLE \"{TableName}\" DROP COLUMN \"raw_data\"";
                await dropCmd.ExecuteNonQueryAsync();
            }
            catch
            {
            }
        }
    }

    private static async Task InsertRowsAsync(string dbPath, string queryTaskId,
        string source, string query, IReadOnlyList<UnifiedAssetRow> rows)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        using var transaction = await connection.BeginTransactionAsync();

        var insertSql = $@"
            INSERT INTO ""{TableName}"" (
                query_task_id, source, query,
                url, ip, port, protocol, title, domain,
                country, country_code, province, city, org, isp, os,
                server, banner, status_code, product, product_category, version,
                cert_issuer, cert_issuer_org, cert_subject, cert_subject_org,
                cert_sn, cert_not_before, cert_not_after, cert_domain,
                icp, as_number,
                header, header_hash, banner_hash, banner_fid,
                jarm, tls_ja3s, tls_version,
                cname, vuln, base_protocol, updated_at,
                link, cert, longitude, latitude,
                ip_tag, is_risk_protocol, icp_exception, is_web, cert_sha256, asset_tag,
                company, service_name, scene, district, x_powered_by, quake_id, sort_order,
                created_at
            ) VALUES (
                @taskId, @source, @query,
                @url, @ip, @port, @protocol, @title, @domain,
                @country, @countryCode, @province, @city, @org, @isp, @os,
                @server, @banner, @statusCode, @product, @productCategory, @version,
                @certIssuer, @certIssuerOrg, @certSubject, @certSubjectOrg,
                @certSn, @certNotBefore, @certNotAfter, @certDomain,
                @icp, @asNumber,
                @header, @headerHash, @bannerHash, @bannerFid,
                @jarm, @tlsJa3s, @tlsVersion,
                @cname, @vuln, @baseProtocol, @updatedAt,
                @link, @cert, @longitude, @latitude,
                @ipTag, @isRiskProtocol, @icpException, @isWeb, @certSha256, @assetTag,
                @company, @serviceName, @scene, @district, @xPoweredBy, @quakeId, @sortOrder,
                @createdAt
            )";

        var createdAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = insertSql;
            command.Parameters.AddWithValue("@taskId", queryTaskId);
            command.Parameters.AddWithValue("@source", source);
            command.Parameters.AddWithValue("@query", query);
            command.Parameters.AddWithValue("@url", row.Url);
            command.Parameters.AddWithValue("@ip", row.Ip);
            command.Parameters.AddWithValue("@port", row.Port);
            command.Parameters.AddWithValue("@protocol", row.Protocol);
            command.Parameters.AddWithValue("@title", row.Title);
            command.Parameters.AddWithValue("@domain", row.Domain);
            command.Parameters.AddWithValue("@country", row.Country);
            command.Parameters.AddWithValue("@countryCode", row.CountryCode);
            command.Parameters.AddWithValue("@province", row.Province);
            command.Parameters.AddWithValue("@city", row.City);
            command.Parameters.AddWithValue("@org", row.Org);
            command.Parameters.AddWithValue("@isp", row.Isp);
            command.Parameters.AddWithValue("@os", row.Os);
            command.Parameters.AddWithValue("@server", row.Server);
            command.Parameters.AddWithValue("@banner", row.Banner);
            command.Parameters.AddWithValue("@statusCode", row.StatusCode);
            command.Parameters.AddWithValue("@product", row.Product);
            command.Parameters.AddWithValue("@productCategory", row.ProductCategory);
            command.Parameters.AddWithValue("@version", row.Version);
            command.Parameters.AddWithValue("@certIssuer", row.CertIssuer);
            command.Parameters.AddWithValue("@certIssuerOrg", row.CertIssuerOrg);
            command.Parameters.AddWithValue("@certSubject", row.CertSubject);
            command.Parameters.AddWithValue("@certSubjectOrg", row.CertSubjectOrg);
            command.Parameters.AddWithValue("@certSn", row.CertSn);
            command.Parameters.AddWithValue("@certNotBefore", row.CertNotBefore);
            command.Parameters.AddWithValue("@certNotAfter", row.CertNotAfter);
            command.Parameters.AddWithValue("@certDomain", row.CertDomain);
            command.Parameters.AddWithValue("@icp", row.Icp);
            command.Parameters.AddWithValue("@asNumber", row.AsNumber);
            command.Parameters.AddWithValue("@header", row.Header);
            command.Parameters.AddWithValue("@headerHash", row.HeaderHash);
            command.Parameters.AddWithValue("@bannerHash", row.BannerHash);
            command.Parameters.AddWithValue("@bannerFid", row.BannerFid);
            command.Parameters.AddWithValue("@jarm", row.Jarm);
            command.Parameters.AddWithValue("@tlsJa3s", row.TlsJa3s);
            command.Parameters.AddWithValue("@tlsVersion", row.TlsVersion);
            command.Parameters.AddWithValue("@cname", row.Cname);
            command.Parameters.AddWithValue("@vuln", row.Vuln);
            command.Parameters.AddWithValue("@baseProtocol", row.BaseProtocol);
            command.Parameters.AddWithValue("@updatedAt", row.UpdatedAt);
            command.Parameters.AddWithValue("@link", row.Link);
            command.Parameters.AddWithValue("@cert", row.Cert);
            command.Parameters.AddWithValue("@longitude", row.Longitude);
            command.Parameters.AddWithValue("@latitude", row.Latitude);
            command.Parameters.AddWithValue("@ipTag", row.IpTag);
            command.Parameters.AddWithValue("@isRiskProtocol", row.IsRiskProtocol);
            command.Parameters.AddWithValue("@icpException", row.IcpException);
            command.Parameters.AddWithValue("@isWeb", row.IsWeb);
            command.Parameters.AddWithValue("@certSha256", row.CertSha256);
            command.Parameters.AddWithValue("@assetTag", row.AssetTag);
            command.Parameters.AddWithValue("@company", row.Company);
            command.Parameters.AddWithValue("@serviceName", row.ServiceName);
            command.Parameters.AddWithValue("@scene", row.Scene);
            command.Parameters.AddWithValue("@district", row.District);
            command.Parameters.AddWithValue("@xPoweredBy", row.XPoweredBy);
            command.Parameters.AddWithValue("@quakeId", row.QuakeId);
            command.Parameters.AddWithValue("@sortOrder", row.SortOrder);
            command.Parameters.AddWithValue("@createdAt", createdAt);

            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
