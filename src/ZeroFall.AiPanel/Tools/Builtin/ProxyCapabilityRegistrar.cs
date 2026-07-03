using System.Threading.Tasks;
using System.Text.Json;
using ZeroFall.Base.AiTools;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Serialization;
using ZeroFall.Platform.Services;

namespace ZeroFall.AiPanel.Tools.Builtin;

public sealed class ProxyCapabilityRegistrar
{
    private readonly ISettingsService _settingsService;
    private readonly IProxyGatewayService _proxyGatewayService;
    private readonly ProxyRuntimeCoordinator _proxyCoordinator;

    public ProxyCapabilityRegistrar(
        ISettingsService settingsService,
        IProxyGatewayService proxyGatewayService,
        ProxyRuntimeCoordinator proxyCoordinator)
    {
        _settingsService = settingsService;
        _proxyGatewayService = proxyGatewayService;
        _proxyCoordinator = proxyCoordinator;
    }

    public void Register(ICapabilityCatalog catalog)
    {
        catalog.Register(new ToolCapability
        {
            Name = "proxy",
            Description = "读取或切换统一代理。action=get 返回当前状态；action=set 切换模式（需 confirmed=true）。",
            RequiresConfirmation = false,
            Definition = new ToolDefinition
            {
                Name = "proxy",
                Description = "读取或切换统一代理。action=get 返回当前状态；action=set 切换模式（需 confirmed=true）。",
                Parameters =
                [
                    new ToolParameterDefinition { Name = "action", Type = "string", Description = "get 或 set", Required = true, EnumValues = ["get", "set"] },
                    new ToolParameterDefinition { Name = "mode", Type = "string", Description = "代理模式（set 时必填）", Required = false, EnumValues = [ProxyModes.Direct, ProxyModes.System, ProxyModes.Fixed, ProxyModes.FluxzyGateway] },
                    new ToolParameterDefinition { Name = "upstreamProxyUrl", Type = "string", Description = "上游代理地址（fixed/fluxzy_gateway 时可选）", Required = false },
                    new ToolParameterDefinition { Name = "confirmed", Type = "boolean", Description = "set 时是否确认执行", Required = false }
                ]
            },
            Executor = async args =>
            {
                if (!args.TryGetString("action", out var action) || string.IsNullOrWhiteSpace(action))
                    return new ToolCallResult(ToolResultJson.Error("action 不能为空"), "proxy");

                var act = action.Trim().ToLowerInvariant();
                if (act == "get")
                {
                    var state = _proxyGatewayService.CurrentState;
                    var json = JsonSerializer.Serialize(state, PlatformJsonContext.Default.ProxyRuntimeState);
                    return new ToolCallResult(json, "proxy");
                }

                if (act != "set")
                    return new ToolCallResult(ToolResultJson.Error("action 必须是 get 或 set"), "proxy");

                if (!args.TryGetBoolean("confirmed", out var confirmed) || !confirmed)
                    return new ToolCallResult(ToolResultJson.Error("切换代理需要 confirmed=true"), "proxy");

                if (!args.TryGetString("mode", out var mode) || string.IsNullOrWhiteSpace(mode))
                    return new ToolCallResult(ToolResultJson.Error("set 需要 mode"), "proxy");

                args.TryGetString("upstreamProxyUrl", out var upstream);

                var settings = _settingsService.Load();
                settings.Proxy.Mode = mode.Trim();
                settings.Proxy.UpstreamProxyUrl = upstream ?? string.Empty;
                if (!_settingsService.Save(settings))
                    return new ToolCallResult(
                        ToolResultJson.Error(_settingsService.LastError ?? "保存代理设置失败"),
                        "proxy");

                var applied = await _proxyCoordinator.ApplyAsync(settings.Proxy);
                var resultJson = JsonSerializer.Serialize(new ProxySwitchResultDto
                {
                    Ok = true,
                    EffectiveMode = applied.EffectiveMode,
                    EffectiveEndpoint = applied.EffectiveEndpoint,
                    IsDegraded = applied.IsDegraded,
                    Message = applied.Message
                }, PlatformJsonContext.Default.ProxySwitchResultDto);
                return new ToolCallResult(resultJson, "proxy");
            }
        });
    }
}
