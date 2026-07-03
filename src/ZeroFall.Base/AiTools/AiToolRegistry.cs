using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroFall.Base.AiTools;

public class AiToolRegistry
{
    private readonly Dictionary<string, ToolEntry> _tools = new();

    /// <summary>注册 OpenAI tools 数组元素 JSON（<c>{"type":"function","function":{...}}</c>），用于 MCP 等动态 schema。</summary>
    public void RegisterOpenAiToolJson(string name, Func<ToolArguments, Task<ToolCallResult>> executor, string toolsArrayElementJson)
    {
        _tools[name] = new ToolEntry(executor, definition: null, toolsArrayElementJson);
    }

    public void Register(string name, Func<ToolArguments, Task<ToolCallResult>> executor, ToolDefinition definition)
    {
        _tools[name] = new ToolEntry(executor, definition, openAiToolJson: null);
    }

    public void RegisterFromCatalog(ICapabilityCatalog catalog, ToolExecutionOrchestrator orchestrator)
    {
        foreach (var capability in catalog.GetAll())
        {
            Register(
                capability.Name,
                args => orchestrator.ExecuteAsync(capability, args),
                capability.Definition);
        }
    }

    public void UnregisterPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return;
        foreach (var key in _tools.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _tools.Remove(key);
    }

    public List<string> GetToolDefinitions()
    {
        return _tools
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => kvp.Value.OpenAiToolJson ?? kvp.Value.Definition!.ToOpenAiJson())
            .ToList();
    }

    public Task<ToolCallResult> ExecuteAsync(string toolName, string argumentsJson)
        => ExecuteAsync(toolName, argumentsJson, CancellationToken.None);

    public async Task<ToolCallResult> ExecuteAsync(string toolName, string argumentsJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_tools.TryGetValue(toolName, out var entry))
            return new ToolCallResult(ToolResultJson.Error($"未知工具: {toolName}"), toolName, exitCode: 1);

        ToolArguments args;
        try
        {
            args = ToolArguments.FromJson(argumentsJson);
        }
        catch (Exception ex)
        {
            return new ToolCallResult(ToolResultJson.Error($"解析参数失败: {ex.Message}"), toolName, exitCode: 1);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await entry.Executor(args);
            var normalized = ToolResultJson.Normalize(raw.ResultText, raw.ExitCode);
            return new ToolCallResult(
                normalized,
                raw.ToolName ?? toolName,
                raw.Command,
                raw.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolCallResult(ToolResultJson.Error($"工具执行失败: {ex.Message}"), toolName, exitCode: 1);
        }
    }

    private sealed class ToolEntry
    {
        public Func<ToolArguments, Task<ToolCallResult>> Executor { get; }
        public ToolDefinition? Definition { get; }
        /// <summary>若非 null，则 <see cref="AiToolRegistry.GetToolDefinitions"/> 直接使用该字符串（已是 tools[] 单元素 JSON）。</summary>
        public string? OpenAiToolJson { get; }

        public ToolEntry(Func<ToolArguments, Task<ToolCallResult>> executor, ToolDefinition? definition, string? openAiToolJson)
        {
            Executor = executor;
            Definition = definition;
            OpenAiToolJson = openAiToolJson;
        }
    }
}
