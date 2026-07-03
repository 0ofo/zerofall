using System;
using System.Runtime.InteropServices;
using System.Text;
using ZeroFall.Traffic;

namespace ZeroFall.Browser.ComInterop;

internal static unsafe class ComHelper
{
    public static int QueryInterface(nint comObj, in Guid iid, out nint ppv)
    {
        var vtable = *(nint*)comObj;
        var qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(*(nint*)(vtable));
        return qi(comObj, in iid, out ppv);
    }

    public static uint AddRef(nint comObj)
    {
        var vtable = *(nint*)comObj;
        var addRef = Marshal.GetDelegateForFunctionPointer<AddRefDelegate>(*(nint*)(vtable + nint.Size));
        return addRef(comObj);
    }

    public static uint Release(nint comObj)
    {
        var vtable = *(nint*)comObj;
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(*(nint*)(vtable + 2 * nint.Size));
        return release(comObj);
    }

    public static string? ReadCoTaskMemString(nint ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        var s = Marshal.PtrToStringUni(ptr);
        Marshal.FreeCoTaskMem(ptr);
        return s;
    }

    public static string? ReadWideStringNoFree(nint ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        return Marshal.PtrToStringUni(ptr);
    }

    public static nint AllocCoTaskMemString(string? s)
    {
        if (s is null) return IntPtr.Zero;
        return Marshal.StringToCoTaskMemUni(s);
    }

    private delegate int QueryInterfaceDelegate(nint @this, in Guid iid, out nint ppv);
    private delegate uint AddRefDelegate(nint @this);
    private delegate uint ReleaseDelegate(nint @this);
}

internal static unsafe class ICoreWebView2VTable
{
    public static readonly Guid IID = new("76ECEACB-0462-4D94-AC83-423A6793775E");

    public static int GetSettings(nint obj, out nint settingsPtr)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 3 * nint.Size));
        return fn(obj, out settingsPtr);
    }

    public static int GetSource(nint obj, out nint uriPtr)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 4 * nint.Size));
        return fn(obj, out uriPtr);
    }

    public static int Navigate(nint obj, string uri)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<NavigateDelegate>(*(nint*)(vtable + 5 * nint.Size));
        var uriPtr = ComHelper.AllocCoTaskMemString(uri);
        try { return fn(obj, uriPtr); }
        finally { Marshal.FreeCoTaskMem(uriPtr); }
    }

    public static int CallDevToolsProtocolMethod(nint obj, string method, string paramsJson, nint handler)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<CallDtpmDelegate>(*(nint*)(vtable + 36 * nint.Size));
        var methodPtr = ComHelper.AllocCoTaskMemString(method);
        var paramsPtr = ComHelper.AllocCoTaskMemString(paramsJson);
        try { return fn(obj, methodPtr, paramsPtr, handler); }
        finally { Marshal.FreeCoTaskMem(methodPtr); Marshal.FreeCoTaskMem(paramsPtr); }
    }

    public static int ExecuteScript(nint obj, string script, nint handler)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<ExecuteScriptDelegate>(*(nint*)(vtable + 29 * nint.Size));
        var scriptPtr = ComHelper.AllocCoTaskMemString(script);
        try { return fn(obj, scriptPtr, handler); }
        finally { Marshal.FreeCoTaskMem(scriptPtr); }
    }

    public static int AddDocumentTitleChanged(nint obj, nint handler, out long token)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<AddEventDelegate>(*(nint*)(vtable + 46 * nint.Size));
        return fn(obj, handler, out token);
    }

    public static int RemoveDocumentTitleChanged(nint obj, long token)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<RemoveEventDelegate>(*(nint*)(vtable + 47 * nint.Size));
        return fn(obj, token);
    }

    public static int GetDocumentTitle(nint obj, out nint titlePtr)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 48 * nint.Size));
        return fn(obj, out titlePtr);
    }

    /// <summary>
    /// 打开 DevTools 窗口。ICoreWebView2::OpenDevToolsWindow（vtable 槽位 51）。
    /// 若已打开则不做任何事。无返回值、无回调，AOT 安全。
    /// </summary>
    public static int OpenDevToolsWindow(nint obj)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<OpenDevToolsDelegate>(*(nint*)(vtable + 51 * nint.Size));
        return fn(obj);
    }

    public static int AddWebResourceRequested(nint obj, nint handler, out long token)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<AddEventDelegate>(*(nint*)(vtable + 55 * nint.Size));
        return fn(obj, handler, out token);
    }

    public static int RemoveWebResourceRequested(nint obj, long token)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<RemoveEventDelegate>(*(nint*)(vtable + 56 * nint.Size));
        return fn(obj, token);
    }

    public static int AddWebResourceRequestedFilter(nint obj, string uri, int resourceContext)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<AddFilterDelegate>(*(nint*)(vtable + 57 * nint.Size));
        var uriPtr = ComHelper.AllocCoTaskMemString(uri);
        try { return fn(obj, uriPtr, resourceContext); }
        finally { Marshal.FreeCoTaskMem(uriPtr); }
    }

    public static int RemoveWebResourceRequestedFilter(nint obj, string uri, int resourceContext)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<AddFilterDelegate>(*(nint*)(vtable + 58 * nint.Size));
        var uriPtr = ComHelper.AllocCoTaskMemString(uri);
        try { return fn(obj, uriPtr, resourceContext); }
        finally { Marshal.FreeCoTaskMem(uriPtr); }
    }

    private delegate int NavigateDelegate(nint @this, nint uri);
    private delegate int CallDtpmDelegate(nint @this, nint method, nint paramsJson, nint handler);
    private delegate int ExecuteScriptDelegate(nint @this, nint javaScript, nint handler);
    private delegate int GetPtrDelegate(nint @this, out nint ptr);
    private delegate int AddEventDelegate(nint @this, nint handler, out long token);
    private delegate int RemoveEventDelegate(nint @this, long token);
    private delegate int AddFilterDelegate(nint @this, nint uri, int resourceContext);
    private delegate int OpenDevToolsDelegate(nint @this);
}

