namespace ZeroFall.AiPanel.Services;

/// <summary>项目 SQLite（<c>.zerofall.db</c>）的 AI 提示与 sql 工具说明片段。</summary>
internal static class ProjectDatabaseSchemaHints
{
    public const string SqlToolDescriptionSuffix = """
        path 可省略：自动使用当前已打开项目的项目库（.zerofall.db）。
        查项目内结构化数据请用本工具，不要用终端 sqlite3。
        探表：SELECT name FROM sqlite_master WHERE type='table' ORDER BY 1。
        固定表：http_traffic_entries、terminal_transcript_*、asset_recon_results、
        source_registry、_meta；另有 CSV 导入生成的动态表（见 source_registry.table_name）。
        """;

    public const string SystemPromptSection = """

        ## 项目数据库（sql 工具，优先于终端 sqlite3）
        - 打开项目后，业务数据落在工作区根目录 **项目库**（`.zerofall.db`）。
        - **`sql` 的 path 可省略**：省略即对当前项目库执行；不要在工作区外用 `sqlite3` 命令查库。
        - 不知表名时：`SELECT name FROM sqlite_master WHERE type='table' ORDER BY 1`。
        - 分析流量、终端、侦察、导入 CSV 时 **优先 sql**；`page_content` / `read_terminal` 是实时 UI 补充，不是事实源全量。

        ### http_traffic_entries（浏览器 CDP/代理 + AI fetch 出站的全量 HTTP(S)）
        - 主键 `entry_id`；时间 `captured_at_utc`、`time_text`。
        - 请求：`method`、`url`、`host`、`path`、`extension`、`request_headers`、`request_body`（及 `request_body_raw` BLOB）、`request_content_type`。
        - 响应：`status`、`status_code`、`response_headers`、`response_body`（及 `response_body_raw`）、`response_content_type`、`response_body_len`、`latency_ms`。
        - 上下文：`source`（Browser/Proxy/**AiFetch**）、`tab`、`browser_tab_id`（fetch 为 `ai-fetch`）、`page_session_id`、`top_level_url`、`resource_context`（WebView2 资源类型 Document/Script/Image…）、`session_document_host`。
        - MIME：`mime_category`、`mime_primary`、`mime_type`；其它 `fingerprint_eligible`、`has_query`、`color`、`remark`。
        - 例：`SELECT url, method, status_code, host, captured_at_utc FROM http_traffic_entries ORDER BY captured_at_utc DESC LIMIT 30`。
        - 按 AI fetch：`WHERE source='AiFetch' ORDER BY captured_at_utc DESC`；返回 JSON 含 `entryId` 可 `WHERE entry_id='…'`。

        ### terminal_transcript_sessions / terminal_transcript_lines（终端行级 transcript）
        - **sessions**：`session_id`（= 终端 Tab Id）、`title`、`last_command_start_line`、`last_command_id`、`next_line_no`、`updated_at_utc`。
        - **lines**：`session_id`、`line_no`、`kind`（Output/Prompt/CommandInput）、`phase`、`text`、`command_id`、`created_at_utc`。
        - AI `read_terminal` 的增量输出以 transcript 为准，比聊天里工具 JSON 的 `output` 更可靠。
        - 例：`SELECT line_no, kind, text FROM terminal_transcript_lines WHERE session_id='…' ORDER BY line_no DESC LIMIT 50`。
        - 列会话：`SELECT session_id, title, last_command_id FROM terminal_transcript_sessions`。

        ### asset_recon_results（FOFA/Hunter/Quake/Shodan 等侦察结果）
        - 任务键 **`query_task_id`**（来自 `asset_recon` 或历史查询）；`source`、`query`、`sort_order`、`created_at`。
        - 常用列：`ip`、`port`、`protocol`、`domain`、`url`、`title`、`country`、`city`、`org`、`product`、`version`、`server`、`banner`、`status_code`、`header`、`cert_*`、`as_number`、`link` 等（共 40+ 统一字段）。
        - 例：`SELECT ip, port, title, source FROM asset_recon_results WHERE query_task_id='…' ORDER BY sort_order LIMIT 50`。
        - 去重统计：`SELECT source, COUNT(*) FROM asset_recon_results GROUP BY source`。

        ### source_registry + 动态表（工作区 CSV 导入）
        - **source_registry**：`source_uuid`、`file_path`、`file_name`、`table_name`（实际数据表名）、`source_type`、`row_count`、`indexed_at`、`is_dirty`、`can_write_back`。
        - 用户把 CSV 索引进项目后，会按文件名生成一张 **动态表**（列名来自 CSV 表头）；先查 registry 再查 `table_name`。
        - 例：`SELECT file_name, table_name, row_count FROM source_registry`；`SELECT * FROM "<table_name>" LIMIT 10`。

        ### _meta（项目库元数据）
        - 键值：`key`、`value`；内部用途，一般无需查。

        """;
}
