using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Browser.ComInterop;

namespace ZeroFall.Browser.Services;

public interface ICdpBridge
{
    void Register(string tabId, WebView2NativeWrapper wrapper);
    void Unregister(string tabId);
    void SetActiveTab(string tabId);
    bool HasSession(string tabId);
    string? ActiveTabId { get; }
    IReadOnlyList<string> GetRegisteredTabIds();
    Task<string> CallMethodAsync(string method, string? parametersAsJson = null);
    Task<string> CallMethodOnTabAsync(string tabId, string method, string? parametersAsJson = null);
    Task<string> ExecuteScriptOnTabAsync(string tabId, string script, TimeSpan? timeout = null);
    Task<bool> WaitForSessionAsync(string tabId, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<string> CaptureScreenshotAsync(string? format = null, int? quality = null);
    string? GetCurrentUrl();
}
