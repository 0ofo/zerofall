using System.Collections.Generic;

namespace ZeroFall.Platform.Services;

public interface ITerminalTranscriptService
{
    void RegisterSession(string sessionId, string? title = null);

    void UnregisterSession(string sessionId);

    /// <summary>标记即将发送的命令起始行号（下一行输出）。</summary>
    int MarkCommandStart(string sessionId, string commandText);

    void AppendOutput(string sessionId, string chunk);

    /// <summary>用 XTerm 当前最后一行校正 transcript（处理 \\r 覆写导致的行内容偏差）。</summary>
    void SyncLastScreenLine(string sessionId, string lineText);

    /// <summary>用 XTerm 可见区尾部行覆盖 transcript 尾部（补全 PTY 未回显的命令行与提示符）。</summary>
    void ReplaceTailFromScreen(string sessionId, IReadOnlyList<string> screenTailLines, int tailLineCount = 28);

    void SetPhase(string sessionId, TerminalCommandPhase phase);

    /// <summary>将 PTY 未换行的尾部刷入 transcript 行表（AI 读屏前调用）。</summary>
    void FlushPendingOutput(string sessionId);

    TerminalCommandPhase? GetPhase(string sessionId);

    bool IsSessionRegistered(string sessionId);

    string? ReadFromLastCommand(string sessionId);

    /// <summary>自上次 AI 工具读完屏幕后的新增 transcript（配合 <see cref="CommitAiToolReadCursor"/>）。</summary>
    string? ReadSinceLastAiToolRead(string sessionId);

    /// <summary>将 AI 读屏游标推进到当前 transcript 末尾。</summary>
    void CommitAiToolReadCursor(string sessionId);

    /// <summary>读取 transcript 末尾 <paramref name="lineCount"/> 行。</summary>
    string? ReadLastLines(string sessionId, int lineCount);

    /// <summary>自该会话最后一次 PTY/屏幕输出更新以来经过的秒数；无会话时返回 null。</summary>
    double? GetSecondsSinceLastOutput(string sessionId);
}
