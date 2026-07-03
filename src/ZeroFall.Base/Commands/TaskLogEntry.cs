using System;

namespace ZeroFall.Base.Commands;

public class TaskLogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public required string Message { get; init; }
    public TaskLogLevel Level { get; init; } = TaskLogLevel.Info;
}