internal static unsafe class ICoreWebView2Settings8VTable
{
    public static readonly Guid IID = new("9e6b0e8f-86ad-4e81-8147-a9b5edb68650");

    public static int PutIsReputationCheckingRequired(nint obj, int value)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<PutIntDelegate>(*(nint*)(vtable + 32 * nint.Size));
        return fn(obj, value);
    }

    private delegate int PutIntDelegate(nint @this, int value);
}

internal static unsafe class ICoreWebView2_14VTable
{
    /// <summary>IID_ICoreWebView2_14 — 用于证书错误回调注册。</summary>
    public static readonly Guid IID = new("6daa4f10-4a90-4753-8898-77c5df534165");

    /// <summary>ICoreWebView2_14Vtbl::add_ServerCertificateErrorDetected（从 QueryInterface 得到的 I14 指针上调用）。</summary>
    public static int AddServerCertificateErrorDetected(nint obj, nint handler, out long token)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<AddEventDelegate>(*(nint*)(vtable + 106 * nint.Size));
        return fn(obj, handler, out token);
    }

    public static int RemoveServerCertificateErrorDetected(nint obj, long token)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<RemoveEventDelegate>(*(nint*)(vtable + 107 * nint.Size));
        return fn(obj, token);
    }

    private delegate int AddEventDelegate(nint @this, nint handler, out long token);
    private delegate int RemoveEventDelegate(nint @this, long token);
}

internal static unsafe class ICoreWebView2_15VTable
{
    public static readonly Guid IID = new("517B2D1D-7DAE-4A66-A4F4-10352FFB9518");

    /// <summary>
    /// ICoreWebView2_14 在 106–108 为证书错误三方法；_15 的 favicon 从 109 起（见 WebView2.h ICoreWebView2_15Vtbl）。
    /// </summary>
    private const int AddFaviconChangedIndex = 109;
    private const int RemoveFaviconChangedIndex = 110;
    private const int GetFaviconIndex = 112;

