namespace ZeroFall.Browser.Services;

/// <summary>流量归档查询列投影，避免列表/网站树拉取 body BLOB。</summary>
public enum TrafficArchiveProjection
{
    /// <summary>全部列（详情、重放）。</summary>
    Full,

    /// <summary>流量表列表：元数据 + 头，不含 body/raw。</summary>
    ListMeta,

    /// <summary>网站树重建：会话 + MIME + 头，不含 body/raw。</summary>
    SiteMapMeta
}

public static class TrafficArchiveColumnSets
{
    private const string MetaCore = """
        entry_id, captured_at_utc, time_text, source, tab, browser_tab_id, page_session_id,
        top_level_url, session_document_host, resource_context, method, url, host, path, extension,
        status, status_code, mime_category, mime_primary, mime_type,
        request_content_type, response_content_type,
        has_query, fingerprint_eligible, response_body_len,
        latency_ms, color, remark
        """;

    public const string ListMeta = MetaCore + ", request_headers, response_headers";
    public const string SiteMapMeta = MetaCore + ", request_headers, response_headers";

    public static string SelectList(TrafficArchiveProjection projection) =>
        projection switch
        {
            TrafficArchiveProjection.ListMeta => ListMeta,
            TrafficArchiveProjection.SiteMapMeta => SiteMapMeta,
            _ => "*"
        };
}
