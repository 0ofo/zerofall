using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Base.Diagnostics;
using ZeroFall.Browser.Services;
using ZeroFall.Browser.ViewModels;
using ZeroFall.Platform.Models;
using ZeroFall.Traffic;
using ZeroFall.Traffic.Capture;

namespace ZeroFall.Browser.ComInterop;

public sealed class WebView2NativeWrapper : IDisposable
{
    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private nint _unknownPtr;
    private nint _corePtr;
    private nint _core2Ptr;
    private nint _core14Ptr;
    private nint _core15Ptr;
    private long _responseReceivedCookie;
    private long _webResourceRequestedCookie;
    private long _documentTitleChangedCookie;
    private long _faviconChangedCookie;
    private int _faviconFetchGeneration;
    private CancellationTokenSource? _faviconDebounceCts;
    private long _serverCertCookie;
    private bool _disposed;
    private BrowserTabViewModel? _tabVm;

    private nint _titleChangedHandlerPtr;
    private nint _responseReceivedHandlerPtr;
    private nint _webResourceRequestedHandlerPtr;
    private nint _serverCertHandlerPtr;
    private nint _faviconChangedHandlerPtr;
    private ICoreWebView2DocumentTitleChangedEventHandler? _titleChangedHandler;
    private ICoreWebView2WebResourceResponseReceivedEventHandler? _responseReceivedHandler;
    private ICoreWebView2WebResourceRequestedEventHandler? _webResourceRequestedHandler;
    private ICoreWebView2ServerCertificateErrorDetectedEventHandler? _serverCertHandler;
    private ICoreWebView2FaviconChangedEventHandler? _faviconChangedHandler;
    private nint _cdpCompletedHandlerPtr;
    private CdpCompletedEventHandler? _cdpCompletedHandler;
    private readonly SemaphoreSlim _cdpCallGate = new(1, 1);
    private readonly object _cdpStateLock = new();
    private TaskCompletionSource<string>? _pendingCdpTcs;
    private string _pendingCdpMethod = string.Empty;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _getContentStateLock = new();
    private readonly Dictionary<long, PendingGetContentRequest> _pendingGetContentRequests = [];
    private long _getContentRequestId;
    private readonly object _requestTimingLock = new();
    private readonly Dictionary<string, Queue<long>> _requestStartTicks = [];
    private readonly Dictionary<string, Queue<WebTrafficResourceContext>> _requestResourceContexts = [];

    private sealed class PendingGetContentRequest
    {
        public required nint HandlerPtr { get; init; }
        public required ICoreWebView2WebResourceResponseViewGetContentCompletedHandler Handler { get; init; }
        public required TaskCompletionSource<nint> CompletionSource { get; init; }
    }

    public bool IsDisposed => _disposed;

    private WebView2NativeWrapper(nint unknownPtr, BrowserTabViewModel tabVm)
    {
        _unknownPtr = unknownPtr;
        _tabVm = tabVm;
        ComHelper.AddRef(_unknownPtr);
    }

