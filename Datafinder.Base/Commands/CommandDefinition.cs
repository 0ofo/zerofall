using System;
using System.Threading.Tasks;

namespace Datafinder.Base.Commands;

public abstract class CommandDefinition
{
    public required string CommandId { get; init; }
    public required string DisplayName { get; init; }
    public string? Category { get; init; }
    public string? Description { get; init; }
    public bool SupportsProgress { get; init; }
    public bool SupportsCancellation { get; init; }
}

public class CommandDefinition<TArgs, TResult> : CommandDefinition
{
    public required Func<TArgs, CommandContext, Task<TResult>> Handler { get; init; }
}
