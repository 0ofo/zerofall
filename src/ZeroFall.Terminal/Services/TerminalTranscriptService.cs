using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Services;
using Microsoft.Data.Sqlite;

namespace ZeroFall.Terminal.Services;

public sealed class TerminalTranscriptService : ITerminalTranscriptService, IDisposable
{
    public const string LinesTable = "terminal_transcript_lines";
    public const string SessionsTable = "terminal_transcript_sessions";

    private readonly IEventBus _eventBus;
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PersistBatch> _persistBatches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MetaPersistState> _metaPersistStates = new(StringComparer.Ordinal);

    private const int PersistFlushDelayMs = 400;
    private const int SessionMetaPersistDelayMs = 900;

    private string _databasePath = string.Empty;
    private bool _schemaReady;

    public TerminalTranscriptService(IEventBus eventBus)
    {
        _eventBus = eventBus;
        _eventBus.Subscribe<ProjectOpenedEvent>(OnProjectOpened);
    }

    public void RegisterSession(string sessionId, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _sessions.AddOrUpdate(
            sessionId,
            _ => new SessionState(sessionId, title),
            (_, existing) =>
            {
                if (!string.IsNullOrWhiteSpace(title))
                    existing.Title = title;
                return existing;
            });

        ScheduleSessionMetaPersist(sessionId);
    }