    public static int AddFaviconChanged(nint obj, nint handler, out long token)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<AddEventDelegate>(*(nint*)(vtable + AddFaviconChangedIndex * nint.Size));
        return fn(obj, handler, out token);
    }

    public static int RemoveFaviconChanged(nint obj, long token)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<RemoveEventDelegate>(*(nint*)(vtable + RemoveFaviconChangedIndex * nint.Size));
        return fn(obj, token);
    }

    /// <param name="format">COREWEBVIEW2_FAVICON_IMAGE_FORMAT_PNG = 0</param>
    public static int GetFavicon(nint obj, int format, nint handler)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<GetFaviconDelegate>(*(nint*)(vtable + GetFaviconIndex * nint.Size));
        return fn(obj, format, handler);
    }

    private delegate int AddEventDelegate(nint @this, nint handler, out long token);
    private delegate int RemoveEventDelegate(nint @this, long token);
    private delegate int GetFaviconDelegate(nint @this, int format, nint handler);
}

internal static unsafe class ICoreWebView2ServerCertificateErrorDetectedEventArgsVTable
{
    /// <summary>COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_ALWAYS_ALLOW = 0</summary>
    public static int PutAction(nint obj, int action)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<PutIntDelegate>(*(nint*)(vtable + 7 * nint.Size));
        return fn(obj, action);
    }

    private delegate int PutIntDelegate(nint @this, int value);
}

internal static unsafe class ICoreWebView2_2VTable
{
    public static readonly Guid IID = new("9E8F0CF8-E670-4B5E-B2BC-73E061E3184C");

    public static int AddWebResourceResponseReceived(nint obj, nint handler, out long token)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<AddEventDelegate>(*(nint*)(vtable + 61 * nint.Size));
        return fn(obj, handler, out token);
    }

    public static int RemoveWebResourceResponseReceived(nint obj, long token)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<RemoveEventDelegate>(*(nint*)(vtable + 62 * nint.Size));
        return fn(obj, token);
    }

    private delegate int AddEventDelegate(nint @this, nint handler, out long token);
    private delegate int RemoveEventDelegate(nint @this, long token);
}

internal static unsafe class ICoreWebView2WebResourceRequestedEventArgsVTable
{
    public static int GetRequest(nint obj, out nint request)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 3 * nint.Size));
        return fn(obj, out request);
    }

    public static int GetResourceContext(nint obj, out int resourceContext)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<GetIntDelegate>(*(nint*)(vtable + 4 * nint.Size));
        return fn(obj, out resourceContext);
    }

    private delegate int GetPtrDelegate(nint @this, out nint ptr);
    private delegate int GetIntDelegate(nint @this, out int value);
}

internal static unsafe class ICoreWebView2WebResourceResponseReceivedEventArgsVTable
{
    public static int GetRequest(nint obj, out nint request)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 3 * nint.Size));
        return fn(obj, out request);
    }

    public static int GetResponse(nint obj, out nint response)
    {
        var vtable = *(nint*)obj;
        var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 4 * nint.Size));
        return fn(obj, out response);
    }

    private delegate int GetPtrDelegate(nint @this, out nint ptr);
}

internal static unsafe class ICoreWebView2WebResourceRequestVTable
{
    public static int GetUri(nint obj, out nint uri) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 3 * nint.Size)); return fn(obj, out uri); }
    public static int GetMethod(nint obj, out nint method) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 5 * nint.Size)); return fn(obj, out method); }
    public static int GetContent(nint obj, out nint content) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 7 * nint.Size)); return fn(obj, out content); }
    public static int GetHeaders(nint obj, out nint headers) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 9 * nint.Size)); return fn(obj, out headers); }

    private delegate int GetPtrDelegate(nint @this, out nint ptr);
}

internal static unsafe class ICoreWebView2WebResourceResponseViewVTable
{
    public static int GetHeaders(nint obj, out nint headers) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 3 * nint.Size)); return fn(obj, out headers); }
    public static int GetStatusCode(nint obj, out int statusCode) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetIntDelegate>(*(nint*)(vtable + 4 * nint.Size)); return fn(obj, out statusCode); }
    public static int GetReasonPhrase(nint obj, out nint reason) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 5 * nint.Size)); return fn(obj, out reason); }
    public static int GetContent(nint obj, nint handler) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetContentDelegate>(*(nint*)(vtable + 6 * nint.Size)); return fn(obj, handler); }

    private delegate int GetPtrDelegate(nint @this, out nint ptr);
    private delegate int GetIntDelegate(nint @this, out int value);
    private delegate int GetContentDelegate(nint @this, nint handler);
}

