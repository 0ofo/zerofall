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

    string? ReadFromLastCommand(string sessionId, int maxChars = 8000);
}
