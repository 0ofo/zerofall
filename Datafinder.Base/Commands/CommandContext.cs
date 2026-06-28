using System;
using System.Threading;

namespace Datafinder.Base.Commands;

public interface IProgressReporter
{
    void Report(double progress, string? message = null);
}

public interface ITaskLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public class CommandContext : IProgressReporter, ITaskLogger
{
    public required CancellationToken CancellationToken { get; init; }
    public required IProgressReporter Progress { get; init; }
    public required ITaskLogger Logger { get; init; }

    public void Report(double progress, string? message = null) => Progress.Report(progress, message);
    public void Info(string message) => Logger.Info(message);
    public void Warn(string message) => Logger.Warn(message);
    public void Error(string message) => Logger.Error(message);
}
