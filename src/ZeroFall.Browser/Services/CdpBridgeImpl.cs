using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Base.Diagnostics;
using ZeroFall.Browser.ComInterop;
using ZeroFall.Platform.Services;

namespace ZeroFall.Browser.Services;

public class CdpBridge : ICdpBridge
{
    private static readonly bool EnableUnsafeNativeCdp =
        !string.Equals(Environment.GetEnvironmentVariable("DATAFINDER_DISABLE_NATIVE_CDP"), "1", StringComparison.Ordinal);

    public static readonly CdpBridge Instance = new();

    private readonly ConcurrentDictionary<string, WebView2NativeWrapper> _sessions = new();
    private string? _activeTabId;

    private static string ErrorJson(string message) =>
        $"{{\"error\":\"{JsonEncode(message)}\"}}";

    private static string JsonEncode(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    public void Register(string tabId, WebView2NativeWrapper wrapper)
    {
        _sessions[tabId] = wrapper;
        _activeTabId = tabId;
        AppDiagnostics.Mark($"CdpBridge register tab={tabId} sessions={_sessions.Count}");
    }

    public void Unregister(string tabId)
    {
        if (_sessions.TryRemove(tabId, out var wrapper))
            wrapper.Dispose();

        if (_activeTabId == tabId)
            _activeTabId = null;
        AppDiagnostics.Mark($"CdpBridge unregister tab={tabId} sessions={_sessions.Count}");
    }

    public void SetActiveTab(string tabId)
    {
        if (_sessions.ContainsKey(tabId))
            _activeTabId = tabId;
    }

    public bool HasSession(string tabId) => _sessions.ContainsKey(tabId);

    public string? ActiveTabId => _activeTabId;

    public IReadOnlyList<string> GetRegisteredTabIds() => _sessions.Keys.ToList();

    private WebView2NativeWrapper? GetByTabId(string tabId) =>
        _sessions.TryGetValue(tabId, out var wrapper) ? wrapper : null;

    private WebView2NativeWrapper? GetActive()
    {
        if (_activeTabId != null && _sessions.TryGetValue(_activeTabId, out var wrapper))
            return wrapper;
        foreach (var kv in _sessions)
        {
            _activeTabId = kv.Key;
            return kv.Value;
        }

        return null;
    }

    public Task<string> CallMethodAsync(string method, string? parametersAsJson = null)
    {
        var wrapper = GetActive();
        if (wrapper == null)
            return Task.FromResult(ErrorJson("没有可用的浏览器会话"));
        return CallMethodOnWrapperAsync(wrapper, method, parametersAsJson);
    }

    public Task<string> CallMethodOnTabAsync(string tabId, string method, string? parametersAsJson = null)
    {
        var wrapper = GetByTabId(tabId);
        if (wrapper == null)
            return Task.FromResult(ErrorJson($"浏览器标签 {tabId} 未就绪"));
        return CallMethodOnWrapperAsync(wrapper, method, parametersAsJson);
    }

    public async Task<string> ExecuteScriptOnTabAsync(string tabId, string script, TimeSpan? timeout = null)
    {
        AppDiagnostics.Mark($"CdpBridge ExecuteScript tab={tabId} timeoutMs={(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds:0}");
        var wrapper = GetByTabId(tabId);
        if (wrapper == null)
            return ErrorJson($"浏览器标签 {tabId} 未就绪");

        try
        {
            return await RunOnUiThreadWithScriptGateAsync(async () =>
            {
                var result = await wrapper.ExecuteScriptAsync(script, timeout).ConfigureAwait(true);
                AppDiagnostics.Mark($"CdpBridge ExecuteScript returned tab={tabId}");
                return result ?? "null";
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Exception($"CdpBridge ExecuteScript failed tab={tabId}", ex);
            return ErrorJson(ex.Message);
        }
    }

    public async Task<bool> WaitForSessionAsync(string tabId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tabId))
            return false;

        tabId = tabId.Trim();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasSession(tabId))
                return true;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        return HasSession(tabId);
    }

    private async Task<string> CallMethodOnWrapperAsync(
        WebView2NativeWrapper wrapper,
        string method,
        string? parametersAsJson)
    {
        if (!EnableUnsafeNativeCdp)
            return ErrorJson("浏览器 CDP 工具暂时处于安全模式（已禁用原生调用，避免崩溃）");

        try
        {
            AppDiagnostics.Mark($"CdpBridge CDP begin method={method}");
            var result = await RunOnUiThreadWithScriptGateAsync(async () =>
            {
                var json = await wrapper.CallDevToolsProtocolMethodAsync(method, parametersAsJson ?? "{}")
                    .ConfigureAwait(true);
                AppDiagnostics.Mark($"CdpBridge CDP returned method={method}");
                return json ?? "{}";
            }).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Exception($"CdpBridge CDP failed method={method}", ex);
            return ErrorJson(ex.Message);
        }
    }

    private static Task<T> RunOnUiThreadWithScriptGateAsync<T>(Func<Task<T>> action) =>
        UiThreadBridge.InvokeAsync(async () =>
        {
            BrowserUiGate.Enter();
            try
            {
                return await action().ConfigureAwait(true);
            }
            finally
            {
                BrowserUiGate.Exit();
            }
        });

    public async Task<string> CaptureScreenshotAsync(string? format = null, int? quality = null)
    {
        if (!EnableUnsafeNativeCdp)
            return ErrorJson("浏览器截图工具暂时处于安全模式（已禁用原生调用，避免崩溃）");

        var wrapper = GetActive();
        if (wrapper == null)
            return ErrorJson("没有可用的浏览器会话");

        try
        {
            return await RunOnUiThreadWithScriptGateAsync(async () =>
            {
                AppDiagnostics.Mark("CdpBridge screenshot begin");
                string json;
                if (format != null || quality.HasValue)
                {
                    var sb = new StringBuilder("{");
                    var first = true;
                    if (format != null)
                    {
                        sb.Append($"\"format\":\"{JsonEncode(format)}\"");
                        first = false;
                    }

                    if (quality.HasValue)
                    {
                        if (!first) sb.Append(',');
                        sb.Append($"\"quality\":{quality.Value}");
                    }

                    sb.Append('}');
                    json = sb.ToString();
                }
                else
                {
                    json = "{}";
                }

                var result = await wrapper.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", json)
                    .ConfigureAwait(true);
                AppDiagnostics.Mark("CdpBridge screenshot returned");
                return result ?? "{}";
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Exception("CdpBridge screenshot failed", ex);
            return ErrorJson(ex.Message);
        }
    }

    public Task<string?> GetCurrentUrlAsync()
    {
        var wrapper = GetActive();
        if (wrapper == null)
            return Task.FromResult<string?>(null);

        return UiThreadBridge.InvokeAsync<string?>(() =>
        {
            BrowserUiGate.Enter();
            try
            {
                return wrapper.GetSource();
            }
            finally
            {
                BrowserUiGate.Exit();
            }
        });
    }

    public string? GetCurrentUrl()
    {
        try
        {
            return GetCurrentUrlAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }
}
