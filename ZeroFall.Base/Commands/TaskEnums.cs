using System;

namespace ZeroFall.Base.Commands;

public enum TaskState
{
    Created,
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum TaskLogLevel
{
    Info,
    Warn,
    Error
}
