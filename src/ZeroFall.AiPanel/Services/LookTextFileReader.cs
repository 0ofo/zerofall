using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ZeroFall.AiPanel.Services;

internal enum LookTextSliceKind
{
    Full,
    Head,
    Tail,
    LineRange
}

internal sealed class LookTextSliceRequest
{
    public int Head { get; init; }
    public int Tail { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }

    public bool HasSlice =>
        Head > 0 || Tail > 0 || StartLine > 0 || EndLine > 0;

    public string? Validate()
    {
        var modes = 0;
        if (Head > 0) modes++;
        if (Tail > 0) modes++;
        if (StartLine > 0 || EndLine > 0) modes++;
        if (modes > 1)
            return "head、tail、start_line/end_line 只能指定一种读法。";

        if (Head < 0)
            return "head 须为正整数。";
        if (Tail < 0)
            return "tail 须为正整数。";
        if (StartLine < 0)
            return "start_line 须为正整数（1-based）。";
        if (EndLine < 0)
            return "end_line 须为正整数（1-based）。";
        if (StartLine > 0 && EndLine > 0 && EndLine < StartLine)
            return "end_line 不能小于 start_line。";

        return null;
    }
}

internal static class LookTextFileReader
{
    public const int MaxContentBytes = 8192;
    public const int MaxSliceLines = 400;
    public const int DefaultLineRangeSpan = 200;

    public static async Task<LookTextReadResult> ReadAsync(string filePath, LookTextSliceRequest slice)
    {
        var validation = slice.Validate();
        if (validation is not null)
            return LookTextReadResult.Error(validation);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var fileSize = stream.Length;
        LookTextSliceKind kind;
        List<LineEntry> selected;
        int totalLines;

        if (slice.Head > 0)
        {
            kind = LookTextSliceKind.Head;
            (selected, totalLines) = await ReadHeadAsync(reader, slice.Head);
        }
        else if (slice.Tail > 0)
        {
            kind = LookTextSliceKind.Tail;
            (selected, totalLines) = await ReadTailAsync(reader, slice.Tail);
        }
        else if (slice.StartLine > 0 || slice.EndLine > 0)
        {
            kind = LookTextSliceKind.LineRange;
            var start = slice.StartLine > 0 ? slice.StartLine : 1;
            var end = slice.EndLine > 0
                ? slice.EndLine
                : Math.Min(start + DefaultLineRangeSpan - 1, start + MaxSliceLines - 1);
            var span = Math.Min(end - start + 1, MaxSliceLines);
            end = start + span - 1;
            (selected, totalLines) = await ReadLineRangeAsync(reader, start, end);
        }
        else if (fileSize <= MaxContentBytes)
        {
            kind = LookTextSliceKind.Full;
            (selected, totalLines) = await ReadAllLinesAsync(reader);
        }
        else
        {
            totalLines = await CountLinesAsync(reader);
            return LookTextReadResult.TooLarge(fileSize, totalLines, slice.HasSlice);
        }

        if (selected.Count == 0)
        {
            return LookTextReadResult.Empty(fileSize, totalLines, kind, slice);
        }

        if (!FitsContentBudget(selected))
        {
            return LookTextReadResult.SliceTooLarge(fileSize, totalLines, kind, slice, selected.Count);
        }

        return LookTextReadResult.Ok(fileSize, totalLines, kind, slice, selected);
    }

    private static async Task<(List<LineEntry> Lines, int TotalLines)> ReadAllLinesAsync(StreamReader reader)
    {
        var lines = new List<LineEntry>();
        var lineNo = 0;
        while (true)
        {
            var text = await reader.ReadLineAsync();
            if (text is null)
                break;
            lineNo++;
            lines.Add(new LineEntry(lineNo, text));
        }

        return (lines, lineNo);
    }

    private static async Task<(List<LineEntry> Lines, int TotalLines)> ReadHeadAsync(StreamReader reader, int head)
    {
        var take = Math.Clamp(head, 1, MaxSliceLines);
        var lines = new List<LineEntry>(take);
        var lineNo = 0;
        while (lineNo < take)
        {
            var text = await reader.ReadLineAsync();
            if (text is null)
                break;
            lineNo++;
            lines.Add(new LineEntry(lineNo, text));
        }

        var total = lineNo;
        while (await reader.ReadLineAsync() is not null)
            total++;

        return (lines, total);
    }