internal static unsafe class ICoreWebView2HttpRequestHeadersVTable
{
    public static int GetIterator(nint obj, out nint iterator) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 8 * nint.Size)); return fn(obj, out iterator); }

    private delegate int GetPtrDelegate(nint @this, out nint ptr);
}

internal static unsafe class ICoreWebView2HttpResponseHeadersVTable
{
    public static int GetIterator(nint obj, out nint iterator) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetPtrDelegate>(*(nint*)(vtable + 7 * nint.Size)); return fn(obj, out iterator); }

    private delegate int GetPtrDelegate(nint @this, out nint ptr);
}

internal static unsafe class ICoreWebView2HttpHeadersCollectionIteratorVTable
{
    public static int GetCurrentHeader(nint obj, out nint name, out nint value) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetCurrentDelegate>(*(nint*)(vtable + 3 * nint.Size)); return fn(obj, out name, out value); }
    public static int GetHasCurrentHeader(nint obj, out int hasCurrent) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetIntDelegate>(*(nint*)(vtable + 4 * nint.Size)); return fn(obj, out hasCurrent); }
    public static int MoveNext(nint obj, out int hasNext) { var vtable = *(nint*)obj; var fn = Marshal.GetDelegateForFunctionPointer<GetIntDelegate>(*(nint*)(vtable + 5 * nint.Size)); return fn(obj, out hasNext); }

    private delegate int GetIntDelegate(nint @this, out int value);
    private delegate int GetCurrentDelegate(nint @this, out nint name, out nint value);
}

internal static unsafe class HeaderReader
{
    public static TrafficHttpHeaders ReadRequestHeadersStructured(nint headersPtr) =>
        ReadStructured(headersPtr, ICoreWebView2HttpRequestHeadersVTable.GetIterator);

    public static TrafficHttpHeaders ReadResponseHeadersStructured(nint headersPtr) =>
        ReadStructured(headersPtr, ICoreWebView2HttpResponseHeadersVTable.GetIterator);

    public static string ReadRequestHeaders(nint headersPtr) =>
        ReadRequestHeadersStructured(headersPtr).ToWireText();

    public static string ReadResponseHeaders(nint headersPtr) =>
        ReadResponseHeadersStructured(headersPtr).ToWireText();

    private static TrafficHttpHeaders ReadStructured(
        nint headersPtr,
        GetIteratorDelegate getIterator)
    {
        var headers = new TrafficHttpHeaders();
        if (headersPtr == IntPtr.Zero)
            return headers;

        try
        {
            var hr = getIterator(headersPtr, out var iterPtr);
            if (hr != 0 || iterPtr == IntPtr.Zero)
                return headers;

            using var iter = new ComReleaser(iterPtr);
            ReadFromIterator(iterPtr, headers);
            return headers;
        }
        catch
        {
            return headers;
        }
    }

    private static void ReadFromIterator(nint iterPtr, TrafficHttpHeaders headers)
    {
        while (true)
        {
            var hr = ICoreWebView2HttpHeadersCollectionIteratorVTable.GetHasCurrentHeader(iterPtr, out var hasCurrent);
            if (hr != 0 || hasCurrent == 0)
                break;

            hr = ICoreWebView2HttpHeadersCollectionIteratorVTable.GetCurrentHeader(iterPtr, out var namePtr, out var valuePtr);
            if (hr != 0)
                break;

            var name = ComHelper.ReadCoTaskMemString(namePtr) ?? string.Empty;
            var value = ComHelper.ReadCoTaskMemString(valuePtr) ?? string.Empty;
            headers.Add(name, value);

            hr = ICoreWebView2HttpHeadersCollectionIteratorVTable.MoveNext(iterPtr, out var hasNext);
            if (hr != 0 || hasNext == 0)
                break;
        }
    }

    private delegate int GetIteratorDelegate(nint headersPtr, out nint iteratorPtr);

    private readonly struct ComReleaser : IDisposable
    {
        private readonly nint _ptr;
        public ComReleaser(nint ptr) => _ptr = ptr;
        public void Dispose() { if (_ptr != IntPtr.Zero) ComHelper.Release(_ptr); }
    }
}
