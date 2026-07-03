using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroFall.Base.Commands;

public class TaskProgressEvent
{
    public required string TaskId { get; init; }
    public double Progress { get; init; }
    public string? Message { get; init; }
}

public class TaskLogEvent
{
    public required string TaskId { get; init; }
    public required TaskLogEntry Entry { get; init; }
}

public interface ICommandService
{
    IDisposable RegisterCommand(CommandDefinition definition);
    CommandTask Submit(string commandId, object? args = null);
    Task<CommandTask> ExecuteAsync(string commandId, object? args = null);
    void Cancel(string taskId);
    CommandTask? GetTask(string taskId);
    IReadOnlyList<CommandTask> GetRunningTasks();
    IReadOnlyList<CommandDefinition> GetRegisteredCommands();
    bool IsCommandRegistered(string commandId);

    event EventHandler<CommandTask>? TaskStarted;
    event EventHandler<TaskProgressEvent>? TaskProgressChanged;
    event EventHandler<TaskLogEvent>? TaskLogEmitted;
    event EventHandler<CommandTask>? TaskCompleted;
    event EventHandler<CommandTask>? TaskFailed;
    event EventHandler<CommandTask>? TaskCancelled;
}

public sealed class CommandService : ICommandService
{
    private readonly Dictionary<string, CommandDefinition> _commands = new();
    private readonly Dictionary<string, CommandTask> _tasks = new();
    private readonly object _lock = new();

    public event EventHandler<CommandTask>? TaskStarted;
    public event EventHandler<TaskProgressEvent>? TaskProgressChanged;
    public event EventHandler<TaskLogEvent>? TaskLogEmitted;
    public event EventHandler<CommandTask>? TaskCompleted;
    public event EventHandler<CommandTask>? TaskFailed;
    public event EventHandler<CommandTask>? TaskCancelled;

    public IDisposable RegisterCommand(CommandDefinition definition)
    {
        lock (_lock)
        {
            _commands[definition.CommandId] = definition;
        }
        return new CommandRegistrationDisposable(this, definition.CommandId);
    }

    public CommandTask Submit(string commandId, object? args = null)
    {
        if (!_commands.TryGetValue(commandId, out var definition))
            throw new InvalidOperationException($"Command '{commandId}' is not registered.");

        var cts = definition.SupportsCancellation ? new CancellationTokenSource() : null;
        var task = new CommandTask
        {
            CommandId = commandId,
            Arguments = args,
            Cts = cts
        };

        lock (_lock)
        {
            _tasks[task.TaskId] = task;
        }

        _ = RunCommandAsync(task, definition, args);

        return task;
    }

    public async Task<CommandTask> ExecuteAsync(string commandId, object? args = null)
    {
        var task = Submit(commandId, args);

        while (task.State is TaskState.Created or TaskState.Queued or TaskState.Running)
        {
            await Task.Delay(50);
        }

        return task;
    }

    public void Cancel(string taskId)
    {
        CommandTask? task;
        lock (_lock)
        {
            if (!_tasks.TryGetValue(taskId, out task)) return;
        }

        if (task.State != TaskState.Running) return;

        task.Cts?.Cancel();
        task.State = TaskState.Cancelled;
        task.FinishedAt = DateTime.UtcNow;
        TaskCancelled?.Invoke(this, task);
    }

    public CommandTask? GetTask(string taskId)
    {
        lock (_lock)
        {
            return _tasks.TryGetValue(taskId, out var task) ? task : null;
        }
    }

    public IReadOnlyList<CommandTask> GetRunningTasks()
    {
        lock (_lock)
        {
            return _tasks.Values.Where(t => t.State == TaskState.Running).ToList();
        }
    }

    public IReadOnlyList<CommandDefinition> GetRegisteredCommands()
    {
        lock (_lock)
        {
            return _commands.Values.ToList();
        }
    }

    public bool IsCommandRegistered(string commandId)
    {
        lock (_lock)
        {
            return _commands.ContainsKey(commandId);
        }
    }

    private async Task RunCommandAsync(CommandTask task, CommandDefinition definition, object? args)
    {
        task.State = TaskState.Running;
        task.StartedAt = DateTime.UtcNow;
        TaskStarted?.Invoke(this, task);

        var progressReporter = new TaskProgressReporter(task, this);
        var taskLogger = new TaskLogger(task, this);
        var context = new CommandContext
        {
            CancellationToken = task.Cts?.Token ?? CancellationToken.None,
            Progress = progressReporter,
            Logger = taskLogger
        };

        try
        {
            object? result = null;

            if (definition is CommandDefinition<object?, object?> typedDef)
            {
                result = await typedDef.Handler(args!, context);
            }
            else
            {
                var handlerMethod = definition.GetType().GetProperty("Handler");
                if (handlerMethod?.GetValue(definition) is Delegate handlerDelegate)
                {
                    result = await (Task<object?>)handlerDelegate.DynamicInvoke(args, context)!;
                }
            }

            task.Result = result;
            task.State = TaskState.Completed;
            task.Progress = 1.0;
            task.FinishedAt = DateTime.UtcNow;
            TaskCompleted?.Invoke(this, task);
        }
        catch (OperationCanceledException)
        {
            task.State = TaskState.Cancelled;
            task.FinishedAt = DateTime.UtcNow;
            TaskCancelled?.Invoke(this, task);
        }
        catch (Exception ex)
        {
            task.State = TaskState.Failed;
            task.Error = ex.Message;
            task.FinishedAt = DateTime.UtcNow;
            TaskFailed?.Invoke(this, task);
        }
    }

    private sealed class TaskProgressReporter : IProgressReporter
    {
        private readonly CommandTask _task;
        private readonly CommandService _service;

        public TaskProgressReporter(CommandTask task, CommandService service)
        {
            _task = task;
            _service = service;
        }

        public void Report(double progress, string? message = null)
        {
            _task.Progress = Math.Clamp(progress, 0, 1);
            _task.ProgressMessage = message;
            _service.TaskProgressChanged?.Invoke(_service, new TaskProgressEvent
            {
                TaskId = _task.TaskId,
                Progress = _task.Progress,
                Message = message
            });
        }
    }

    private sealed class TaskLogger : ITaskLogger
    {
        private readonly CommandTask _task;
        private readonly CommandService _service;

        public TaskLogger(CommandTask task, CommandService service)
        {
            _task = task;
            _service = service;
        }

        public void Info(string message) => Emit(TaskLogLevel.Info, message);
        public void Warn(string message) => Emit(TaskLogLevel.Warn, message);
        public void Error(string message) => Emit(TaskLogLevel.Error, message);

        private void Emit(TaskLogLevel level, string message)
        {
            var entry = new TaskLogEntry { Level = level, Message = message };
            _task.Logs.Add(entry);
            _service.TaskLogEmitted?.Invoke(_service, new TaskLogEvent
            {
                TaskId = _task.TaskId,
                Entry = entry
            });
        }
    }

    private sealed class CommandRegistrationDisposable : IDisposable
    {
        private readonly CommandService _service;
        private readonly string _commandId;
        private bool _disposed;

        public CommandRegistrationDisposable(CommandService service, string commandId)
        {
            _service = service;
            _commandId = commandId;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_service._lock)
            {
                _service._commands.Remove(_commandId);
            }
        }
    }
}
