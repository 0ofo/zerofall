namespace ZeroFall.AiPanel.Services;

/// <summary>帮助模型理解 ZeroFall 内置工具分工与选用顺序（写入 system prompt）。</summary>
internal static class AiToolPlaybook
{
    public const string SystemPromptSection = """

        ## 工具选用（发挥 ZeroFall 能力的核心）
        ZeroFall 会把**浏览器 HTTP 流量、终端输出、资产侦察**写入**项目库**（`.zerofall.db`）。
        **已发生的历史事实优先用 `sql` 查库**；`page_content` / `read_terminal` 只看当前 UI 快照，不是全量数据源。

        | 目标 | 首选 | 说明 |
        |------|------|------|
        | 复盘/筛选已捕获的 HTTP 请求与 body | **`sql` → `http_traffic_entries`** | 扁平 URL/状态码/body；历史全量以库为准 |
        | **当前浏览器会话**某站点打了哪些路径/静态资源/CDN | **`browser_website_tree`** | 先 `browser_tab action=open` 浏览目标站；比盲 `page_content` 或堆 `fetch` 更适合摸清站点结构 |
        | 查终端某次命令的完整历史输出 | **`sql` → `terminal_transcript_lines`** | 比聊天里工具 JSON 的 output 更准；`read_terminal` 返回末尾 `tail` 行（默认 50） |
        | 分析 FOFA/Hunter 等侦察结果 | **`sql` → `asset_recon_results`** | 按 `query_task_id`；新发查询/继续拉取用 `asset_recon` |
        | 分析用户导入的 CSV | **`sql` → `source_registry`** 再查 `table_name` | |
        | 当前已打开页面正文（含 JS 渲染后） | `browser_tab` + `page_content` | DOM 快照；SPA/反 CDP 站优先 **`sql`→`http_traffic_entries`** |
        | SPA / 反 CDP 站 API 与 JSON 响应 | **`sql` → `http_traffic_entries`** | 先 `browser_tab action=open` 触发浏览，再查库；比 `page_content` / `Runtime.evaluate` 可靠 |
        | 出站 HTTP 单条请求 / API | **`fetch`** | 写入 `http_traffic_entries`；默认返回完整 JSON；搜索/阅读正文用 `isAll=false`，HTML 可用 isMd；不执行页面 JS |
        | 多条有策略的 HTTP（批量、遍历、条件分支） | **工作区脚本 + 终端** | 不要堆几十次 `fetch`；单条无策略请求才用 `fetch` |
        | 交互登录、填表、点按钮 | `browser_cdp` / `browser_tab` | JS 执行统一用 `browser_cdp` 调 `Runtime.evaluate`，参数包含 `expression`、`returnByValue`、`awaitPromise` |
        | 跑 shell、ssh、编译、执行脚本 | **`send_terminal_command` + `read_terminal`** | 无 `execute_command`；慢命令加 `& echo _cmd_end_`；`read_terminal` 用 `tail` 取末尾行；Windows 乱码先 `chcp 65001` |
        | 看工作区有哪些文件/读文本/搜索内容 | **`look`** | `path` 读/列；`find` 找文件名；`grep` 搜正文正则 |
        | 批量文本替换 | **`replace`** | 默认 `dry_run=true` 只预览；确认后必须显式 `dry_run=false` 才写入 |
        | 不知道当前打开的是哪个 Tab/面板 | `ui_context` | 默认 scope=active 且含选中数据摘要；浏览器 Tab Id 形如 `browser-…` |
        | 独立子任务、多步调研 | `spawn_agent` | 子 Agent 同样能用 sql/浏览器/终端 |

        **`look` 参数（易错）**：
        - 工作区 = 侧边栏已打开项目的根目录；须先打开项目。
        - **三种模式**：`path` 读文件/列目录；`find="*.cs"` 找文件名/目录名；`grep="ERROR|WARN"` 搜正文正则。
        - **搜文件内容**：`grep=正则` + `glob=文件名或 **/*.md`；`path` 省略即可，**不要** `path="."`。
        - **正文 grep 是正则**：如 `grep="ERROR|WARN"`、`grep="https?://"`；普通词如 `grep="HttpClient"` 也可直接用。
        - **在单个文件内搜**：先确认文件存在（无参 `look` 或 `path=目录` 列出）；再 `path=该文件` + `grep=正则`，或 `glob=该文件` + `grep=正则`。
        - **禁止**把文件名只写在 `path` 里做搜索；找文件名用 `find`，搜正文用 `grep`；`find` 和 `grep` 不要同时传。
        - 大文件：先 `grep` 定位行号，再 `path=文件` + `start_line`/`head`/`tail` 切片读；单次正文 ≤8KB。

        **`replace` 参数（易错）**：
        - 默认 `dry_run=true`，返回预览但不会写文件；确认匹配正确后再次调用并显式传 `dry_run=false`。
        - 替换后如需确认，优先用 `look path=文件 head/tail/行范围` 或 `look grep=正则 glob=...` 验证。

        **Windows 终端编码**：
        - 中文乱码时先用 `send_terminal_command` 执行 `chcp 65001`，再重跑命令。
        - PowerShell 输出仍乱码时可先执行 `[Console]::OutputEncoding=[Text.UTF8Encoding]::UTF8; $OutputEncoding=[Text.UTF8Encoding]::UTF8`。

        **`sql` 起手式**（`path` 省略 = 项目库）：
        1. `SELECT name FROM sqlite_master WHERE type='table' ORDER BY 1`
        2. 按上表查 `http_traffic_entries` / `terminal_transcript_*` / `asset_recon_results`
        3. 始终 `LIMIT`，大字段（body）先选元数据列再按需查 body

        **常见误区（避免）**：
        - 用终端 `sqlite3` 查项目库 → 应用内必须用 **`sql` 工具**
        - 复盘 HTTP 时只靠 `page_content` / `fetch` 重放，不查 **`http_traffic_entries`**
        - SPA/反 CDP 站只靠 `page_content` 或 `Runtime.evaluate` 读正文，不查 **`http_traffic_entries`** 里的 XHR JSON
        - 摸清站点资源面只靠 `page_content` 或 `sql`，不用 **`browser_website_tree`**（当前会话路径/CDN 拓扑）
        - 终端 output 混乱时只靠 `read_terminal`，不查 **`terminal_transcript_lines`**（`read_terminal` 仅末尾 `tail` 行快照）
        - 侦察结果只在 UI 表格里看几行，不用 **`sql` 拉全量**
        - 用 Python/curl 脚本发**单条** HTTP → 应统一用 **`fetch`**
        - 多条有策略请求只靠反复 `fetch` → 应写工作区脚本后在终端执行
        - 大文件或未指定切片时对多个路径全文 **`look`** → 先用 **`look grep=正则 glob=...`** 定位，再按 head/tail/行范围读取；超过 8KB 时 `look` 只返回 hint
        - 用 **`path`** 写一个并不存在的文件名来搜索 → 应改用 `find=文件名` 或 `grep=正文正则`
        - 调用 **`replace`** 后看到预览就以为已修改 → 默认 `dry_run=true`，必须 `dry_run=false` 才会落盘
        - Windows 终端中文输出乱码时直接相信乱码结果 → 先 `chcp 65001` 或设置 PowerShell UTF-8 输出后重试
        - 读工作区代码用 **`invoke_ui_menu` / `workspace.openFile`** 打开编辑器 → AI 应 **`look`**，不会在 Content 区开 Tab

        """;
}
