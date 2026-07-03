using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using ZeroFall.AiPanel.Models;
using ZeroFall.Base.AiTools;
using ZeroFall.Base.Data;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;
using ZeroFall.Platform.Services.RelationalDb;
using Wildcard;

namespace ZeroFall.AiPanel.Services;

public class LookService : IDisposable
{
    private const int MaxWriteContentLength = 1_048_576;
    private const int MaxSqliteSchemaRows = 50;
    private const int MaxGrepLines = 200;
    private const int MaxGrepFiles = 8_000;
    private const int MaxGrepLineTextChars = 500;
    private const int GrepSnippetRadius = 220;
    private const int MaxPathSearchResults = 500;
    private const int MaxReplaceFiles = 500;
    private const int MaxReplacePreviewLines = 80;

    private static readonly HashSet<string> SqliteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".sqlite3"
    };

    private readonly IRelationalDbBrowserRegistry _relationalDbRegistry;
    private readonly ISettingsService _settingsService;
    private readonly IEventBus _eventBus;
    private readonly IFileTypeInspector _fileTypeInspector;
    private readonly IAiToolResultRuntimeStore _toolResultRuntimeStore;
    private string _workspaceDirectory;
    private string _projectDatabasePath = string.Empty;
    private readonly Action<ProjectOpenedEvent> _projectOpenedHandler;

    public string WorkspaceDirectory => _workspaceDirectory;

    public LookService(
        IRelationalDbBrowserRegistry relationalDbRegistry,
        ISettingsService settingsService,
        IEventBus eventBus,
        IFileTypeInspector fileTypeInspector,
        IAiToolResultRuntimeStore toolResultRuntimeStore)
    {
        _relationalDbRegistry = relationalDbRegistry;
        _settingsService = settingsService;
        _eventBus = eventBus;
        _fileTypeInspector = fileTypeInspector;
        _toolResultRuntimeStore = toolResultRuntimeStore;

        var lastProjectPath = settingsService.Load().General.LastProjectPath;
        _workspaceDirectory = !string.IsNullOrEmpty(lastProjectPath) && Directory.Exists(lastProjectPath)
            ? lastProjectPath
            : ResolveFallbackWorkspaceDirectory();
        _projectDatabasePath = WorkspaceDatabase.GetPath(_workspaceDirectory);

        _projectOpenedHandler = OnProjectOpened;
        _eventBus.Subscribe(_projectOpenedHandler);
    }

    private void OnProjectOpened(ProjectOpenedEvent e)
    {
        _workspaceDirectory = e.DirectoryPath;
        _projectDatabasePath = !string.IsNullOrWhiteSpace(e.DatabasePath)
            ? e.DatabasePath
            : WorkspaceDatabase.GetPath(e.DirectoryPath);
    }

    private static string ResolveFallbackWorkspaceDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(userProfile)
            ? userProfile
            : AppContext.BaseDirectory;
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe(_projectOpenedHandler);
    }

    [AiTool("look",
        """
        统一查看/搜索**已打开项目的工作区**（侧边栏文件树根目录）。未打开项目时工作区无效。
        无参数：列出工作区根目录。path=目录：列目录；path=已存在的文件：预览（超 8KB 仅 hint，用 head/tail/行范围切片）。
        path=@tool_result:<message_id>：读取消息表中完整工具结果；可传 grep=正则搜索，或用 head/tail/start_line/end_line 切片。
        搜**文件名/目录名**：find=关键词或路径通配符（如 *.cs、**/*Test*、src/**/*.md）；path 省略=整棵工作区，path=目录=限定搜索根（也支持 src/** 等通配）。glob 可再缩小文件范围。
        搜**文件/工具结果正文**：grep=正则；glob 指定文件（如 report.md 或 **/*.md）；path 省略或=目录，禁止 path="."；在单个已存在文件内搜可 path=该文件。
        禁止：把待搜文件名只写在 path 里（文件不存在会报「搜索根无效」）；find 与 grep 同时传；用 path="." 代替省略。
        SQLite（.db/.sqlite）与 MySQL（*.mysql）返回表结构；查数据用 sql 工具。
        """)]
    public async Task<string> LookAsync(
        [ToolParam("相对工作区的目录/文件；或 @tool_result:<message_id> 读取完整工具结果；省略=工作区根", Required = false)] string? path = null,
        [ToolParam("按文件名/目录名或相对路径查找，支持 * ? **（如 *.cs、src/**/*.md）；不与 grep 同时使用", Required = false)] string? find = null,
        [ToolParam("按正文正则搜索文件或 @tool_result；不与 find 同时使用", Required = false)] string? grep = null,
        [ToolParam("grep/find 时限定文件，如 report.md、**/*.cs；省略=**/*", Required = false)] string? glob = null,
        [ToolParam("仅 grep 有效：匹配行前后上下文行数", Required = false)] int context_lines = 0,
        [ToolParam("搜索时忽略大小写", Required = false)] bool ignore_case = false,
        [ToolParam("读取文件开头 N 行（类似 head -n）；与 tail/行范围互斥", Required = false)] int head = 0,
        [ToolParam("读取文件末尾 N 行（类似 tail -n）；与 head/行范围互斥", Required = false)] int tail = 0,
        [ToolParam("起始行号（1-based，含）；与 head/tail 互斥", Required = false)] int start_line = 0,
        [ToolParam("结束行号（1-based，含）；省略时从 start_line 起最多 200 行", Required = false)] int end_line = 0)
    {
        if (!string.IsNullOrWhiteSpace(find) && !string.IsNullOrWhiteSpace(grep))
            return ToolResultJson.Error("find 和 grep 只能传一个。");

        if (TryParseToolResultPath(path, out var toolMessageId))
        {
            var output = await LoadToolResultOutputAsync(toolMessageId).ConfigureAwait(false);
            if (output == null)
                return ToolResultJson.Error($"未找到工具结果: {path}");

            var toolPath = BuildToolResultPath(toolMessageId);
            if (!string.IsNullOrWhiteSpace(find))
                return ToolResultJson.Error("@tool_result 不支持 find；请使用 grep=正则 搜索正文。");

            if (!string.IsNullOrWhiteSpace(grep))
            {
                return GrepText(toolPath, output, grep, context_lines, ignore_case);
            }

            return LookText(toolPath, output, new LookTextSliceRequest
            {
                Head = head,
                Tail = tail,
                StartLine = start_line,
                EndLine = end_line
            });
        }

        if (!string.IsNullOrWhiteSpace(grep))
        {
            return await GrepAsync(grep, path, glob, context_lines, ignore_case);
        }

        if (!string.IsNullOrWhiteSpace(find))
        {
            return SearchPath(find, path, glob, ignore_case);
        }

        var resolvedPath = ResolvePath(path);
        var slice = new LookTextSliceRequest
        {
            Head = head,
            Tail = tail,
            StartLine = start_line,
            EndLine = end_line
        };

        if (Directory.Exists(resolvedPath))
        {
            return await LookDirectoryAsync(resolvedPath);
        }

        if (File.Exists(resolvedPath))
        {
            return await LookFileAsync(resolvedPath, slice);
        }

        return ToolResultJson.Error($"路径不存在: {ToWorkspaceRelativePath(resolvedPath)}");
    }

    private static bool TryParseToolResultPath(string? path, out long messageId)
    {
        messageId = -1;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        const string prefix = "@tool_result:";
        var raw = path.Trim();
        if (!raw.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var rest = raw[prefix.Length..];
        if (string.IsNullOrWhiteSpace(rest))
            return false;

        if (long.TryParse(rest, out messageId) && messageId > 0)
            return true;

        var split = rest.LastIndexOf(':');
        if (split > 0
            && split < rest.Length - 1
            && long.TryParse(rest[(split + 1)..], out messageId)
            && messageId > 0)
            return true;

        return false;
    }

    private static string BuildToolResultPath(long messageId) =>
        ChatContextCompressionService.BuildToolResultPath(messageId);

    private async Task<string?> LoadToolResultOutputAsync(long messageId)
    {
        if (_toolResultRuntimeStore.TryGet(messageId, out var runtimeOutput))
            return NormalizeToolResultText(runtimeOutput);

        var dbPath = ResolveDatabasePath(null);
        if (dbPath == null)
            return null;

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT content
            FROM "{AiChatSessionStore.MessagesTable}"
            WHERE id = $id AND type = $type
            """;
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$type", ChatMessageDto.TypeTool);
        var raw = await cmd.ExecuteScalarAsync();
        if (raw is not string json || string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.TryGetProperty("output", out var output)
                ? ToolResultJson.FromPersistedOutput(output)
                : string.Empty;
            return NormalizeToolResultText(text);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeToolResultText(string text) =>
        PrettyPrintJsonIfPossible(text);

    private static string PrettyPrintJsonIfPossible(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var trimmed = text.Trim();
        if (trimmed.Length < 2
            || !((trimmed[0] == '{' && trimmed[^1] == '}')
                 || (trimmed[0] == '[' && trimmed[^1] == ']')))
            return text;

        try
        {
            var node = JsonNode.Parse(trimmed);
            return node?.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }) ?? text;
        }
        catch
        {
            return text;
        }
    }

    private static string LookText(string path, string text, LookTextSliceRequest slice)
    {
        var validation = slice.Validate();
        if (validation is not null)
            return ToolResultJson.Error(validation);

        var bytes = Encoding.UTF8.GetByteCount(text);
        var lines = SplitLines(text);
        if (!slice.HasSlice && bytes > LookTextFileReader.MaxContentBytes)
        {
            return ToolResultJson.Data(o =>
            {
                o["path"] = path;
                o["size"] = FormatSize(bytes);
                o["totalLines"] = lines.Count;
                o["hint"] = $"工具结果约 {lines.Count:N0} 行、{FormatSize(bytes)}，超过单次 {LookTextFileReader.MaxContentBytes / 1024}KB 限制。"
                            + "请指定 head、tail、start_line/end_line，或配合 grep=正则 筛选。";
            });
        }

        var (selected, kind) = SelectLines(lines, slice);
        var selectedBytes = Encoding.UTF8.GetByteCount(string.Join('\n', selected.Select(l => l.Text)));
        if (selectedBytes > LookTextFileReader.MaxContentBytes)
        {
            return ToolResultJson.Data(o =>
            {
                o["path"] = path;
                o["size"] = FormatSize(bytes);
                o["totalLines"] = lines.Count;
                o["hint"] = $"所选片段约 {selected.Count:N0} 行，编码后仍超过 {LookTextFileReader.MaxContentBytes / 1024}KB。请缩小读取范围。";
            });
        }

        return ToolResultJson.Data(o =>
        {
            o["path"] = path;
            o["size"] = FormatSize(bytes);
            o["totalLines"] = lines.Count;
            o["content"] = string.Join('\n', selected.Select(l => l.Text));
            if (kind != LookTextSliceKind.Full || selected.Count < lines.Count)
            {
                o["startLine"] = selected.Count == 0 ? 0 : selected[0].Line;
                o["endLine"] = selected.Count == 0 ? 0 : selected[^1].Line;
            }
        });
    }

    private static string GrepText(string path, string text, string pattern, int contextLines, bool ignoreCase)
    {
        var linePattern = pattern.Trim();
        var lines = SplitLines(text);
        var ctx = Math.Clamp(contextLines, 0, 20);
        var matches = new JsonArray();
        var matchedLineIndexes = new HashSet<int>();
        var matchCount = 0;
        var truncated = false;
        Regex regexMatcher;
        try
        {
            regexMatcher = CreateRegex(linePattern, ignoreCase);
        }
        catch (Exception ex)
        {
            return ToolResultJson.Error($"正则无效: {ex.Message}");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var match = regexMatcher.Match(lines[i].Text);
            if (!match.Success)
                continue;

            matchCount++;
            var start = Math.Max(0, i - ctx);
            var end = Math.Min(lines.Count - 1, i + ctx);
            for (var j = start; j <= end; j++)
            {
                if (!matchedLineIndexes.Add(j))
                    continue;
                if (matches.Count >= MaxGrepLines)
                {
                    truncated = true;
                    break;
                }

                matches.Add(BuildGrepMatchObject(
                    path,
                    lines[j].Line,
                    lines[j].Text,
                    isMatch: j == i,
                    j == i ? match : null));
            }

            if (truncated)
                break;
        }

        return ToolResultJson.Data(o =>
        {
            o["pattern"] = linePattern;
            o["regex"] = true;
            o["filesScanned"] = 1;
            o["matchCount"] = matchCount;
            o["truncated"] = truncated;
            o["matches"] = matches;
        });
    }

    private static List<(int Line, string Text)> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = normalized.Split('\n');
        var lines = new List<(int Line, string Text)>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
            lines.Add((i + 1, parts[i]));
        return lines;
    }

    private static (List<(int Line, string Text)> Lines, LookTextSliceKind Kind) SelectLines(
        List<(int Line, string Text)> lines,
        LookTextSliceRequest slice)
    {
        if (slice.Head > 0)
            return (lines.Take(slice.Head).ToList(), LookTextSliceKind.Head);
        if (slice.Tail > 0)
            return (lines.Skip(Math.Max(0, lines.Count - slice.Tail)).ToList(), LookTextSliceKind.Tail);
        if (slice.StartLine > 0 || slice.EndLine > 0)
        {
            var start = Math.Max(1, slice.StartLine > 0 ? slice.StartLine : 1);
            var end = slice.EndLine > 0
                ? slice.EndLine
                : Math.Min(lines.Count, start + LookTextFileReader.DefaultLineRangeSpan - 1);
            return (lines.Where(l => l.Line >= start && l.Line <= end).ToList(), LookTextSliceKind.LineRange);
        }

        return (lines, LookTextSliceKind.Full);
    }

    [AiTool("sql",
        """
        【优先】分析项目内已持久化的流量、终端历史、侦察结果、CSV 导入数据时用本工具；path 省略即当前项目库（.zerofall.db）。
        不要用终端 sqlite3。起手：SELECT name FROM sqlite_master WHERE type='table' ORDER BY 1。
        SELECT/SHOW/EXPLAIN/DESCRIBE：1 行 {列:值}；单列多行 {列:[值…]}；多列多行 {cols, rows} 矩阵；0 行单列 {列:[]}。DML/DDL 返回影响行数。
        MySQL 连接文件传 *.mysql；未指定 database 时用 schema.table。
        """ + ProjectDatabaseSchemaHints.SqlToolDescriptionSuffix)]
    public async Task<string> SqlAsync(
        [ToolParam("SQLite 相对路径（如 .zerofall.db）或 *.mysql 连接文件；省略=当前项目库", Required = false)] string? path = null,
        [ToolParam("SQL 语句，如 SELECT * FROM http_traffic_entries LIMIT 10、SHOW TABLES")] string sql = "")
    {
        if (string.IsNullOrWhiteSpace(sql))
            return ToolResultJson.Error("SQL 语句不能为空");

        var resolvedPath = ResolveDatabasePath(path);
        if (resolvedPath == null)
        {
            var expected = GetDefaultDatabasePathHint();
            return ToolResultJson.Error(
                string.IsNullOrWhiteSpace(path)
                    ? $"未找到项目数据库（期望 {expected}）。请先在应用中打开项目文件夹。"
                    : $"数据库不存在或不支持: {path}。项目库通常为 {expected}；MySQL 请传 *.mysql 连接文件。");
        }

        var browser = _relationalDbRegistry.Resolve(resolvedPath);
        if (browser == null)
            return ToolResultJson.Error($"不支持的数据源: {ToWorkspaceRelativePath(resolvedPath)}");

        if (RelationalSqlHelper.IsReadOnlyQuery(sql))
            return await ExecuteSelectAsync(browser, resolvedPath, sql);

        return await ExecuteNonQueryAsync(browser, resolvedPath, sql);
    }

    [AiTool("write", "向工作区内写入文本文件（UTF-8）。可创建新文件或覆盖/追加已有文本文件。勿用于 .zerofall.db、*.sqlite、*.mysql；改库请用 sql 工具。")]
    public async Task<string> WriteAsync(
        [ToolParam("相对工作区路径，如 src/Foo.cs 或 notes.md")] string path,
        [ToolParam("要写入的完整文本内容")] string content,
        [ToolParam("为 true 时在文件末尾追加，否则覆盖", Required = false)] bool append = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolResultJson.Error("path 不能为空");

        if (content.Length > MaxWriteContentLength)
            return ToolResultJson.Error($"内容过长（{content.Length} 字符），上限 {MaxWriteContentLength}。");

        var resolvedPath = ResolvePath(path);
        if (!IsPathWithinWorkspace(resolvedPath))
            return ToolResultJson.Error($"拒绝写入工作区外路径: {path}");

        if (IsProtectedFileTarget(resolvedPath))
            return ToolResultJson.Error("该路径禁止直接 write（请用 sql 改数据库，或选择其它文本文件）。");

        if (Directory.Exists(resolvedPath))
            return ToolResultJson.Error("目标是目录，请指定文件路径。");

        try
        {
            var dir = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var existed = File.Exists(resolvedPath);
            if (append && existed)
                await File.AppendAllTextAsync(resolvedPath, content, Utf8NoBom);
            else
                await File.WriteAllTextAsync(resolvedPath, content, Utf8NoBom);

            var rel = ToWorkspaceRelativePath(resolvedPath);
            var bytes = Encoding.UTF8.GetByteCount(content);
            var action = append && existed ? "已追加" : "已写入";
            PublishWorkspaceFileChanged(resolvedPath);

            return ToolResultJson.Data(o =>
            {
                o["action"] = action;
                o["path"] = rel;
                o["bytes"] = bytes;
                o["size"] = $"{FormatSize(bytes)} UTF-8";
            });
        }
        catch (Exception ex)
        {
            return ToolResultJson.Error($"写入失败: {ex.Message}");
        }
    }

    [AiTool("move", "移动或重命名工作区内文件/目录。destination 为已存在目录时移入该目录；否则作为新路径（可跨目录改名）。禁止操作 .zerofall.db、*.sqlite、*.mysql。")]
    public Task<string> MoveAsync(
        [ToolParam("源路径，如 src/Foo.cs 或 docs")] string source,
        [ToolParam("目标目录或新路径，如 backup/ 或 src/Bar.cs")] string destination)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Task.FromResult(ToolResultJson.Error("source 不能为空"));
        if (string.IsNullOrWhiteSpace(destination))
            return Task.FromResult(ToolResultJson.Error("destination 不能为空"));

        var sourcePath = ResolvePath(source);
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            return Task.FromResult(ToolResultJson.Error($"源路径不存在: {source}"));

        if (!IsPathWithinWorkspace(sourcePath))
            return Task.FromResult(ToolResultJson.Error($"拒绝操作工作区外路径: {source}"));

        if (IsProtectedFileTarget(sourcePath))
            return Task.FromResult(ToolResultJson.Error("源路径受保护，禁止 move。"));

        var destPath = ResolvePath(destination);
        if (Directory.Exists(destPath))
            destPath = Path.Combine(destPath, Path.GetFileName(sourcePath));
        else
        {
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);
        }

        if (!IsPathWithinWorkspace(destPath))
            return Task.FromResult(ToolResultJson.Error($"拒绝移动到工作区外: {destination}"));

        if (IsProtectedFileTarget(destPath))
            return Task.FromResult(ToolResultJson.Error("目标路径受保护，禁止 move。"));

        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResultJson.Ok("源与目标相同，无需移动。"));

        if (Directory.Exists(sourcePath)
            && destPath.StartsWith(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ToolResultJson.Error("不能将目录移动到其自身子目录内。"));
        }

        try
        {
            if (Directory.Exists(sourcePath))
            {
                if (Directory.Exists(destPath))
                    return Task.FromResult(ToolResultJson.Error($"目标已存在: {ToWorkspaceRelativePath(destPath)}"));
                Directory.Move(sourcePath, destPath);
            }
            else
            {
                try
                {
                    File.Move(sourcePath, destPath, overwrite: true);
                }
                catch (IOException)
                {
                    File.Copy(sourcePath, destPath, overwrite: true);
                    TryDeleteFile(sourcePath);
                }
            }

            PublishWorkspaceFileChanged(sourcePath, deleted: true);
            PublishWorkspaceFileChanged(destPath);

            return Task.FromResult(ToolResultJson.Data(o =>
            {
                o["from"] = ToWorkspaceRelativePath(sourcePath);
                o["to"] = ToWorkspaceRelativePath(destPath);
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResultJson.Error($"移动失败: {ex.Message}"));
        }
    }

    public Task<string> GrepAsync(
        [ToolParam("正则搜索模式，如 HttpClient、ERROR|WARN、https?://")] string pattern,
        [ToolParam("搜索根路径（文件或目录），省略为工作区根", Required = false)] string? path = null,
        [ToolParam("文件 glob，默认 **/*；例 **/*.cs、src/**/*.axaml", Required = false)] string? glob = null,
        [ToolParam("匹配行前后上下文行数（类似 grep -C）", Required = false)] int context_lines = 0,
        [ToolParam("忽略大小写", Required = false)] bool ignore_case = false)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(ToolResultJson.Error("pattern 不能为空"));

        var files = ResolveSearchFileTargets(path, glob);
        if (files.Count == 0)
            return Task.FromResult(ToolResultJson.Error($"未找到可搜索的文件（path={path ?? "."}, glob={glob ?? "**/*"}）"));

        return Task.FromResult(GrepRegexFiles(pattern.Trim(), files, context_lines, ignore_case));
    }

    private string GrepRegexFiles(string pattern, IReadOnlyList<string> files, int contextLines, bool ignoreCase)
    {
        Regex regex;
        try
        {
            regex = CreateRegex(pattern, ignoreCase);
        }
        catch (Exception ex)
        {
            return ToolResultJson.Error($"正则无效: {ex.Message}");
        }

        var ctx = Math.Clamp(contextLines, 0, 20);
        var matches = new JsonArray();
        var matchCount = 0;
        var truncated = false;

        foreach (var file in files)
        {
            List<string> lines;
            try
            {
                lines = File.ReadLines(file).ToList();
            }
            catch
            {
                continue;
            }

            var emitted = new HashSet<int>();
            for (var i = 0; i < lines.Count; i++)
            {
                var match = regex.Match(lines[i]);
                if (!match.Success)
                    continue;

                matchCount++;
                var start = Math.Max(0, i - ctx);
                var end = Math.Min(lines.Count - 1, i + ctx);
                for (var j = start; j <= end; j++)
                {
                    if (!emitted.Add(j))
                        continue;
                    if (matches.Count >= MaxGrepLines)
                    {
                        truncated = true;
                        break;
                    }

                    matches.Add(BuildGrepMatchObject(
                        ToWorkspaceRelativePath(file),
                        j + 1,
                        lines[j],
                        isMatch: j == i,
                        j == i ? match : null));
                }

                if (truncated)
                    break;
            }

            if (truncated)
                break;
        }

        return ToolResultJson.Data(o =>
        {
            o["pattern"] = pattern;
            o["regex"] = true;
            o["filesScanned"] = files.Count;
            o["matchCount"] = matchCount;
            o["truncated"] = truncated;
            o["matches"] = matches;
        });
    }

    private static JsonObject BuildGrepMatchObject(
        string path,
        int line,
        string text,
        bool isMatch,
        Match? match)
    {
        var snippet = isMatch && match is { Success: true }
            ? BuildCenteredSnippet(text, match.Index, Math.Max(1, match.Length), out var startColumn, out var truncated)
            : TruncateLineForGrep(text, out startColumn, out truncated);

        var obj = new JsonObject
        {
            ["path"] = path,
            ["line"] = line,
            ["text"] = snippet,
            ["isMatch"] = isMatch
        };

        if (isMatch && match is { Success: true })
        {
            obj["column"] = match.Index + 1;
            obj["matchLength"] = match.Length;
        }

        if (truncated)
        {
            obj["truncated"] = true;
            obj["lineLength"] = text.Length;
            obj["snippetStartColumn"] = startColumn;
        }

        return obj;
    }

    private static string TruncateLineForGrep(string text, out int startColumn, out bool truncated)
    {
        startColumn = 1;
        truncated = text.Length > MaxGrepLineTextChars;
        if (!truncated)
            return text;

        return text[..MaxGrepLineTextChars] + "…";
    }

    private static string BuildCenteredSnippet(
        string text,
        int matchIndex,
        int matchLength,
        out int startColumn,
        out bool truncated)
    {
        if (text.Length <= MaxGrepLineTextChars)
        {
            startColumn = 1;
            truncated = false;
            return text;
        }

        var start = Math.Max(0, matchIndex - GrepSnippetRadius);
        var end = Math.Min(text.Length, matchIndex + matchLength + GrepSnippetRadius);
        if (end - start > MaxGrepLineTextChars)
            end = Math.Min(text.Length, start + MaxGrepLineTextChars);

        startColumn = start + 1;
        truncated = true;
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = end < text.Length ? "…" : string.Empty;
        return prefix + text[start..end] + suffix;
    }

    private string SearchPath(string pattern, string? path, string? glob, bool ignoreCase)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResultJson.Error("pattern 不能为空");

        var trimmedFind = pattern.Trim();
        var searchRoots = ResolveFindSearchRoots(path);
        if (searchRoots.Count == 0)
            return ToolResultJson.Error($"搜索根不存在或不在工作区内: {path ?? "."}");

        var isPathStyle = IsPathStyleFindPattern(trimmedFind);
        var matcherPattern = isPathStyle
            ? trimmedFind.Replace('\\', '/')
            : NormalizeContentPattern(trimmedFind);

        var results = new JsonArray();
        var truncated = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddEntry(string entryPath)
        {
            if (truncated || !seen.Add(entryPath))
                return;

            if (!IsPathWithinWorkspaceOrRoot(entryPath))
                return;

            var rel = ToWorkspaceRelativePath(entryPath);
            var name = Path.GetFileName(entryPath);
            if (!MatchesFindPattern(rel, name, matcherPattern, isPathStyle, ignoreCase))
                return;

            if (results.Count >= MaxPathSearchResults)
            {
                truncated = true;
                return;
            }

            results.Add(new JsonObject
            {
                ["type"] = Directory.Exists(entryPath) ? "dir" : "file",
                ["path"] = rel,
                ["name"] = name
            });
        }

        var fileGlob = string.IsNullOrWhiteSpace(glob) ? "**/*" : glob.Trim();
        var options = new GlobOptions { RespectGitignore = true };

        foreach (var searchRoot in searchRoots)
        {
            if (truncated)
                break;

            if (isPathStyle)
            {
                foreach (var entry in Glob.Match(matcherPattern, searchRoot, options))
                {
                    if (truncated)
                        break;
                    if (!File.Exists(entry) || !IsPathWithinWorkspaceOrRoot(entry) || IsProtectedFileTarget(entry))
                        continue;
                    TryAddEntry(entry);
                }

                foreach (var dir in EnumerateDirectoriesSafe(searchRoot))
                {
                    if (truncated)
                        break;
                    TryAddEntry(dir);
                }

                continue;
            }

            foreach (var entry in Glob.Match(fileGlob, searchRoot, options))
            {
                if (truncated)
                    break;
                if (!File.Exists(entry) || !IsPathWithinWorkspaceOrRoot(entry) || IsProtectedFileTarget(entry))
                    continue;
                TryAddEntry(entry);
            }

            foreach (var dir in EnumerateDirectoriesSafe(searchRoot))
            {
                if (truncated)
                    break;
                TryAddEntry(dir);
            }
        }

        var primaryRoot = searchRoots.Count == 1
            ? searchRoots[0]
            : _workspaceDirectory;

        return ToolResultJson.Data(o =>
        {
            o["pattern"] = matcherPattern;
            o["root"] = ToWorkspaceRelativePath(primaryRoot);
            if (searchRoots.Count > 1)
            {
                var roots = new JsonArray();
                foreach (var root in searchRoots)
                    roots.Add(ToWorkspaceRelativePath(root));
                o["roots"] = roots;
            }
            if (!string.IsNullOrWhiteSpace(glob))
                o["glob"] = glob.Trim();
            o["matchCount"] = results.Count;
            o["truncated"] = truncated;
            o["matches"] = results;
        });
    }

    /// <summary>find 搜索根：字面目录/文件，或 path 通配符展开后的目录列表。</summary>
    private List<string> ResolveFindSearchRoots(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Directory.Exists(_workspaceDirectory) && IsPathWithinWorkspaceOrRoot(_workspaceDirectory)
                ? [_workspaceDirectory]
                : [];
        }

        var trimmed = path.Trim();
        if (!ContainsWildcard(trimmed))
        {
            var resolved = ResolvePath(trimmed);
            if (File.Exists(resolved))
            {
                var parent = Path.GetDirectoryName(resolved);
                return parent != null && IsPathWithinWorkspaceOrRoot(parent) ? [parent] : [];
            }

            return Directory.Exists(resolved) && IsPathWithinWorkspaceOrRoot(resolved) ? [resolved] : [];
        }

        var pattern = trimmed.Replace('\\', '/');
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new GlobOptions { RespectGitignore = true };
        foreach (var entry in Glob.Match(pattern, _workspaceDirectory, options))
        {
            string? root = null;
            if (Directory.Exists(entry))
                root = entry;
            else if (File.Exists(entry))
                root = Path.GetDirectoryName(entry);

            if (root != null && IsPathWithinWorkspaceOrRoot(root))
                roots.Add(root);
        }

        return roots.ToList();
    }

    private static bool IsPathStyleFindPattern(string pattern) =>
        pattern.Contains('/', StringComparison.Ordinal)
        || pattern.Contains('\\', StringComparison.Ordinal)
        || pattern.Contains("**", StringComparison.Ordinal);

    private static bool MatchesFindPattern(
        string relativePath,
        string fileName,
        string pattern,
        bool isPathStyle,
        bool ignoreCase)
    {
        var rel = relativePath.Replace('\\', '/');
        if (isPathStyle)
        {
            var pathPattern = pattern.Replace('\\', '/');
            if (ignoreCase)
                return Glob.IsMatch(pathPattern.ToLowerInvariant(), rel.ToLowerInvariant());

            return Glob.IsMatch(pathPattern, rel);
        }

        return MatchesWildcard(fileName, pattern, ignoreCase)
            || MatchesWildcard(rel, pattern, ignoreCase);
    }

    [AiTool("replace", "在工作区文件中查找并替换文本（类似 sed）。find 支持通配符；多行文本按字面量匹配。注意：默认 dry_run=true 仅预览、不写入；确认预览正确后必须显式 dry_run=false 才会落盘。禁止改 .zerofall.db、*.sqlite、*.mysql。")]
    public Task<string> ReplaceAsync(
        [ToolParam("要查找的文本或通配模式，如 oldMethod 或 *console.log(*)*")] string find,
        [ToolParam("替换为的文本；捕获组替换可用 $1、$2")] string replace,
        [ToolParam("搜索根路径（文件或目录），省略为工作区根", Required = false)] string? path = null,
        [ToolParam("文件 glob，默认 **/*", Required = false)] string? glob = null,
        [ToolParam("默认 true：仅预览不写入；必须显式传 false 才会真正修改文件", Required = false)] bool dry_run = true,
        [ToolParam("忽略大小写", Required = false)] bool ignore_case = false)
    {
        if (string.IsNullOrEmpty(find))
            return Task.FromResult(ToolResultJson.Error("find 不能为空"));

        var files = ResolveSearchFileTargets(path, glob);
        if (files.Count == 0)
            return Task.FromResult(ToolResultJson.Error($"未找到可替换的文件（path={path ?? "."}, glob={glob ?? "**/*"}）"));

        if (files.Count > MaxReplaceFiles)
            return Task.FromResult(ToolResultJson.Error($"匹配 {files.Count} 个文件，超过上限 {MaxReplaceFiles}，请缩小 glob 或 path。"));

        var fileArray = files.ToArray();
        IReadOnlyList<FileReplacer.FileResult> results;
        try
        {
            results = dry_run
                ? FileReplacer.Preview(fileArray, find, replace ?? string.Empty, ignore_case)
                : FileReplacer.Apply(fileArray, find, replace ?? string.Empty, ignore_case);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResultJson.Error($"替换失败: {ex.Message}"));
        }

        var previewLines = new JsonArray();
        var filesChanged = 0;
        var totalReplacements = 0;
        var previewTruncated = false;

        foreach (var file in results)
        {
            if (file.Replacements.Count == 0)
                continue;

            filesChanged++;
            totalReplacements += file.Replacements.Count;

            var filePreview = new JsonObject
            {
                ["path"] = ToWorkspaceRelativePath(file.FilePath),
                ["count"] = file.Replacements.Count
            };

            var lines = new JsonArray();
            foreach (var r in file.Replacements)
            {
                if (lines.Count >= MaxReplacePreviewLines)
                {
                    previewTruncated = true;
                    break;
                }

                lines.Add(new JsonObject
                {
                    ["line"] = r.LineNumber,
                    ["before"] = r.OriginalLine,
                    ["after"] = r.ReplacedLine
                });
            }

            filePreview["lines"] = lines;
            previewLines.Add(filePreview);
        }

        return Task.FromResult(ToolResultJson.Data(o =>
        {
            o["dryRun"] = dry_run;
            o["filesScanned"] = files.Count;
            o["filesChanged"] = filesChanged;
            o["replacementCount"] = totalReplacements;
            o["previewTruncated"] = previewTruncated;
            o["files"] = previewLines;
            if (!dry_run)
                o["applied"] = true;
        }));
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string NormalizeContentPattern(string pattern)
    {
        var p = pattern.Trim();
        if (!ContainsWildcard(p))
            return $"*{p}*";
        return p;
    }

    private List<string> ResolveSearchFileTargets(string? path, string? glob)
    {
        var searchRoot = ResolvePath(path);
        if (File.Exists(searchRoot))
        {
            if (!IsPathWithinWorkspaceOrRoot(searchRoot) || IsProtectedFileTarget(searchRoot))
                return [];
            return [searchRoot];
        }

        if (!Directory.Exists(searchRoot) || !IsPathWithinWorkspaceOrRoot(searchRoot))
            return [];

        var globPattern = string.IsNullOrWhiteSpace(glob) ? "**/*" : glob.Trim();
        var options = new GlobOptions { RespectGitignore = true };
        var files = new List<string>();

        foreach (var entry in Glob.Match(globPattern, searchRoot, options))
        {
            if (!File.Exists(entry))
                continue;
            if (!IsPathWithinWorkspaceOrRoot(entry))
                continue;
            if (IsProtectedFileTarget(entry))
                continue;

            files.Add(entry);
            if (files.Count >= MaxGrepFiles)
                break;
        }

        return files;
    }

    private static bool MatchesWildcard(string value, string pattern, bool ignoreCase) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase);

    private static Regex CreateRegex(string pattern, bool ignoreCase)
    {
        var options = RegexOptions.CultureInvariant;
        if (ignoreCase)
            options |= RegexOptions.IgnoreCase;
        return new Regex(pattern, options, TimeSpan.FromSeconds(2));
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (name is ".git" or ".svn" or ".hg")
                    continue;

                yield return dir;
                pending.Push(dir);
            }
        }
    }

    private bool IsPathWithinWorkspace(string fullPath)
    {
        var workspace = Path.GetFullPath(_workspaceDirectory);
        var target = Path.GetFullPath(fullPath);
        if (string.Equals(target, workspace, StringComparison.OrdinalIgnoreCase))
            return false;

        var prefix = workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsPathWithinWorkspaceOrRoot(string fullPath)
    {
        var workspace = Path.GetFullPath(_workspaceDirectory);
        var target = Path.GetFullPath(fullPath);
        return string.Equals(target, workspace, StringComparison.OrdinalIgnoreCase)
               || IsPathWithinWorkspace(target);
    }

    private static bool IsProtectedFileTarget(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (WorkspaceDatabase.IsProtectedDatabaseFile(fileName))
            return true;

        var ext = Path.GetExtension(filePath);
        if (SqliteExtensions.Contains(ext))
            return true;

        return DatabaseConnectionFiles.IsMySqlConnectionFile(filePath);
    }

    private static bool ContainsWildcard(string path) =>
        path.IndexOf('*') >= 0 || path.IndexOf('?') >= 0;

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private string? ResolveDatabasePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (!string.IsNullOrEmpty(_projectDatabasePath) && File.Exists(_projectDatabasePath))
                return _projectDatabasePath;

            var defaultDb = WorkspaceDatabase.GetPath(_workspaceDirectory);
            return File.Exists(defaultDb) ? defaultDb : null;
        }

        var resolved = ResolvePath(path);
        if (!File.Exists(resolved))
            return null;

        return _relationalDbRegistry.Resolve(resolved) != null ? resolved : null;
    }

    private string GetDefaultDatabasePathHint()
    {
        var path = !string.IsNullOrEmpty(_projectDatabasePath)
            ? _projectDatabasePath
            : WorkspaceDatabase.GetPath(_workspaceDirectory);
        return ToWorkspaceRelativePath(path);
    }

    private static async Task<string> ExecuteSelectAsync(IRelationalDbBrowser browser, string connectionPath, string sql)
    {
        try
        {
            var result = await browser.ExecuteQueryAsync(connectionPath, sql);

            if (result.Error != null)
                return ToolResultJson.Error($"查询错误: {result.Error}");

            return ToolResultJson.QueryRows(result.Columns, result.Rows);
        }
        catch (Exception ex)
        {
            return ToolResultJson.Error($"查询执行失败: {ex.Message}");
        }
    }

    private static async Task<string> ExecuteNonQueryAsync(IRelationalDbBrowser browser, string connectionPath, string sql)
    {
        try
        {
            var affected = await browser.ExecuteNonQueryAsync(connectionPath, sql);
            return ToolResultJson.Data(o => { o["affectedRows"] = affected; });
        }
        catch (Exception ex)
        {
            return ToolResultJson.Error($"执行失败: {ex.Message}");
        }
    }

    private string ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return _workspaceDirectory;

        if (Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(Path.Combine(_workspaceDirectory, path));
    }

    private string ToWorkspaceRelativePath(string absolutePath)
    {
        try
        {
            var rel = Path.GetRelativePath(_workspaceDirectory, absolutePath);
            return rel.Replace('\\', '/');
        }
        catch
        {
            return absolutePath;
        }
    }

    private Task<string> LookDirectoryAsync(string dirPath)
    {
        var dirInfo = new DirectoryInfo(dirPath);

        var entries = new List<(string Type, string Name, string Size, string Modified)>();

        try
        {
            foreach (var subDir in dirInfo.GetDirectories())
            {
                entries.Add(("dir", subDir.Name, "-", FormatDateTime(subDir.LastWriteTime)));
            }
        }
        catch (UnauthorizedAccessException) { }

        try
        {
            foreach (var file in dirInfo.GetFiles())
            {
                var type = FormatDirectoryFileType(file.FullName);
                entries.Add((type, file.Name, FormatSize(file.Length), FormatDateTime(file.LastWriteTime)));
            }
        }
        catch (UnauthorizedAccessException) { }

        var rows = new JsonArray();
        foreach (var e in entries)
        {
            rows.Add(new JsonObject
            {
                ["type"] = e.Type,
                ["name"] = e.Name,
                ["size"] = e.Size,
                ["modified"] = e.Modified
            });
        }

        return Task.FromResult(ToolResultJson.Data(rows));
    }

    private async Task<string> LookFileAsync(string filePath, LookTextSliceRequest slice)
    {
        var ext = Path.GetExtension(filePath);
        var fileName = Path.GetFileName(filePath);
        var fileSize = new FileInfo(filePath).Length;

        if (SqliteExtensions.Contains(ext) || DatabaseConnectionFiles.IsMySqlConnectionFile(filePath))
        {
            return await LookConnectionFileAsync(filePath, fileName, fileSize);
        }

        if (_fileTypeInspector.IsTextFile(filePath))
        {
            return await LookTextFileAsync(filePath, ext, slice);
        }

        return LookBinaryFileAsync(filePath, fileName, fileSize);
    }

    private async Task<string> LookConnectionFileAsync(string filePath, string fileName, long fileSize)
    {
        var browser = _relationalDbRegistry.Resolve(filePath);
        if (browser == null)
            return ToolResultJson.Error($"不支持的数据源: {fileName}");

        var kindLabel = browser.Kind switch
        {
            RelationalDbKind.MySql => "MySQL 连接",
            RelationalDbKind.Sqlite => "SQLite 数据库",
            _ => "数据库"
        };

        var relPath = ToWorkspaceRelativePath(filePath);
        string? connection = null;
        if (browser.Kind == RelationalDbKind.MySql)
        {
            try
            {
                var config = MySqlConnectionConfig.Load(filePath);
                connection = $"{config.Host}:{config.Port} / user={config.User}"
                             + (string.IsNullOrWhiteSpace(config.Database)
                                 ? "（未指定默认库，表名用 schema.table）"
                                 : $" / database={config.Database}");
            }
            catch
            {
                // 连接信息解析失败时仍展示表列表
            }
        }

        var tableRows = new JsonArray();
        var schemaRows = new JsonArray();

        try
        {
            var tables = await browser.GetTableNamesAsync(filePath);

            foreach (var table in tables)
            {
                try
                {
                    var tableData = await browser.GetTablePageAsync(filePath, table, 0, 1);
                    var rowCount = await browser.GetTableRowCountAsync(filePath, table);
                    tableRows.Add(new JsonObject
                    {
                        ["name"] = table,
                        ["columns"] = tableData.Columns.Count,
                        ["rows"] = rowCount
                    });
                }
                catch (Exception ex)
                {
                    tableRows.Add(new JsonObject
                    {
                        ["name"] = table,
                        ["columns"] = "?",
                        ["rows"] = $"错误: {ex.Message}"
                    });
                }
            }

            foreach (var table in tables.Take(MaxSqliteSchemaRows))
            {
                try
                {
                    var tableData = await browser.GetTablePageAsync(filePath, table, 0, 1);
                    schemaRows.Add(new JsonObject
                    {
                        ["table"] = table,
                        ["columns"] = string.Join(", ", tableData.Columns)
                    });
                }
                catch
                {
                    schemaRows.Add(new JsonObject
                    {
                        ["table"] = table,
                        ["columns"] = "(无法读取结构)"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            return ToolResultJson.Error(ex.Message, o =>
            {
                o["kind"] = kindLabel;
                o["fileName"] = fileName;
                o["size"] = FormatSize(fileSize);
                o["path"] = relPath;
                if (connection is not null)
                    o["connection"] = connection;
            });
        }

        return ToolResultJson.Data(o =>
        {
            o["kind"] = kindLabel;
            o["fileName"] = fileName;
            o["size"] = FormatSize(fileSize);
            o["path"] = relPath;
            if (connection is not null)
                o["connection"] = connection;
            o["tables"] = tableRows;
            o["schemas"] = schemaRows;
        });
    }

    private async Task<string> LookTextFileAsync(
        string filePath,
        string ext,
        LookTextSliceRequest slice)
    {
        var relPath = ToWorkspaceRelativePath(filePath);
        var probe = _fileTypeInspector.Probe(filePath);
        var language = GetCodeLanguage(ext);

        LookTextReadResult result;
        try
        {
            result = await LookTextFileReader.ReadAsync(filePath, slice);
        }
        catch (Exception ex)
        {
            return ToolResultJson.Error($"读取失败: {ex.Message}");
        }

        if (result.IsError)
            return ToolResultJson.Error(result.ErrorMessage ?? "读取失败");

        if (result.IsTooLarge)
        {
            return ToolResultJson.Data(o =>
            {
                result.WriteMetadata(o, relPath, probe.MimeType, language);
                o["hint"] = BuildLookTooLargeHint(result.FileSizeBytes, result.TotalLines);
            });
        }

        if (result.IsSliceTooLarge)
        {
            return ToolResultJson.Data(o =>
            {
                result.WriteMetadata(o, relPath, probe.MimeType, language);
                o["hint"] = BuildLookSliceTooLargeHint(result.ReturnedLineCount, result.TotalLines);
            });
        }

        return ToolResultJson.Data(o =>
        {
            result.WriteMetadata(o, relPath, probe.MimeType, language);
            result.WriteContent(o);
        });
    }

    private static string BuildLookTooLargeHint(long fileSizeBytes, int totalLines)
    {
        var size = FormatSize(fileSizeBytes);
        var linePart = totalLines > 0 ? $"约 {totalLines:N0} 行、" : string.Empty;
        return $"文件 {linePart}{size}，超过单次 {LookTextFileReader.MaxContentBytes / 1024}KB 限制。"
               + "请指定 head（开头 N 行）、tail（末尾 N 行）或 start_line+end_line 分段读取；"
               + "扫大量文件时用 look grep=正则 定位后再读取，勿对每个文件全文读取。";
    }

    private static string BuildLookSliceTooLargeHint(int requestedLines, int totalLines)
    {
        return $"所选片段约 {requestedLines} 行，编码后仍超过 {LookTextFileReader.MaxContentBytes / 1024}KB。"
               + "请减小 head/tail 或缩小 start_line~end_line 范围。"
               + (totalLines > 0 ? $" 文件共 {totalLines:N0} 行。" : string.Empty);
    }

    private string LookBinaryFileAsync(string filePath, string fileName, long fileSize)
    {
        var probe = _fileTypeInspector.Probe(filePath);

        return ToolResultJson.Data(o =>
        {
            o["fileName"] = fileName;
            o["path"] = ToWorkspaceRelativePath(filePath);
            o["size"] = FormatSize(fileSize);
            o["mimeType"] = probe.MimeType;
        });
    }

    private string FormatDirectoryFileType(string filePath)
    {
        if (DatabaseConnectionFiles.IsMySqlConnectionFile(filePath))
            return "🗄 MySQL";

        return _fileTypeInspector.Probe(filePath).MimeType ?? "application/octet-stream";
    }

    private static string GetCodeLanguage(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".json" => "json",
            ".xml" => "xml",
            ".yaml" or ".yml" => "yaml",
            ".toml" => "toml",
            ".sql" => "sql",
            ".csv" => "csv",
            ".md" => "markdown",
            ".js" or ".ts" => "javascript",
            ".py" => "python",
            ".cs" => "csharp",
            ".java" => "java",
            ".c" or ".h" => "c",
            ".cpp" or ".hpp" => "cpp",
            ".rs" => "rust",
            ".go" => "go",
            ".sh" => "bash",
            ".ps1" => "powershell",
            ".html" => "html",
            ".css" or ".scss" or ".less" => "css",
            _ => ""
        };
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:0.##} {units[unitIndex]}";
    }

    private static string FormatDateTime(DateTime dt)
    {
        return dt.ToString("yyyy-MM-dd HH:mm");
    }

    private void PublishWorkspaceFileChanged(string absolutePath, bool deleted = false)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !IsPathWithinWorkspace(absolutePath))
            return;

        var path = Path.GetFullPath(absolutePath);
        var evt = new WorkspaceFileChangedEvent(path, deleted);
        if (Dispatcher.UIThread.CheckAccess())
            _eventBus.Publish(evt);
        else
            Dispatcher.UIThread.Post(() => _eventBus.Publish(evt));
    }
}