    public static WebView2NativeWrapper? TryCreate(nint unknownPtr, BrowserTabViewModel tabVm)
    {
        if (unknownPtr == IntPtr.Zero) return null;

        try
        {
            var wrapper = new WebView2NativeWrapper(unknownPtr, tabVm);
            wrapper.Initialize();
            return wrapper;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] Failed: {ex}");
            return null;
        }
    }

    private void Initialize()
    {
        var hr = ComHelper.QueryInterface(_unknownPtr, in ICoreWebView2VTable.IID, out _corePtr);
        if (hr != 0)
            throw new COMException($"QueryInterface ICoreWebView2 failed: 0x{hr:X8}", hr);

        hr = ComHelper.QueryInterface(_unknownPtr, in ICoreWebView2_2VTable.IID, out _core2Ptr);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] ICoreWebView2_2 not available: 0x{hr:X8}");
            _core2Ptr = IntPtr.Zero;
        }

        hr = ComHelper.QueryInterface(_unknownPtr, in ICoreWebView2_14VTable.IID, out _core14Ptr);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] ICoreWebView2_14 not available: 0x{hr:X8}");
            _core14Ptr = IntPtr.Zero;
        }

        hr = ComHelper.QueryInterface(_unknownPtr, in ICoreWebView2_15VTable.IID, out _core15Ptr);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] ICoreWebView2_15 not available: 0x{hr:X8}");
            _core15Ptr = IntPtr.Zero;
        }
    }

    public bool AttachEvents()
    {
        try
        {
            ApplySecurityResearchSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] ApplySecurityResearchSettings failed: {ex}");
        }

        try
        {
            AttachDocumentTitleChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] AttachDocumentTitleChanged failed: {ex}");
        }

        try
        {
            AttachResponseReceived();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] AttachResponseReceived failed: {ex}");
        }

        try
        {
            AttachWebResourceRequested();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] AttachWebResourceRequested failed: {ex}");
        }

        try
        {
            AddWebResourceRequestedFilter();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] AddWebResourceRequestedFilter failed: {ex}");
        }

        try
        {
            AttachServerCertificateBypass();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] AttachServerCertificateBypass failed: {ex}");
        }

        try
        {
            AttachFaviconChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] AttachFaviconChanged failed: {ex}");
        }

        return true;
    }

    /// <summary>
    /// 安全工具内置浏览器必须能打开被 SmartScreen 标记、证书过期/自签等目标。
    /// 这里通过 AOT 兼容 COM vtable 关闭 WebView2 SmartScreen 信誉检查。
    /// </summary>
    private void ApplySecurityResearchSettings()
    {
        if (_corePtr == IntPtr.Zero)
            return;

        var hr = ICoreWebView2VTable.GetSettings(_corePtr, out var settingsPtr);
        if (hr != 0 || settingsPtr == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] get_Settings failed: 0x{hr:X8}");
            return;
        }

        nint settings8Ptr = IntPtr.Zero;
        try
        {
            hr = ComHelper.QueryInterface(settingsPtr, in ICoreWebView2Settings8VTable.IID, out settings8Ptr);
            if (hr != 0 || settings8Ptr == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] ICoreWebView2Settings8 not available: 0x{hr:X8}");
                return;
            }

            hr = ICoreWebView2Settings8VTable.PutIsReputationCheckingRequired(settings8Ptr, 0);
            if (hr != 0)
                System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] put_IsReputationCheckingRequired(false) failed: 0x{hr:X8}");
        }
        finally
        {
            if (settings8Ptr != IntPtr.Zero)
                ComHelper.Release(settings8Ptr);
            ComHelper.Release(settingsPtr);
        }
    }

    private void AttachDocumentTitleChanged()
    {
        if (_corePtr == IntPtr.Zero) return;

        _titleChangedHandler = new DocumentTitleChangedEventHandler((sender, args) =>
            OnDocumentTitleChanged(IntPtr.Zero, sender, args));
        unsafe
        {
            _titleChangedHandlerPtr = (nint)ComInterfaceMarshaller<ICoreWebView2DocumentTitleChangedEventHandler>.ConvertToUnmanaged(_titleChangedHandler);
        }

        var hr = ICoreWebView2VTable.AddDocumentTitleChanged(_corePtr, _titleChangedHandlerPtr, out _documentTitleChangedCookie);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] add_DocumentTitleChanged failed: 0x{hr:X8}");
            if (_titleChangedHandlerPtr != IntPtr.Zero)
            {
                unsafe
                {
                    ComInterfaceMarshaller<ICoreWebView2DocumentTitleChangedEventHandler>.Free((void*)_titleChangedHandlerPtr);
                }
                _titleChangedHandlerPtr = IntPtr.Zero;
            }
            _titleChangedHandler = null;
        }
    }

    private void AttachFaviconChanged()
    {
        if (_core15Ptr == IntPtr.Zero) return;

        _faviconChangedHandler = new FaviconChangedEventHandler((sender, args) =>
            OnFaviconChanged(IntPtr.Zero, sender, args));
        unsafe
        {
            _faviconChangedHandlerPtr = (nint)ComInterfaceMarshaller<ICoreWebView2FaviconChangedEventHandler>.ConvertToUnmanaged(_faviconChangedHandler);
        }

        var hr = ICoreWebView2_15VTable.AddFaviconChanged(_core15Ptr, _faviconChangedHandlerPtr, out _faviconChangedCookie);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] add_FaviconChanged failed: 0x{hr:X8}");
            if (_faviconChangedHandlerPtr != IntPtr.Zero)
            {
                unsafe
                {
                    ComInterfaceMarshaller<ICoreWebView2FaviconChangedEventHandler>.Free((void*)_faviconChangedHandlerPtr);
                }
                _faviconChangedHandlerPtr = IntPtr.Zero;
            }
            _faviconChangedHandler = null;
        }
    }

    private void AttachResponseReceived()
    {
        if (_core2Ptr == IntPtr.Zero) return;

        _responseReceivedHandler = new WebResourceResponseReceivedEventHandler((sender, args) =>
            OnResponseReceived(IntPtr.Zero, sender, args));
        unsafe
        {
            _responseReceivedHandlerPtr = (nint)ComInterfaceMarshaller<ICoreWebView2WebResourceResponseReceivedEventHandler>.ConvertToUnmanaged(_responseReceivedHandler);
        }

        var hr = ICoreWebView2_2VTable.AddWebResourceResponseReceived(_core2Ptr, _responseReceivedHandlerPtr, out _responseReceivedCookie);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] add_WebResourceResponseReceived failed: 0x{hr:X8}");
            if (_responseReceivedHandlerPtr != IntPtr.Zero)
            {
                unsafe
                {
                    ComInterfaceMarshaller<ICoreWebView2WebResourceResponseReceivedEventHandler>.Free((void*)_responseReceivedHandlerPtr);
                }
                _responseReceivedHandlerPtr = IntPtr.Zero;
            }
            _responseReceivedHandler = null;
        }
    }

    private void AttachWebResourceRequested()
    {
        if (_corePtr == IntPtr.Zero) return;

        _webResourceRequestedHandler = new WebResourceRequestedEventHandler((sender, args) =>
            OnWebResourceRequested(IntPtr.Zero, sender, args));
        unsafe
        {
            _webResourceRequestedHandlerPtr = (nint)ComInterfaceMarshaller<ICoreWebView2WebResourceRequestedEventHandler>.ConvertToUnmanaged(_webResourceRequestedHandler);
        }

        var hr = ICoreWebView2VTable.AddWebResourceRequested(_corePtr, _webResourceRequestedHandlerPtr, out _webResourceRequestedCookie);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] add_WebResourceRequested failed: 0x{hr:X8}");
            if (_webResourceRequestedHandlerPtr != IntPtr.Zero)
            {
                unsafe
                {
                    ComInterfaceMarshaller<ICoreWebView2WebResourceRequestedEventHandler>.Free((void*)_webResourceRequestedHandlerPtr);
                }
                _webResourceRequestedHandlerPtr = IntPtr.Zero;
            }
            _webResourceRequestedHandler = null;
        }
    }

    private void AddWebResourceRequestedFilter()
    {
        if (_corePtr == IntPtr.Zero) return;
        var hr = ICoreWebView2VTable.AddWebResourceRequestedFilter(_corePtr, "*", 0);
        if (hr != 0)
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] AddWebResourceRequestedFilter failed: 0x{hr:X8}");
    }

    /// <summary>
    /// 安全/渗透工具场景：对 TLS 证书错误（含过期、自签）自动放行，避免停在错误页导致 CDP/脚本无法继续。
    /// </summary>
    private void AttachServerCertificateBypass()
    {
        if (_core14Ptr == IntPtr.Zero) return;

        _serverCertHandler = new ServerCertificateErrorDetectedEventHandler(OnServerCertificateErrorDetected);
        unsafe
        {
            _serverCertHandlerPtr = (nint)ComInterfaceMarshaller<ICoreWebView2ServerCertificateErrorDetectedEventHandler>.ConvertToUnmanaged(_serverCertHandler);
        }

        var hr = ICoreWebView2_14VTable.AddServerCertificateErrorDetected(_core14Ptr, _serverCertHandlerPtr, out _serverCertCookie);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] add_ServerCertificateErrorDetected failed: 0x{hr:X8}");
            if (_serverCertHandlerPtr != IntPtr.Zero)
            {
                unsafe
                {
                    ComInterfaceMarshaller<ICoreWebView2ServerCertificateErrorDetectedEventHandler>.Free((void*)_serverCertHandlerPtr);
                }
                _serverCertHandlerPtr = IntPtr.Zero;
            }
            _serverCertHandler = null;
            _serverCertCookie = 0;
        }
    }

    private static int OnServerCertificateErrorDetected(nint sender, nint argsPtr)
    {
        if (argsPtr == IntPtr.Zero) return 0;
        try
        {
            const int alwaysAllow = 0; // COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_ALWAYS_ALLOW
            var hr = ICoreWebView2ServerCertificateErrorDetectedEventArgsVTable.PutAction(argsPtr, alwaysAllow);
            if (hr != 0)
                System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] put_Action(AlwaysAllow) failed: 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] OnServerCertificateErrorDetected: {ex}");
        }
        return 0;
    }

    public string? GetDocumentTitle()
    {
        if (_corePtr == IntPtr.Zero) return null;
        var hr = ICoreWebView2VTable.GetDocumentTitle(_corePtr, out var titlePtr);
        if (hr != 0) return null;
        return ComHelper.ReadCoTaskMemString(titlePtr);
    }

    /// <summary>打开 DevTools 窗口（若已打开则不重复打开）。</summary>
    public bool OpenDevToolsWindow()
    {
        if (_corePtr == IntPtr.Zero) return false;
        var hr = ICoreWebView2VTable.OpenDevToolsWindow(_corePtr);
        return hr == 0;
    }

    public string? GetSource()
    {
        if (_corePtr == IntPtr.Zero) return null;
        var hr = ICoreWebView2VTable.GetSource(_corePtr, out var uriPtr);
        if (hr != 0) return null;
        return ComHelper.ReadCoTaskMemString(uriPtr);
    }

    public Task<string> CallDevToolsProtocolMethodAsync(string method, string parametersAsJson)
    {
        return CallDevToolsProtocolMethodCoreAsync(method, parametersAsJson);
    }

    public Task<string> ExecuteScriptAsync(string script, TimeSpan? timeout = null)
    {
        AppDiagnostics.Mark($"WebView ExecuteScript requested timeoutMs={(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds:0} length={script?.Length ?? 0}");
        return ExecuteScriptCoreAsync(script ?? string.Empty, timeout ?? TimeSpan.FromSeconds(10));
    }

    private async Task<string> ExecuteScriptCoreAsync(string script, TimeSpan timeout)
    {
        if (_corePtr == IntPtr.Zero || _disposed)
        {
            AppDiagnostics.Mark("WebView ExecuteScript skipped no-core");
            return "{\"error\":\"No CoreWebView2 available\"}";
        }

        try
        {
            AppDiagnostics.Mark("WebView ExecuteScript wait gate");
            await _cdpCallGate.WaitAsync(_disposeCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return "{\"error\":\"ExecuteScript cancelled: wrapper disposed\"}";
        }

        try
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            nint handlerPtr = IntPtr.Zero;
            var freeInCallback = 0;
            var freed = 0;

            void FreeHandlerPtr()
            {
                if (handlerPtr == IntPtr.Zero || Interlocked.Exchange(ref freed, 1) != 0)
                    return;

                unsafe
                {
                    ComInterfaceMarshaller<ICoreWebView2ExecuteScriptCompletedHandler>.Free((void*)handlerPtr);
                }
            }

            ICoreWebView2ExecuteScriptCompletedHandler? handler = null;
            handler = new ExecuteScriptCompletedEventHandler((errorCode, resultObjectAsJson) =>
            {
                try
                {
                    if (errorCode != 0)
                        tcs.TrySetResult($"{{\"error\":\"ExecuteScript failed: 0x{errorCode:X8}\"}}");
                    else
                        tcs.TrySetResult(ComHelper.ReadWideStringNoFree(resultObjectAsJson) ?? "null");
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult($"{{\"error\":\"ExecuteScript callback error: {JsonEscape(ex.Message)}\"}}");
                }
                finally
                {
                    if (Volatile.Read(ref freeInCallback) != 0)
                        FreeHandlerPtr();
                }

                return 0;
            });

            unsafe
            {
                handlerPtr = (nint)ComInterfaceMarshaller<ICoreWebView2ExecuteScriptCompletedHandler>.ConvertToUnmanaged(handler);
            }

            try
            {
                var hr = ICoreWebView2VTable.ExecuteScript(_corePtr, script, handlerPtr);
                if (hr != 0)
                {
                    AppDiagnostics.Mark($"WebView ExecuteScript start failed hr=0x{hr:X8}");
                    return $"{{\"error\":\"ExecuteScript failed: 0x{hr:X8}\"}}";
                }

                AppDiagnostics.Mark("WebView ExecuteScript native call accepted");
                var delay = timeout > TimeSpan.Zero
                    ? Task.Delay(timeout, _disposeCts.Token)
                    : Task.Delay(TimeSpan.FromSeconds(10), _disposeCts.Token);
                var completed = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(true);
                if (completed != tcs.Task)
                {
                    Volatile.Write(ref freeInCallback, 1);
                    AppDiagnostics.Mark($"WebView ExecuteScript timeout seconds={Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds))}");
                    return $"{{\"error\":\"ExecuteScript timeout ({Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds))}s)\"}}";
                }

                AppDiagnostics.Mark("WebView ExecuteScript completed");
                return await tcs.Task.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref freeInCallback, 1);
                AppDiagnostics.Mark("WebView ExecuteScript cancelled wrapper disposed");
                return "{\"error\":\"ExecuteScript cancelled: wrapper disposed\"}";
            }
            finally
            {
                if (Volatile.Read(ref freeInCallback) == 0)
                    FreeHandlerPtr();
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Exception("WebView ExecuteScript failed", ex);
            throw;
        }
        finally
        {
            AppDiagnostics.Mark("WebView ExecuteScript gate release");
            _cdpCallGate.Release();
        }
    }

    private async Task<string> CallDevToolsProtocolMethodCoreAsync(string method, string parametersAsJson)
    {
        if (_corePtr == IntPtr.Zero || _disposed)
        {
            AppDiagnostics.Mark($"CDP skipped no-core method={method}");
            return "{\"error\":\"No CoreWebView2 available\"}";
        }

        try
        {
            AppDiagnostics.Mark($"CDP wait gate method={method}");
            await _cdpCallGate.WaitAsync(_disposeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return "{\"error\":\"CDP call cancelled: wrapper disposed\"}";
        }

        try
        {
            AppDiagnostics.Mark($"CDP call begin method={method} paramsLength={parametersAsJson?.Length ?? 0}");
            EnsureCdpCompletedHandler();

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_cdpStateLock)
            {
                _pendingCdpTcs = tcs;
                _pendingCdpMethod = method;
            }

            var hr = ICoreWebView2VTable.CallDevToolsProtocolMethod(_corePtr, method, parametersAsJson ?? "{}", _cdpCompletedHandlerPtr);
            if (hr != 0)
            {
                lock (_cdpStateLock)
                {
                    _pendingCdpTcs = null;
                    _pendingCdpMethod = string.Empty;
                }
                AppDiagnostics.Mark($"CDP native call failed method={method} hr=0x{hr:X8}");
                return $"{{\"error\":\"CallDevToolsProtocolMethod failed: 0x{hr:X8}\"}}";
            }

            Task completed;
            try
            {
                completed = await Task.WhenAny(tcs.Task, Task.Delay(10000, _disposeCts.Token));
            }
            catch (OperationCanceledException)
            {
                lock (_cdpStateLock)
                {
                    _pendingCdpTcs = null;
                    _pendingCdpMethod = string.Empty;
                }
                return "{\"error\":\"CDP call cancelled: wrapper disposed\"}";
            }
            if (completed != tcs.Task)
            {
                lock (_cdpStateLock)
                {
                    _pendingCdpTcs = null;
                    _pendingCdpMethod = string.Empty;
                }
                AppDiagnostics.Mark($"CDP call timeout method={method}");
                return $"{{\"error\":\"CDP call timeout (10s): {method}\"}}";
            }

            AppDiagnostics.Mark($"CDP call completed method={method}");
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Exception($"CDP call failed method={method}", ex);
            throw;
        }
        finally
        {
            AppDiagnostics.Mark($"CDP gate release method={method}");
            _cdpCallGate.Release();
        }
    }

    private void EnsureCdpCompletedHandler()
    {
        if (_cdpCompletedHandlerPtr != IntPtr.Zero)
            return;

        _cdpCompletedHandler = new CdpCompletedEventHandler(OnCdpCompleted);
        unsafe
        {
            _cdpCompletedHandlerPtr = (nint)ComInterfaceMarshaller<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>.ConvertToUnmanaged(_cdpCompletedHandler);
        }
    }

    private int OnCdpCompleted(int errorCode, nint returnObjectAsJson)
    {
        TaskCompletionSource<string>? tcs;
        string method;
        lock (_cdpStateLock)
        {
            tcs = _pendingCdpTcs;
            method = _pendingCdpMethod;
            _pendingCdpTcs = null;
            _pendingCdpMethod = string.Empty;
        }

        if (tcs == null)
            return 0;

        try
        {
            if (errorCode != 0)
            {
                tcs.TrySetResult($"{{\"error\":\"CDP call failed: 0x{errorCode:X8}\"}}");
            }
            else
            {
                // 在当前实现中，returnObjectAsJson 指针释放由 WebView2 内部管理；此处仅做只读拷贝。
                var json = ComHelper.ReadWideStringNoFree(returnObjectAsJson) ?? "{}";
                tcs.TrySetResult(json);
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetResult($"{{\"error\":\"CDP callback error ({method}): {ex.Message}\"}}");
        }

        return 0;
    }

    private (long requestId, Task<nint> completionTask)? BeginGetContentRequest()
    {
        var tcs = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = Interlocked.Increment(ref _getContentRequestId);

        ICoreWebView2WebResourceResponseViewGetContentCompletedHandler? callback = null;
        callback = new GetContentCompletedEventHandler((errorCode, contentStream) =>
        {
            CompleteGetContentRequest(requestId, errorCode == 0 ? contentStream : IntPtr.Zero);
            return 0;
        });

        nint handlerPtr;
        unsafe
        {
            handlerPtr = (nint)ComInterfaceMarshaller<ICoreWebView2WebResourceResponseViewGetContentCompletedHandler>.ConvertToUnmanaged(callback);
        }

        lock (_getContentStateLock)
        {
            if (_disposed)
            {
                unsafe
                {
                    ComInterfaceMarshaller<ICoreWebView2WebResourceResponseViewGetContentCompletedHandler>.Free((void*)handlerPtr);
                }
                return null;
            }

            _pendingGetContentRequests[requestId] = new PendingGetContentRequest
            {
                HandlerPtr = handlerPtr,
                Handler = callback,
                CompletionSource = tcs
            };
        }

        return (requestId, tcs.Task);
    }

    private nint GetGetContentHandlerPtr(long requestId)
    {
        lock (_getContentStateLock)
        {
            if (_pendingGetContentRequests.TryGetValue(requestId, out var request))
                return request.HandlerPtr;
        }

        return IntPtr.Zero;
    }

    private void CompleteGetContentRequest(long requestId, nint streamPtr)
    {
        PendingGetContentRequest? request = null;
        lock (_getContentStateLock)
        {
            if (_pendingGetContentRequests.TryGetValue(requestId, out request))
                _pendingGetContentRequests.Remove(requestId);
        }

        if (request == null)
            return;

        request.CompletionSource.TrySetResult(streamPtr);
        unsafe
        {
            ComInterfaceMarshaller<ICoreWebView2WebResourceResponseViewGetContentCompletedHandler>.Free((void*)request.HandlerPtr);
        }
    }

    private int OnDocumentTitleChanged(nint @this, nint sender, nint args)
    {
        try
        {
            var title = GetDocumentTitle();
            var source = GetSource();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrWhiteSpace(source))
                    _tabVm?.SyncAddress(source);
                _tabVm?.ApplyDocumentTitle(title);
            });
        }
        catch { }
        return 0;
    }

    private int OnFaviconChanged(nint @this, nint sender, nint args)
    {
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(RequestFaviconUpdate);
        }
        catch { }
        return 0;
    }

    /// <summary>拉取当前页 favicon（含 JS 动态更新后的图标）。</summary>
    public void RefreshFavicon() => RequestFaviconUpdate();

    private void RequestFaviconUpdate()
    {
        if (_disposed || _core15Ptr == IntPtr.Zero)
            return;

        var previous = _faviconDebounceCts;
        var cts = new CancellationTokenSource();
        _faviconDebounceCts = cts;
        var token = cts.Token;
        previous?.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || _disposed)
                    return;

                Avalonia.Threading.Dispatcher.UIThread.Post(DoGetFavicon, Avalonia.Threading.DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void DoGetFavicon()
    {
        if (_disposed || _core15Ptr == IntPtr.Zero)
            return;

        var generation = Interlocked.Increment(ref _faviconFetchGeneration);
        nint handlerPtr = IntPtr.Zero;
        GetFaviconCompletedEventHandler? handler = null;
        handler = new GetFaviconCompletedEventHandler((errorCode, streamPtr) =>
        {
            try
            {
                OnGetFaviconCompleted(generation, errorCode, streamPtr);
            }
            finally
            {
                if (handlerPtr != IntPtr.Zero)
                {
                    unsafe
                    {
                        ComInterfaceMarshaller<ICoreWebView2GetFaviconCompletedHandler>.Free((void*)handlerPtr);
                    }
                }
            }

            return 0;
        });

        unsafe
        {
            handlerPtr = (nint)ComInterfaceMarshaller<ICoreWebView2GetFaviconCompletedHandler>.ConvertToUnmanaged(handler!);
        }

        var hr = ICoreWebView2_15VTable.GetFavicon(_core15Ptr, 0, handlerPtr);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] GetFavicon failed: 0x{hr:X8}");
            unsafe
            {
                ComInterfaceMarshaller<ICoreWebView2GetFaviconCompletedHandler>.Free((void*)handlerPtr);
            }
        }
    }

    private void OnGetFaviconCompleted(int generation, int errorCode, nint streamPtr)
    {
        if (_disposed || generation != Volatile.Read(ref _faviconFetchGeneration))
        {
            if (streamPtr != IntPtr.Zero)
                ComHelper.Release(streamPtr);
            return;
        }

        if (errorCode != 0 || streamPtr == IntPtr.Zero)
        {
            // 仅当前代请求失败时清图标，避免被后续并发请求误清。
            if (generation == Volatile.Read(ref _faviconFetchGeneration))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _tabVm?.ClearFavicon());
            if (streamPtr != IntPtr.Zero)
                ComHelper.Release(streamPtr);
            return;
        }

        try
        {
            var bytes = ReadFaviconStreamBytes(streamPtr);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || generation != Volatile.Read(ref _faviconFetchGeneration))
                    return;
                if (bytes.Length == 0)
                    _tabVm?.ClearFavicon();
                else
                    _tabVm?.ApplyFavicon(bytes);
            });
        }
        catch
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _tabVm?.ClearFavicon());
        }
        finally
        {
            ComHelper.Release(streamPtr);
        }
    }

    private int OnWebResourceRequested(nint @this, nint sender, nint argsPtr)
    {
        try
        {
            ProcessWebResourceRequested(argsPtr);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] OnWebResourceRequested error: {ex}");
        }
        return 0;
    }

    private void ProcessWebResourceRequested(nint argsPtr)
    {
        var hr = ICoreWebView2WebResourceRequestedEventArgsVTable.GetRequest(argsPtr, out var reqPtr);
        if (hr != 0 || reqPtr == IntPtr.Zero) return;

        var resourceContext = WebTrafficResourceContext.Unknown;
        if (ICoreWebView2WebResourceRequestedEventArgsVTable.GetResourceContext(argsPtr, out var rawContext) == 0)
            resourceContext = WebTrafficResourceContextExtensions.FromWebView2(rawContext);

        ICoreWebView2WebResourceRequestVTable.GetUri(reqPtr, out var uriPtr);
        ICoreWebView2WebResourceRequestVTable.GetMethod(reqPtr, out var methodPtr);

        var uri = ComHelper.ReadCoTaskMemString(uriPtr) ?? string.Empty;
        var method = ComHelper.ReadCoTaskMemString(methodPtr) ?? "GET";

        ComHelper.Release(reqPtr);
        RecordRequestStart(method, uri, resourceContext);
    }

    private int OnResponseReceived(nint @this, nint sender, nint argsPtr)
    {
        try
        {
            ProcessResponseReceived(argsPtr);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2NativeWrapper] OnResponseReceived error: {ex}");
        }
        return 0;
    }

    private void ProcessResponseReceived(nint argsPtr)
    {
        var hr = ICoreWebView2WebResourceResponseReceivedEventArgsVTable.GetRequest(argsPtr, out var reqPtr);
        if (hr != 0 || reqPtr == IntPtr.Zero) return;

        hr = ICoreWebView2WebResourceResponseReceivedEventArgsVTable.GetResponse(argsPtr, out var respPtr);
        if (hr != 0 || respPtr == IntPtr.Zero) { ComHelper.Release(reqPtr); return; }

        ICoreWebView2WebResourceRequestVTable.GetUri(reqPtr, out var uriPtr);
        ICoreWebView2WebResourceRequestVTable.GetMethod(reqPtr, out var methodPtr);
        ICoreWebView2WebResourceRequestVTable.GetContent(reqPtr, out var reqBodyStreamPtr);
        ICoreWebView2WebResourceRequestVTable.GetHeaders(reqPtr, out var reqHeadersPtr);

        ICoreWebView2WebResourceResponseViewVTable.GetStatusCode(respPtr, out var statusCode);
        ICoreWebView2WebResourceResponseViewVTable.GetHeaders(respPtr, out var respHeadersPtr);

        var uri = ComHelper.ReadCoTaskMemString(uriPtr) ?? string.Empty;
        var method = ComHelper.ReadCoTaskMemString(methodPtr) ?? "UNKNOWN";

        var reqHeaders = HeaderReader.ReadRequestHeadersStructured(reqHeadersPtr);
        var respHeaders = HeaderReader.ReadResponseHeadersStructured(respHeadersPtr);
        var reqBodyResult = TryReadRequestBody(reqBodyStreamPtr);
        var latencyMs = TryTakeRequestLatencyMs(method, uri);
        var resourceContext = TryTakeRequestResourceContext(method, uri);

        if (reqHeadersPtr != IntPtr.Zero) ComHelper.Release(reqHeadersPtr);
        if (reqBodyStreamPtr != IntPtr.Zero) ComHelper.Release(reqBodyStreamPtr);
        if (respHeadersPtr != IntPtr.Zero) ComHelper.Release(respHeadersPtr);
        ComHelper.Release(reqPtr);

        var entryId = Guid.NewGuid().ToString("N");
        var capture = TrafficCaptureRecord.FromBrowser(
            entryId,
            DateTime.Now.ToString("HH:mm:ss.fff"),
            string.IsNullOrWhiteSpace(_tabVm?.Title) ? "浏览页" : _tabVm!.Title,
            _tabVm?.TabId ?? string.Empty,
            _tabVm?.PageSessionId ?? 0,
            string.IsNullOrWhiteSpace(_tabVm?.TopLevelUrl) ? (_tabVm?.Address ?? string.Empty) : _tabVm!.TopLevelUrl,
            method,
            uri,
            statusCode,
            latencyMs,
            reqHeaders,
            respHeaders,
            reqBodyResult.Text,
            reqBodyResult.Raw,
            (TrafficResourceContext)(int)resourceContext);

        var tabVm = _tabVm;
        if (tabVm is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => tabVm.SubmitCapture(capture));
        }

        TryReadResponseBodyAsync(respPtr, entryId, reqBodyResult.Text, reqBodyResult.Raw);
    }

    private void PublishTrafficBodyUpdate(
        string entryId,
        string requestBody,
        byte[]? requestBodyRaw,
        string responseBody,
        byte[]? responseBodyRaw)
    {
        var tabVm = _tabVm;
        if (tabVm is null)
            return;

        _ = Task.Run(() => tabVm.UpdateTrafficBody(
            entryId,
            requestBody,
            responseBody,
            requestBodyRaw,
            responseBodyRaw));
    }

    private static string BuildRequestTimingKey(string method, string uri) => $"{method}\0{uri}";

    private void RecordRequestStart(string method, string uri, WebTrafficResourceContext resourceContext)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return;

        var key = BuildRequestTimingKey(method, uri);
        lock (_requestTimingLock)
        {
            if (!_requestStartTicks.TryGetValue(key, out var queue))
            {
                queue = new Queue<long>();
                _requestStartTicks[key] = queue;
            }

            queue.Enqueue(Stopwatch.GetTimestamp());

            if (!_requestResourceContexts.TryGetValue(key, out var contextQueue))
            {
                contextQueue = new Queue<WebTrafficResourceContext>();
                _requestResourceContexts[key] = contextQueue;
            }

            contextQueue.Enqueue(resourceContext);
        }
    }

    private long? TryTakeRequestLatencyMs(string method, string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var key = BuildRequestTimingKey(method, uri);
        lock (_requestTimingLock)
        {
            if (!_requestStartTicks.TryGetValue(key, out var queue) || queue.Count == 0)
                return null;

            var startTicks = queue.Dequeue();
            if (queue.Count == 0)
                _requestStartTicks.Remove(key);

            return (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        }
    }

    private WebTrafficResourceContext TryTakeRequestResourceContext(string method, string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return WebTrafficResourceContext.Unknown;

        var key = BuildRequestTimingKey(method, uri);
        lock (_requestTimingLock)
        {
            if (!_requestResourceContexts.TryGetValue(key, out var queue) || queue.Count == 0)
                return WebTrafficResourceContext.Unknown;

            var context = queue.Dequeue();
            if (queue.Count == 0)
                _requestResourceContexts.Remove(key);

            return context;
        }
    }

    private async void TryReadResponseBodyAsync(
        nint respPtr,
        string entryId,
        string requestBody,
        byte[]? requestBodyRaw)
    {
        try
        {
            ComHelper.AddRef(respPtr);

            var request = BeginGetContentRequest();
            if (request is null)
            {
                PublishTrafficBodyUpdate(entryId, requestBody, requestBodyRaw, string.Empty, null);
                return;
            }

            var requestId = request.Value.requestId;
            var completionTask = request.Value.completionTask;
            var handlerPtr = GetGetContentHandlerPtr(requestId);
            if (handlerPtr == IntPtr.Zero)
            {
                PublishTrafficBodyUpdate(entryId, requestBody, requestBodyRaw, string.Empty, null);
                return;
            }

            var hr = ICoreWebView2WebResourceResponseViewVTable.GetContent(respPtr, handlerPtr);
            if (hr != 0)
            {
                CompleteGetContentRequest(requestId, IntPtr.Zero);
                PublishTrafficBodyUpdate(entryId, requestBody, requestBodyRaw, string.Empty, null);
                return;
            }

            Task completed;
            try
            {
                completed = await Task.WhenAny(completionTask, Task.Delay(8000, _disposeCts.Token));
            }
            catch (OperationCanceledException)
            {
                PublishTrafficBodyUpdate(entryId, requestBody, requestBodyRaw, string.Empty, null);
                return;
            }

            if (completed != completionTask)
            {
                PublishTrafficBodyUpdate(entryId, requestBody, requestBodyRaw, string.Empty, null);
                return;
            }

            var streamPtr = await completionTask;

            if (streamPtr == IntPtr.Zero)
            {
                PublishTrafficBodyUpdate(entryId, requestBody, requestBodyRaw, string.Empty, null);
                return;
            }

            var raw = ReadIStreamBytes(streamPtr);
            var encoded = TrafficBodyCodec.EncodeBody(raw);
            PublishTrafficBodyUpdate(entryId, requestBody, requestBodyRaw, encoded.Text, encoded.Raw);

            ComHelper.Release(streamPtr);
        }
        catch
        {
            PublishTrafficBodyUpdate(entryId, requestBody, requestBodyRaw, string.Empty, null);
        }
        finally
        {
            ComHelper.Release(respPtr);
        }
    }

    private static byte[] ReadIStreamBytes(nint streamPtr)
    {
        IStreamVTable.Seek(streamPtr, 0, 0, out _);
        return TrafficBodyCodec.ReadStreamBytes((buffer, count) =>
        {
            _ = IStreamVTable.Read(streamPtr, buffer, count, out var readCount);
            return readCount > 0 ? readCount : 0u;
        });
    }

    private const int MaxFaviconBytes = 256 * 1024;

    private static byte[] ReadFaviconStreamBytes(nint streamPtr)
    {
        IStreamVTable.Seek(streamPtr, 0, 0, out _);
        return TrafficBodyCodec.ReadStreamBytes((buffer, count) =>
        {
            _ = IStreamVTable.Read(streamPtr, buffer, count, out var readCount);
            return readCount > 0 ? readCount : 0u;
        }, MaxFaviconBytes);
    }

    private static (string Text, byte[]? Raw) TryReadRequestBody(nint contentStreamPtr)
    {
        if (contentStreamPtr == IntPtr.Zero)
            return (string.Empty, null);

        try
        {
            var raw = ReadIStreamBytes(contentStreamPtr);
            if (raw.Length == 0)
                return (string.Empty, null);

            var encoded = TrafficBodyCodec.EncodeBody(raw);
            return (encoded.Text, encoded.Raw);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();
        _faviconDebounceCts?.Cancel();
        _faviconDebounceCts?.Dispose();
        _faviconDebounceCts = null;

        try
        {
            if (_corePtr != IntPtr.Zero && _documentTitleChangedCookie != 0)
            {
                try { ICoreWebView2VTable.RemoveDocumentTitleChanged(_corePtr, _documentTitleChangedCookie); }
                catch { }
            }

            if (_corePtr != IntPtr.Zero && _webResourceRequestedCookie != 0)
            {
                try { ICoreWebView2VTable.RemoveWebResourceRequested(_corePtr, _webResourceRequestedCookie); }
                catch { }
            }

            if (_core2Ptr != IntPtr.Zero && _responseReceivedCookie != 0)
            {
                try { ICoreWebView2_2VTable.RemoveWebResourceResponseReceived(_core2Ptr, _responseReceivedCookie); }
                catch { }
            }

            if (_core14Ptr != IntPtr.Zero && _serverCertCookie != 0)
            {
                try { ICoreWebView2_14VTable.RemoveServerCertificateErrorDetected(_core14Ptr, _serverCertCookie); }
                catch { }
            }

            if (_core15Ptr != IntPtr.Zero && _faviconChangedCookie != 0)
            {
                try { ICoreWebView2_15VTable.RemoveFaviconChanged(_core15Ptr, _faviconChangedCookie); }
                catch { }
            }
        }
        catch { }

        if (_faviconChangedHandlerPtr != IntPtr.Zero)
        {
            unsafe
            {
                ComInterfaceMarshaller<ICoreWebView2FaviconChangedEventHandler>.Free((void*)_faviconChangedHandlerPtr);
            }
            _faviconChangedHandlerPtr = IntPtr.Zero;
        }
        _faviconChangedHandler = null;

        if (_titleChangedHandlerPtr != IntPtr.Zero)
        {
            unsafe
            {
                ComInterfaceMarshaller<ICoreWebView2DocumentTitleChangedEventHandler>.Free((void*)_titleChangedHandlerPtr);
            }
            _titleChangedHandlerPtr = IntPtr.Zero;
        }
        if (_webResourceRequestedHandlerPtr != IntPtr.Zero)
        {
            unsafe
            {
                ComInterfaceMarshaller<ICoreWebView2WebResourceRequestedEventHandler>.Free((void*)_webResourceRequestedHandlerPtr);
            }
            _webResourceRequestedHandlerPtr = IntPtr.Zero;
        }
        if (_responseReceivedHandlerPtr != IntPtr.Zero)
        {
            unsafe
            {
                ComInterfaceMarshaller<ICoreWebView2WebResourceResponseReceivedEventHandler>.Free((void*)_responseReceivedHandlerPtr);
            }
            _responseReceivedHandlerPtr = IntPtr.Zero;
        }
        if (_serverCertHandlerPtr != IntPtr.Zero)
        {
            unsafe
            {
                ComInterfaceMarshaller<ICoreWebView2ServerCertificateErrorDetectedEventHandler>.Free((void*)_serverCertHandlerPtr);
            }
            _serverCertHandlerPtr = IntPtr.Zero;
        }
        if (_cdpCompletedHandlerPtr != IntPtr.Zero)
        {
            unsafe
            {
                ComInterfaceMarshaller<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>.Free((void*)_cdpCompletedHandlerPtr);
            }
            _cdpCompletedHandlerPtr = IntPtr.Zero;
        }
        _cdpCompletedHandler = null;
        lock (_cdpStateLock)
        {
            _pendingCdpTcs?.TrySetResult("{\"error\":\"CDP call cancelled: wrapper disposed\"}");
            _pendingCdpTcs = null;
            _pendingCdpMethod = string.Empty;
        }
        lock (_getContentStateLock)
        {
            foreach (var pending in _pendingGetContentRequests.Values)
            {
                pending.CompletionSource.TrySetResult(IntPtr.Zero);
                unsafe
                {
                    ComInterfaceMarshaller<ICoreWebView2WebResourceResponseViewGetContentCompletedHandler>.Free((void*)pending.HandlerPtr);
                }
            }
            _pendingGetContentRequests.Clear();
        }
        _disposeCts.Dispose();
        lock (_requestTimingLock)
            _requestStartTicks.Clear();

        _cdpCallGate.Dispose();

        _titleChangedHandler = null;
        _responseReceivedHandler = null;
        _webResourceRequestedHandler = null;
        _serverCertHandler = null;
        _faviconChangedHandler = null;

        if (_core15Ptr != IntPtr.Zero) ComHelper.Release(_core15Ptr);
        if (_core14Ptr != IntPtr.Zero) ComHelper.Release(_core14Ptr);
        if (_core2Ptr != IntPtr.Zero) ComHelper.Release(_core2Ptr);
        if (_corePtr != IntPtr.Zero) ComHelper.Release(_corePtr);
        if (_unknownPtr != IntPtr.Zero) ComHelper.Release(_unknownPtr);

        _tabVm = null;
    }
}

internal static unsafe class IStreamVTable
{
    public static int Read(nint stream, byte[] pv, uint cb, out uint pcbRead)
    {
        var vtable = *(nint*)stream;
        var fn = Marshal.GetDelegateForFunctionPointer<ReadDelegate>(*(nint*)(vtable + 3 * nint.Size));
        return fn(stream, pv, cb, out pcbRead);
    }

    public static int Seek(nint stream, long move, uint origin, out ulong newPosition)
    {
        var vtable = *(nint*)stream;
        var fn = Marshal.GetDelegateForFunctionPointer<SeekDelegate>(*(nint*)(vtable + 5 * nint.Size));
        return fn(stream, move, origin, out newPosition);
    }

    private delegate int ReadDelegate(nint @this, byte[] pv, uint cb, out uint pcbRead);
    private delegate int SeekDelegate(nint @this, long dlibMove, uint dwOrigin, out ulong plibNewPosition);
}
