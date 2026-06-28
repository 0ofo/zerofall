using System;

namespace Datafinder.Base.Commands;

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