    private static async Task<(List<LineEntry> Lines, int TotalLines)> ReadTailAsync(StreamReader reader, int tail)
    {
        var take = Math.Clamp(tail, 1, MaxSliceLines);
        var ring = new Queue<LineEntry>(take + 1);
        var lineNo = 0;
        while (true)
        {
            var text = await reader.ReadLineAsync();
            if (text is null)
                break;
            lineNo++;
            ring.Enqueue(new LineEntry(lineNo, text));
            while (ring.Count > take)
                ring.Dequeue();
        }

        return (new List<LineEntry>(ring), lineNo);
    }

    private static async Task<(List<LineEntry> Lines, int TotalLines)> ReadLineRangeAsync(
        StreamReader reader,
        int startLine,
        int endLine)
    {
        var lines = new List<LineEntry>();
        var lineNo = 0;
        while (lineNo < endLine)
        {
            var text = await reader.ReadLineAsync();
            if (text is null)
                break;
            lineNo++;
            if (lineNo < startLine)
                continue;
            lines.Add(new LineEntry(lineNo, text));
        }

        var total = lineNo;
        while (await reader.ReadLineAsync() is not null)
            total++;

        return (lines, total);
    }

    private static async Task<int> CountLinesAsync(StreamReader reader)
    {
        var count = 0;
        while (await reader.ReadLineAsync() is not null)
            count++;
        return count;
    }

    private static bool FitsContentBudget(IReadOnlyList<LineEntry> lines)
    {
        var total = 0;
        foreach (var line in lines)
        {
            total += Encoding.UTF8.GetByteCount(line.Text) + 16;
            if (total > MaxContentBytes)
                return false;
        }

        return true;
    }

    internal readonly record struct LineEntry(int Line, string Text);
}

internal sealed class LookTextReadResult
{
    public bool IsError { get; private init; }
    public string? ErrorMessage { get; private init; }
    public bool IsTooLarge { get; private init; }
    public bool IsSliceTooLarge { get; private init; }
    public long FileSizeBytes { get; private init; }
    public int TotalLines { get; private init; }
    public LookTextSliceKind Kind { get; private init; }
    public LookTextSliceRequest Slice { get; private init; } = new();
    public IReadOnlyList<LookTextFileReader.LineEntry> Lines { get; private init; } = Array.Empty<LookTextFileReader.LineEntry>();
    public int ReturnedLineCount { get; private init; }

    public static LookTextReadResult Error(string message) =>
        new() { IsError = true, ErrorMessage = message };

    public static LookTextReadResult TooLarge(long fileSize, int totalLines, bool hadSlice) =>
        new()
        {
            IsTooLarge = true,
            FileSizeBytes = fileSize,
            TotalLines = totalLines
        };

    public static LookTextReadResult SliceTooLarge(
        long fileSize,
        int totalLines,
        LookTextSliceKind kind,
        LookTextSliceRequest slice,
        int returnedLineCount) =>
        new()
        {
            IsSliceTooLarge = true,
            FileSizeBytes = fileSize,
            TotalLines = totalLines,
            Kind = kind,
            Slice = slice,
            ReturnedLineCount = returnedLineCount
        };

    public static LookTextReadResult Empty(
        long fileSize,
        int totalLines,
        LookTextSliceKind kind,
        LookTextSliceRequest slice) =>
        new()
        {
            FileSizeBytes = fileSize,
            TotalLines = totalLines,
            Kind = kind,
            Slice = slice,
            Lines = Array.Empty<LookTextFileReader.LineEntry>()
        };

    public static LookTextReadResult Ok(
        long fileSize,
        int totalLines,
        LookTextSliceKind kind,
        LookTextSliceRequest slice,
        IReadOnlyList<LookTextFileReader.LineEntry> lines) =>
        new()
        {
            FileSizeBytes = fileSize,
            TotalLines = totalLines,
            Kind = kind,
            Slice = slice,
            Lines = lines
        };

    public void WriteMetadata(JsonObject o, string relPath, string? mimeType, string language)
    {
        o["path"] = relPath;
        o["size"] = FormatSize(FileSizeBytes);
        o["totalLines"] = TotalLines;
        if (!string.IsNullOrEmpty(mimeType))
            o["mimeType"] = mimeType;
        if (!string.IsNullOrEmpty(language))
            o["language"] = language;
    }

    public void WriteContent(JsonObject o)
    {
        if (Lines.Count == 0)
        {
            o["content"] = string.Empty;
            return;
        }

        var sb = new StringBuilder();
        foreach (var line in Lines)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line.Text);
        }

        o["content"] = sb.ToString();
        if (Kind != LookTextSliceKind.Full || Lines.Count < TotalLines)
        {
            o["startLine"] = Lines[0].Line;
            o["endLine"] = Lines[^1].Line;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }
}
