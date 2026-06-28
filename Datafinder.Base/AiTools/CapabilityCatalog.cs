using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Datafinder.Base.AiTools;

public sealed class ToolCapability
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required ToolDefinition Definition { get; init; }
    public bool RequiresConfirmation { get; init; }
    public required Func<ToolArguments, Task<ToolCallResult>> Executor { get; init; }
}

public interface ICapabilityCatalog
{
    void Register(ToolCapability capability);
    IReadOnlyList<ToolCapability> GetAll();
}

public sealed class CapabilityCatalog : ICapabilityCatalog
{
    private readonly Dictionary<string, ToolCapability> _capabilities = new(StringComparer.Ordinal);

    public void Register(ToolCapability capability)
    {
        _capabilities[capability.Name] = capability;
    }

    public IReadOnlyList<ToolCapability> GetAll()
    {
        return _capabilities.Values.ToList();
    }
}

public sealed class ToolExecutionOrchestrator
{
    public async Task<ToolCallResult> ExecuteAsync(ToolCapability capability, ToolArguments args)
    {
        if (capability.RequiresConfirmation)
        {
            if (!args.TryGetBoolean("confirmed", out var confirmed) || !confirmed)
            {
                return new ToolCallResult(ToolResultJson.Error("该操作需要确认，请传入 confirmed=true。"), capability.Name);
            }
        }

        return await capability.Executor(args);
    }
}
