namespace ZeroFall.Base.AiTools;

public class ToolCallResult
{
    public string ResultText { get; }
    public string? ToolName { get; }
    public string? Command { get; }
    public int ExitCode { get; }

    public ToolCallResult(string resultText, string? toolName = null, string? command = null, int exitCode = 0)
    {
        ResultText = resultText;
        ToolName = toolName;
        Command = command;
        ExitCode = exitCode;
    }
}
