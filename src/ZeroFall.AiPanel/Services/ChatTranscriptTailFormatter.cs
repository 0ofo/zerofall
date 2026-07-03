using System.Collections.Generic;
using System.Text;
using ZeroFall.AiPanel.Models;

namespace ZeroFall.AiPanel.Services;

/// <summary>将会话消息格式化为 transcript 块，并只取尾部 N 块用于 UI。</summary>
internal static class ChatTranscriptTailFormatter
{
    private const string ToolRunningMarker = "执行中…";

    public static string Format(IReadOnlyList<ChatMessage> messages, int maxBlocks) =>
        FormatSnapshots(ToSnapshots(messages), maxBlocks);

    public static string FormatSnapshots(IReadOnlyList<ChatMessageUiSnapshot> messages, int maxBlocks)
    {
        if (messages.Count == 0 || maxBlocks <= 0)
            return string.Empty;

        var blocks = new List<string>();
        foreach (var message in messages)
            CollectBlocks(message, blocks);

        if (blocks.Count == 0)
            return string.Empty;

        var omitted = 0;
        if (blocks.Count > maxBlocks)
        {
            omitted = blocks.Count - maxBlocks;
            blocks.RemoveRange(0, omitted);
        }

        var sb = new StringBuilder();
        if (omitted > 0)
            sb.AppendLine($"...（已省略更早 {omitted} 块）").AppendLine();

        for (var i = 0; i < blocks.Count; i++)
            sb.Append(blocks[i]);

        return sb.ToString().TrimEnd();
    }

    private static ChatMessageUiSnapshot[] ToSnapshots(IReadOnlyList<ChatMessage> messages)
    {
        var rows = new ChatMessageUiSnapshot[messages.Count];
        for (var i = 0; i < messages.Count; i++)
            rows[i] = ChatMessageUiSnapshot.From(messages[i]);
        return rows;
    }

    private static void CollectBlocks(ChatMessageUiSnapshot message, List<string> blocks)
    {
        if (message.IsUser)
        {
            blocks.Add(FormatUserBlock(message.Content));
            return;
        }

        if (message.HasToolCall)
        {
            blocks.Add(FormatToolBlock(message));
            return;
        }

        if (message.IsThinking || message.HasReasoning)
            blocks.Add(FormatReasoningBlock(message));

        if (message.IsStreaming || !string.IsNullOrWhiteSpace(message.Content))
            blocks.Add(FormatAssistantBlock(message.Content, message.IsStreaming));
    }

    private static string FormatUserBlock(string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---");
        AppendBlockquoteBody(sb, "> [用户] ", content);
        sb.AppendLine("---");
        return sb.ToString();
    }

    private static string FormatToolBlock(ChatMessageUiSnapshot message)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---");

        var label = string.IsNullOrEmpty(message.ToolDisplayName)
            ? message.ToolName
            : message.ToolDisplayName;
        sb.Append("> [工具] ").AppendLine(NormalizeLine(label));

        if (!string.IsNullOrWhiteSpace(message.ToolArgumentsJson))
        {
            sb.AppendLine("> [参数]");
            AppendBlockquoteBody(sb, "> ", message.ToolArgumentsJson.Trim());
        }

        sb.AppendLine("> [返回]");
        AppendBlockquoteBody(sb, "> ", ResolveToolOutput(message));
        sb.AppendLine("---");
        return sb.ToString();
    }

    private static string FormatReasoningBlock(ChatMessageUiSnapshot message)
    {
        var body = message.IsThinking && string.IsNullOrEmpty(message.ReasoningContent)
            ? "..."
            : message.ReasoningContent.Trim();

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("> [思考]");
        if (!string.IsNullOrEmpty(body))
            AppendBlockquoteBody(sb, "> ", body);
        sb.AppendLine("---");
        return sb.ToString();
    }

    private static string FormatAssistantBlock(string content, bool streaming)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(content))
            sb.Append(content.TrimEnd());
        if (streaming)
            sb.Append('…');
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }

    private static string ResolveToolOutput(ChatMessageUiSnapshot message)
    {
        if (message.IsToolRunning)
            return ToolRunningMarker;

        if (string.IsNullOrWhiteSpace(message.ToolOutput))
            return message.ToolExitCode == 0 ? "（无输出）" : "（失败）";

        return message.ToolOutput;
    }

    private static void AppendBlockquoteBody(StringBuilder sb, string linePrefix, string body)
    {
        if (string.IsNullOrEmpty(body))
            return;

        var lines = body.Split('\n');
        sb.Append(linePrefix).AppendLine(NormalizeLine(lines[0]));
        for (var i = 1; i < lines.Length; i++)
            sb.Append("> ").AppendLine(NormalizeLine(lines[i]));
    }

    private static string NormalizeLine(string line) =>
        line.Replace("\r", string.Empty);
}