    public void UnregisterSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _ = RequestPersistFlushAsync(sessionId, immediate: true);
        _sessions.TryRemove(sessionId, out _);
        _persistBatches.TryRemove(sessionId, out _);
        _metaPersistStates.TryRemove(sessionId, out _);
    }

    public int MarkCommandStart(string sessionId, string commandText)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return -1;

        lock (session.Sync)
        {
            FlushPartialLineLocked(session);
            session.LastCommandId++;
            session.LastCommandText = commandText;

            var lineNo = session.NextLineNo++;
            var record = new TranscriptLine(
                lineNo,
                TerminalLineKind.CommandInput,
                session.Phase,
                commandText,
                session.LastCommandId);
            session.LastCommandStartLine = lineNo;
            session.Lines.Add(record);
            TrimSessionLines(session);
            QueuePersistLine(sessionId, record);
            ScheduleSessionMetaPersist(sessionId);
            _ = RequestPersistFlushAsync(sessionId, immediate: true);
            return lineNo;
        }
    }

    public void AppendOutput(string sessionId, string chunk)
    {
        if (string.IsNullOrEmpty(chunk) || !_sessions.TryGetValue(sessionId, out var session))
            return;

        var stripped = TerminalAnsiText.Strip(chunk);
        if (string.IsNullOrEmpty(stripped))
            return;

        lock (session.Sync)
        {
            session.OutputAppendCount++;
            session.OutputChunks.Add(new OutputChunk(session.LastCommandId, stripped));
            TouchLastOutputLocked(session);
            if (session.OutputAppendCount % 64 == 0 || session.OutputChunks.Count > 8000)
                TrimOutputChunks(session);

            var feed = session.Recorder.Feed(stripped);
            if (feed.RewindLastLine)
                RemoveLastLineForCommandLocked(session, session.LastCommandId);

            foreach (var lineText in feed.CompletedLines)
                AppendCompleteLineLocked(session, lineText);
        }
    }

    public void SyncLastScreenLine(string sessionId, string lineText)
    {
        if (string.IsNullOrEmpty(lineText) || !_sessions.TryGetValue(sessionId, out var session))
            return;

        lock (session.Sync)
        {
            if (!string.IsNullOrEmpty(session.Recorder.PeekPartial()))
            {
                var partial = session.Recorder.PeekPartial()!;
                if (IsStablePromptLine(lineText) && !IsStablePromptLine(partial))
                    FlushPartialLineLocked(session);
                else
                {
                    session.Recorder.ReplacePartial(lineText);
                    if (IsStablePromptLine(lineText))
                        FlushPartialLineLocked(session);
                    return;
                }
            }

            for (var i = session.Lines.Count - 1; i >= 0; i--)
            {
                var line = session.Lines[i];
                if (line.CommandId != session.LastCommandId)
                    continue;

                if (string.Equals(line.Text, lineText, StringComparison.Ordinal))
                    return;

                // 屏幕已到新提示符时，勿用其覆盖上一条命令输出（如 whoami 的 hp\a）
                if (IsStablePromptLine(lineText) && !string.IsNullOrWhiteSpace(line.Text)
                    && !IsStablePromptLine(line.Text))
                {
                    AppendCompleteLineLocked(session, lineText);
                    return;
                }

                var updated = line with { Text = lineText };
                session.Lines[i] = updated;
                TouchLastOutputLocked(session);
                QueuePersistLine(sessionId, updated);
                return;
            }

            session.Recorder.ReplacePartial(lineText);
            if (IsStablePromptLine(lineText))
                FlushPartialLineLocked(session);
            else
                TouchLastOutputLocked(session);
        }
    }

    private static bool IsStablePromptLine(string? lineText) =>
        TerminalPromptDetector.LooksLikeShellPromptLine(lineText);

    public void ReplaceTailFromScreen(string sessionId, IReadOnlyList<string> screenTailLines, int tailLineCount = 28)
    {
        if (screenTailLines.Count == 0 || !_sessions.TryGetValue(sessionId, out var session))
            return;

        tailLineCount = Math.Clamp(tailLineCount, 4, 64);
        var screen = screenTailLines
            .TakeLast(tailLineCount)
            .Select(l => l ?? string.Empty)
            .ToList();

        while (screen.Count > 0 && string.IsNullOrWhiteSpace(screen[^1]))
            screen.RemoveAt(screen.Count - 1);

        if (screen.Count == 0)
            return;

        List<int> deletedLineNos;
        List<TranscriptLine> insertedLines;
        lock (session.Sync)
        {
            FlushPartialLineLocked(session);
            session.Recorder.ClearPartial();

            deletedLineNos = new List<int>(tailLineCount);
            var removeCount = Math.Min(tailLineCount, session.Lines.Count);
            for (var i = 0; i < removeCount; i++)
            {
                var removed = session.Lines[^1];
                session.Lines.RemoveAt(session.Lines.Count - 1);
                deletedLineNos.Add(removed.LineNo);
            }

            insertedLines = new List<TranscriptLine>(screen.Count);
            var commandIndex = TerminalScreenReader.FindLastCommandLineIndex(screen, session.LastCommandText);
            for (var i = 0; i < screen.Count; i++)
            {
                var commandId = commandIndex >= 0 && i >= commandIndex ? session.LastCommandId : 0;
                var record = CreateLineRecordLocked(session, screen[i], commandId);
                session.Lines.Add(record);
                insertedLines.Add(record);
            }

            if (commandIndex >= 0 && commandIndex < insertedLines.Count)
                session.LastCommandStartLine = insertedLines[commandIndex].LineNo;

            ReconcileAiReadCursorAfterTailReplace(session, deletedLineNos, insertedLines);
            TouchLastOutputLocked(session);
            TrimSessionLines(session);
        }

        _ = PersistTailReplaceAsync(sessionId, deletedLineNos, insertedLines);
        ScheduleSessionMetaPersist(sessionId);
        _ = RequestPersistFlushAsync(sessionId, immediate: true);
    }

    private void RemoveLastLineForCommandLocked(SessionState session, int commandId)
    {
        for (var i = session.Lines.Count - 1; i >= 0; i--)
        {
            if (session.Lines[i].CommandId != commandId)
                continue;

            var removed = session.Lines[i];
            session.Lines.RemoveAt(i);
            QueueDeleteLine(session.SessionId, removed.LineNo);
            return;
        }
    }

    public void SetPhase(string sessionId, TerminalCommandPhase phase)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        lock (session.Sync)
        {
            if (phase == TerminalCommandPhase.Idle && session.Phase == TerminalCommandPhase.Executing)
                FlushPartialLineLocked(session);

            session.Phase = phase;
        }

        if (phase == TerminalCommandPhase.Idle)
            _ = RequestPersistFlushAsync(sessionId, immediate: true);
    }

    public void FlushPendingOutput(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        lock (session.Sync)
            FlushPartialLineLocked(session);
    }

    public TerminalCommandPhase? GetPhase(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        lock (session.Sync)
            return session.Phase;
    }

    public bool IsSessionRegistered(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _sessions.ContainsKey(sessionId);

    public string? ReadFromLastCommand(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        lock (session.Sync)
        {
            var startLineNo = ResolveCommandReadStartLine(session);
            if (startLineNo <= 0)
                return ReadTailLocked(session);

            var fromLines = ReadFromLineNoLocked(session, startLineNo);
            if (!string.IsNullOrEmpty(fromLines))
                return fromLines;

            var commandId = session.LastCommandId;
            if (commandId <= 0)
                return string.Empty;

            var raw = new StringBuilder();
            foreach (var chunk in session.OutputChunks)
            {
                if (chunk.CommandId != commandId)
                    continue;

                raw.Append(chunk.Text);
            }

            var text = TerminalTextNormalizer.NormalizeForAi(raw.ToString());
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : text;
        }
    }

    public string? ReadSinceLastAiToolRead(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        lock (session.Sync)
        {
            if (session.LastAiToolReadAfterLineNo < 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var line in session.Lines)
            {
                if (line.LineNo < session.LastAiToolReadAfterLineNo)
                    continue;

                if (line.LineNo == session.LastAiToolReadAfterLineNo)
                {
                    var lineSnapshot = session.LastAiToolReadLineSnapshot;
                    if (!string.IsNullOrEmpty(lineSnapshot)
                        && line.Text.StartsWith(lineSnapshot, StringComparison.Ordinal)
                        && line.Text.Length > lineSnapshot.Length)
                        sb.AppendLine(line.Text[lineSnapshot.Length..]);
                    else if (!string.Equals(line.Text, lineSnapshot, StringComparison.Ordinal))
                        sb.AppendLine(line.Text);
                    continue;
                }

                sb.AppendLine(line.Text);
            }

            var partial = session.Recorder.PeekPartial();
            if (!string.IsNullOrEmpty(partial))
            {
                var snapshot = session.LastAiToolReadPartialSnapshot;
                if (string.IsNullOrEmpty(snapshot))
                    sb.Append(partial);
                else if (partial.StartsWith(snapshot, StringComparison.Ordinal)
                         && partial.Length > snapshot.Length)
                    sb.Append(partial[snapshot.Length..]);
                else if (!string.Equals(partial, snapshot, StringComparison.Ordinal))
                    sb.Append(partial);
            }

            var text = TerminalTextNormalizer.NormalizeForAi(sb.ToString());
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : text;
        }
    }

    public string? ReadLastLines(string sessionId, int lineCount)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        lineCount = Math.Clamp(lineCount, 1, TerminalScreenReader.AiMaxLines);

        lock (session.Sync)
        {
            var sb = new StringBuilder();
            var startIndex = Math.Max(0, session.Lines.Count - lineCount);
            for (var i = startIndex; i < session.Lines.Count; i++)
                sb.AppendLine(session.Lines[i].Text);

            var partial = session.Recorder.PeekPartial();
            if (!string.IsNullOrEmpty(partial))
                sb.Append(partial);

            var text = TerminalTextNormalizer.NormalizeForAi(sb.ToString());
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : text;
        }
    }

    public void CommitAiToolReadCursor(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        lock (session.Sync)
        {
            if (session.Lines.Count > 0)
            {
                var last = session.Lines[^1];
                session.LastAiToolReadAfterLineNo = last.LineNo;
                session.LastAiToolReadLineSnapshot = last.Text;
            }

            session.LastAiToolReadPartialSnapshot = session.Recorder.PeekPartial();
        }
    }

    private static void ReconcileAiReadCursorAfterTailReplace(
        SessionState session,
        IReadOnlyList<int> deletedLineNos,
        IReadOnlyList<TranscriptLine> insertedLines)
    {
        if (session.LastAiToolReadAfterLineNo < 0 || deletedLineNos.Count == 0)
            return;

        var cursorDeleted = deletedLineNos.Contains(session.LastAiToolReadAfterLineNo);
        var deletedBeforeCursor = false;
        foreach (var n in deletedLineNos)
        {
            if (n < session.LastAiToolReadAfterLineNo)
            {
                deletedBeforeCursor = true;
                break;
            }
        }

        if (!cursorDeleted && !deletedBeforeCursor)
            return;

        if (insertedLines.Count == 0)
        {
            session.LastAiToolReadAfterLineNo = -1;
            session.LastAiToolReadLineSnapshot = null;
            session.LastAiToolReadPartialSnapshot = null;
            return;
        }

        var snapshot = session.LastAiToolReadLineSnapshot;
        TranscriptLine? anchor = null;
        if (!string.IsNullOrEmpty(snapshot))
        {
            for (var i = insertedLines.Count - 1; i >= 0; i--)
            {
                var line = insertedLines[i];
                if (string.Equals(line.Text, snapshot, StringComparison.Ordinal)
                    || line.Text.StartsWith(snapshot, StringComparison.Ordinal))
                {
                    anchor = line;
                    break;
                }
            }
        }

        anchor ??= insertedLines[^1];
        session.LastAiToolReadAfterLineNo = anchor.Value.LineNo;
        session.LastAiToolReadLineSnapshot = anchor.Value.Text;
    }

    public double? GetSecondsSinceLastOutput(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        lock (session.Sync)
            return ElapsedSecondsSince(session.LastOutputUtc);
    }

    private static void TouchLastOutputLocked(SessionState session) =>
        session.LastOutputUtc = DateTime.UtcNow;

    private static double ElapsedSecondsSince(DateTime utc) =>
        Math.Round((DateTime.UtcNow - utc).TotalSeconds, 1, MidpointRounding.AwayFromZero);

    private static int ResolveCommandReadStartLine(SessionState session)
    {
        if (session.LastCommandStartLine > 0 && session.LastCommandId > 0)
        {
            foreach (var line in session.Lines)
            {
                if (line.LineNo != session.LastCommandStartLine)
                    continue;

                if (line.Kind == TerminalLineKind.CommandInput && line.CommandId == session.LastCommandId)
                    return session.LastCommandStartLine;

                if (line.CommandId == session.LastCommandId)
                    return session.LastCommandStartLine;
                break;
            }
        }

        if (session.LastCommandId > 0)
        {
            foreach (var line in session.Lines)
            {
                if (line.CommandId == session.LastCommandId && line.Kind == TerminalLineKind.CommandInput)
                    return line.LineNo;
            }
        }

        if (!string.IsNullOrEmpty(session.LastCommandText))
        {
            for (var i = session.Lines.Count - 1; i >= 0; i--)
            {
                var line = session.Lines[i];
                if (line.CommandId != 0 && line.CommandId != session.LastCommandId)
                    continue;

                if (TerminalScreenReader.FindLastCommandLineIndex([line.Text], session.LastCommandText) >= 0)
                    return line.LineNo;
            }
        }

        if (session.LastCommandId > 0)
        {
            foreach (var line in session.Lines)
            {
                if (line.CommandId == session.LastCommandId)
                    return line.LineNo;
            }
        }

        return session.LastCommandStartLine > 0 ? session.LastCommandStartLine : -1;
    }

    private static string ReadFromLineNoLocked(SessionState session, int startLineNo)
    {
        var sb = new StringBuilder();
        TranscriptLine? startRecord = null;
        foreach (var line in session.Lines)
        {
            if (line.LineNo == startLineNo)
            {
                startRecord = line;
                break;
            }
        }

        var skipCommandEcho = startRecord?.Kind == TerminalLineKind.CommandInput
                              && !string.IsNullOrEmpty(session.LastCommandText);

        foreach (var line in session.Lines)
        {
            if (line.LineNo < startLineNo)
                continue;

            if (skipCommandEcho
                && line.LineNo > startLineNo
                && line.Kind != TerminalLineKind.CommandInput
                && TerminalScreenReader.FindLastCommandLineIndex([line.Text], session.LastCommandText) >= 0)
                continue;

            sb.AppendLine(line.Text);
        }

        var partial = session.Recorder.PeekPartial();
        if (!string.IsNullOrEmpty(partial))
            sb.Append(partial);

        return TerminalScreenReader.TrimTrailingEmptyLinesStatic(sb.ToString());
    }

    private static void TrimOutputChunks(SessionState session)
    {
        const int maxChunks = 8000;
        while (session.OutputChunks.Count > maxChunks)
            session.OutputChunks.RemoveAt(0);
    }

    private static string ReadTailLocked(SessionState session)
    {
        var sb = new StringBuilder();
        foreach (var line in session.Lines.TakeLast(200))
        {
            sb.AppendLine(line.Text);
        }

        var partial = session.Recorder.PeekPartial();
        if (!string.IsNullOrEmpty(partial))
            sb.Append(partial);

        var text = TerminalScreenReader.TrimTrailingEmptyLinesStatic(sb.ToString());
        return string.IsNullOrEmpty(text) ? string.Empty : text;
    }

    private void AppendCompleteLineLocked(SessionState session, string lineText)
    {
        TouchLastOutputLocked(session);
        var record = CreateLineRecordLocked(session, lineText);
        session.Lines.Add(record);
        TrimSessionLines(session);
        QueuePersistLine(session.SessionId, record);
    }

    private static TranscriptLine CreateLineRecordLocked(SessionState session, string lineText, int? commandId = null)
    {
        var lineNo = session.NextLineNo++;
        var cid = commandId ?? session.LastCommandId;
        var kind = ClassifyLine(session, lineText, lineNo);
        // CommandInput 仅由 MarkCommandStart 创建；PTY 回显/残余命令行保持 Output，避免重复计入 AI 读屏。

        return new TranscriptLine(lineNo, kind, session.Phase, lineText, cid);
    }

    private async Task PersistTailReplaceAsync(
        string sessionId,
        IReadOnlyList<int> deletedLineNos,
        IReadOnlyList<TranscriptLine> insertedLines)
    {
        await FlushPersistQueueAsync(sessionId).ConfigureAwait(false);
        if (!await TryEnsureSchemaAsync().ConfigureAwait(false))
            return;
        if (deletedLineNos.Count == 0 && insertedLines.Count == 0)
            return;

        await _dbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

            if (deletedLineNos.Count > 0)
            {
                await using var deleteCmd = connection.CreateCommand();
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = $"""
                    DELETE FROM "{LinesTable}"
                    WHERE session_id = $session_id AND line_no = $line_no;
                    """;
                var sid = deleteCmd.CreateParameter();
                sid.ParameterName = "$session_id";
                deleteCmd.Parameters.Add(sid);
                var lineNoParam = deleteCmd.CreateParameter();
                lineNoParam.ParameterName = "$line_no";
                deleteCmd.Parameters.Add(lineNoParam);

                foreach (var lineNo in deletedLineNos)
                {
                    sid.Value = sessionId;
                    lineNoParam.Value = lineNo;
                    await deleteCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            if (insertedLines.Count > 0)
                await UpsertLinesAsync(connection, tx, sessionId, insertedLines).ConfigureAwait(false);

            await tx.CommitAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private void QueuePersistLine(string sessionId, TranscriptLine line)
    {
        var batch = _persistBatches.GetOrAdd(sessionId, _ => new PersistBatch());
        lock (batch.Sync)
        {
            batch.Deletes.Remove(line.LineNo);
            batch.Upserts[line.LineNo] = line;
        }

        SchedulePersistFlush(sessionId);
    }

    private void QueueDeleteLine(string sessionId, int lineNo)
    {
        var batch = _persistBatches.GetOrAdd(sessionId, _ => new PersistBatch());
        lock (batch.Sync)
        {
            batch.Upserts.Remove(lineNo);
            batch.Deletes.Add(lineNo);
        }

        SchedulePersistFlush(sessionId);
    }

    private void SchedulePersistFlush(string sessionId) =>
        _ = RequestPersistFlushAsync(sessionId, immediate: false);

    private Task RequestPersistFlushAsync(string sessionId, bool immediate)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.CompletedTask;

        var batch = _persistBatches.GetOrAdd(sessionId, _ => new PersistBatch());
        if (immediate)
        {
            Interlocked.Exchange(ref batch.FlushScheduled, 0);
            return FlushPersistQueueAsync(sessionId);
        }

        if (Interlocked.CompareExchange(ref batch.FlushScheduled, 1, 0) != 0)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PersistFlushDelayMs).ConfigureAwait(false);
                await FlushPersistQueueAsync(sessionId).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref batch.FlushScheduled, 0);
                if (_persistBatches.TryGetValue(sessionId, out var pending))
                {
                    lock (pending.Sync)
                    {
                        if (pending.Upserts.Count > 0 || pending.Deletes.Count > 0)
                            SchedulePersistFlush(sessionId);
                    }
                }
            }
        });
    }

    private async Task FlushPersistQueueAsync(string sessionId)
    {
        if (!_persistBatches.TryGetValue(sessionId, out var batch))
            return;

        List<TranscriptLine> upserts;
        List<int> deletes;
        lock (batch.Sync)
        {
            if (batch.Upserts.Count == 0 && batch.Deletes.Count == 0)
                return;

            upserts = batch.Upserts.Values.ToList();
            deletes = batch.Deletes.ToList();
            batch.Upserts.Clear();
            batch.Deletes.Clear();
        }

        if (!await TryEnsureSchemaAsync().ConfigureAwait(false))
            return;

        await _dbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

            if (deletes.Count > 0)
            {
                await using var deleteCmd = connection.CreateCommand();
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = $"""
                    DELETE FROM "{LinesTable}"
                    WHERE session_id = $session_id AND line_no = $line_no;
                    """;
                var sid = deleteCmd.CreateParameter();
                sid.ParameterName = "$session_id";
                deleteCmd.Parameters.Add(sid);
                var lineNoParam = deleteCmd.CreateParameter();
                lineNoParam.ParameterName = "$line_no";
                deleteCmd.Parameters.Add(lineNoParam);

                foreach (var lineNo in deletes)
                {
                    sid.Value = sessionId;
                    lineNoParam.Value = lineNo;
                    await deleteCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            if (upserts.Count > 0)
                await UpsertLinesAsync(connection, tx, sessionId, upserts).ConfigureAwait(false);

            await tx.CommitAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private static async Task UpsertLinesAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sessionId,
        IReadOnlyList<TranscriptLine> lines)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO "{LinesTable}" (
                session_id, line_no, kind, phase, text, command_id, created_at_utc
            ) VALUES (
                $session_id, $line_no, $kind, $phase, $text, $command_id, $created_at_utc
            )
            ON CONFLICT(session_id, line_no) DO UPDATE SET
                kind = excluded.kind,
                phase = excluded.phase,
                text = excluded.text,
                command_id = excluded.command_id,
                created_at_utc = excluded.created_at_utc;
            """;

        var pSessionId = cmd.CreateParameter();
        pSessionId.ParameterName = "$session_id";
        cmd.Parameters.Add(pSessionId);
        var pLineNo = cmd.CreateParameter();
        pLineNo.ParameterName = "$line_no";
        cmd.Parameters.Add(pLineNo);
        var pKind = cmd.CreateParameter();
        pKind.ParameterName = "$kind";
        cmd.Parameters.Add(pKind);
        var pPhase = cmd.CreateParameter();
        pPhase.ParameterName = "$phase";
        cmd.Parameters.Add(pPhase);
        var pText = cmd.CreateParameter();
        pText.ParameterName = "$text";
        cmd.Parameters.Add(pText);
        var pCommandId = cmd.CreateParameter();
        pCommandId.ParameterName = "$command_id";
        cmd.Parameters.Add(pCommandId);
        var pCreatedAt = cmd.CreateParameter();
        pCreatedAt.ParameterName = "$created_at_utc";
        cmd.Parameters.Add(pCreatedAt);

        var createdAt = DateTime.UtcNow.ToString("O");
        foreach (var line in lines)
        {
            pSessionId.Value = sessionId;
            pLineNo.Value = line.LineNo;
            pKind.Value = line.Kind.ToString();
            pPhase.Value = line.Phase.ToString();
            pText.Value = line.Text;
            pCommandId.Value = line.CommandId;
            pCreatedAt.Value = createdAt;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private void ScheduleSessionMetaPersist(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var state = _metaPersistStates.GetOrAdd(sessionId, _ => new MetaPersistState());
        if (Interlocked.CompareExchange(ref state.Scheduled, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SessionMetaPersistDelayMs).ConfigureAwait(false);
                await PersistSessionMetaAsync(sessionId).ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref state.Scheduled, 0);
            }
        });
    }

    private void FlushPartialLineLocked(SessionState session)
    {
        var partial = session.Recorder.PeekPartial();
        if (string.IsNullOrEmpty(partial))
            return;

        session.Recorder.ClearPartial();
        AppendCompleteLineLocked(session, partial);
    }

    private static TerminalLineKind ClassifyLine(SessionState session, string lineText, int lineNo)
    {
        if (session.LastCommandStartLine > 0 && lineNo == session.LastCommandStartLine)
            return TerminalLineKind.CommandInput;

        if (TerminalPromptDetector.TryGetTextAfterKaliPromptMarker(lineText, out var afterKali)
            && afterKali.Length > 0)
        {
            return TerminalLineKind.Output;
        }

        if (TerminalPromptDetector.LooksLikeShellPromptLine(lineText))
            return TerminalLineKind.Prompt;

        return TerminalLineKind.Output;
    }

    private static void TrimSessionLines(SessionState session)
    {
        const int maxLines = 12000;
        if (session.Lines.Count <= maxLines)
            return;

        session.Lines.RemoveRange(0, session.Lines.Count - maxLines);
    }

    private void OnProjectOpened(ProjectOpenedEvent e)
    {
        _databasePath = e.DatabasePath ?? string.Empty;
        _schemaReady = false;
        _ = OnProjectOpenedAsync();
    }

    private async Task OnProjectOpenedAsync()
    {
        if (!await TryEnsureSchemaAsync().ConfigureAwait(false))
            return;

        foreach (var sessionId in _sessions.Keys)
            await PersistSessionMetaAsync(sessionId).ConfigureAwait(false);
    }

    private async Task<bool> TryEnsureSchemaAsync()
    {
        if (string.IsNullOrWhiteSpace(_databasePath))
            return false;

        if (_schemaReady)
            return true;

        await _dbGate.WaitAsync();
        try
        {
            if (_schemaReady)
                return true;

            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    CREATE TABLE IF NOT EXISTS "{SessionsTable}" (
                        session_id TEXT PRIMARY KEY,
                        title TEXT,
                        last_command_start_line INTEGER NOT NULL DEFAULT -1,
                        last_command_id INTEGER NOT NULL DEFAULT 0,
                        next_line_no INTEGER NOT NULL DEFAULT 1,
                        updated_at_utc TEXT NOT NULL
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    CREATE TABLE IF NOT EXISTS "{LinesTable}" (
                        session_id TEXT NOT NULL,
                        line_no INTEGER NOT NULL,
                        kind TEXT NOT NULL,
                        phase TEXT NOT NULL,
                        text TEXT NOT NULL,
                        command_id INTEGER NOT NULL DEFAULT 0,
                        created_at_utc TEXT NOT NULL,
                        PRIMARY KEY (session_id, line_no)
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            _schemaReady = true;
            return true;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private async Task PersistSessionMetaAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        if (!await TryEnsureSchemaAsync().ConfigureAwait(false))
            return;

        SessionSnapshot snapshot;
        lock (session.Sync)
            snapshot = session.Snapshot();

        await _dbGate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO "{SessionsTable}" (
                    session_id, title, last_command_start_line, last_command_id, next_line_no, updated_at_utc
                ) VALUES (
                    $session_id, $title, $last_command_start_line, $last_command_id, $next_line_no, $updated_at_utc
                )
                ON CONFLICT(session_id) DO UPDATE SET
                    title = excluded.title,
                    last_command_start_line = excluded.last_command_start_line,
                    last_command_id = excluded.last_command_id,
                    next_line_no = excluded.next_line_no,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$session_id", snapshot.SessionId);
            cmd.Parameters.AddWithValue("$title", snapshot.Title ?? string.Empty);
            cmd.Parameters.AddWithValue("$last_command_start_line", snapshot.LastCommandStartLine);
            cmd.Parameters.AddWithValue("$last_command_id", snapshot.LastCommandId);
            cmd.Parameters.AddWithValue("$next_line_no", snapshot.NextLineNo);
            cmd.Parameters.AddWithValue("$updated_at_utc", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public void Dispose() => _dbGate.Dispose();

    private sealed class PersistBatch
    {
        public object Sync { get; } = new();
        public Dictionary<int, TranscriptLine> Upserts { get; } = new();
        public HashSet<int> Deletes { get; } = new();
        public int FlushScheduled;
    }

    private sealed class MetaPersistState
    {
        public int Scheduled;
    }

    private sealed class SessionState
    {
        public SessionState(string sessionId, string? title)
        {
            SessionId = sessionId;
            Title = title;
        }

        public object Sync { get; } = new();
        public string SessionId { get; }
        public string? Title { get; set; }
        public TerminalCommandPhase Phase { get; set; } = TerminalCommandPhase.Unknown;
        public int NextLineNo { get; set; } = 1;
        public int LastCommandStartLine { get; set; } = -1;
        public int LastCommandId { get; set; }
        public string? LastCommandText { get; set; }
        public TerminalOutputLineRecorder Recorder { get; } = new();
        public List<TranscriptLine> Lines { get; } = [];
        public List<OutputChunk> OutputChunks { get; } = [];
        public int OutputAppendCount { get; set; }
        /// <summary>AI 工具已读到的最后一行 line_no；同行追加内容靠 <see cref="LastAiToolReadLineSnapshot"/> 增量。</summary>
        public int LastAiToolReadAfterLineNo { get; set; } = -1;
        public string? LastAiToolReadLineSnapshot { get; set; }
        /// <summary>上次 commit 时 recorder 中的未换行尾部，用于增量读 partial。</summary>
        public string? LastAiToolReadPartialSnapshot { get; set; }
        public DateTime LastOutputUtc { get; set; } = DateTime.UtcNow;

        public SessionSnapshot Snapshot() => new(
            SessionId,
            Title,
            LastCommandStartLine,
            LastCommandId,
            NextLineNo);
    }

    private readonly record struct TranscriptLine(
        int LineNo,
        TerminalLineKind Kind,
        TerminalCommandPhase Phase,
        string Text,
        int CommandId);

    private readonly record struct OutputChunk(int CommandId, string Text);

    private readonly record struct SessionSnapshot(
        string SessionId,
        string? Title,
        int LastCommandStartLine,
        int LastCommandId,
        int NextLineNo);
}
