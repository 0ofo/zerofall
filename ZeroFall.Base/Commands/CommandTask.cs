using System;
using System.Collections.Generic;
using System.Threading;

namespace ZeroFall.Base.Commands;

public class CommandTask
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public required string CommandId { get; init; }
    public object? Arguments { get; init; }
    public TaskState State { get; set; } = TaskState.Created;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public object? Result { get; set; }
    public string? Error { get; set; }

    public double Progress { get; set; }
    public string? ProgressMessage { get; set; }

    public List<TaskLogEntry> Logs { get; } = new();

    public CancellationTokenSource? Cts { get; init; }

    public TimeSpan? Duration => StartedAt.HasValue
        ? (FinishedAt ?? DateTime.UtcNow) - StartedAt.Value
        : null;
}
