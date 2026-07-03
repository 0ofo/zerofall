using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ZeroFall.Browser.ComInterop;

[GeneratedComInterface]
[Guid("F5F2B923-953E-4042-9F95-F3A118E1AFD4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2DocumentTitleChangedEventHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

[GeneratedComInterface]
[Guid("7DE9898A-24F5-40C3-A2DE-D4F458E69828")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2WebResourceResponseReceivedEventHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

[GeneratedComInterface]
[Guid("5C4889F0-5EF6-4C5A-952C-D8F1B92D0574")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2CallDevToolsProtocolMethodCompletedHandler
{
    [PreserveSig]
    int Invoke(int errorCode, nint returnObjectAsJson);
}

[GeneratedComInterface]
[Guid("49511172-CC67-4BCA-9923-137112F4C4CC")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2ExecuteScriptCompletedHandler
{
    [PreserveSig]
    int Invoke(int errorCode, nint resultObjectAsJson);
}

[GeneratedComInterface]
[Guid("875738E1-9FA2-40E3-8B74-2E8972DD6FE7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2WebResourceResponseViewGetContentCompletedHandler
{
    [PreserveSig]
    int Invoke(int errorCode, nint contentStream);
}

[GeneratedComInterface]
[Guid("969b3a26-d85e-4795-8199-fef57344da22")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2ServerCertificateErrorDetectedEventHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

[GeneratedComClass]
internal sealed partial class DocumentTitleChangedEventHandler : ICoreWebView2DocumentTitleChangedEventHandler
{
    private readonly Func<nint, nint, int> _callback;

    public DocumentTitleChangedEventHandler(Func<nint, nint, int> callback)
    {
        _callback = callback;
    }

    public int Invoke(nint sender, nint args)
    {
        try
        {
            return _callback(sender, args);
        }
        catch
        {
            return 0;
        }
    }
}

[GeneratedComInterface]
[Guid("AB0504FA-F99A-4907-B750-EF54124F509F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2WebResourceRequestedEventHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

[GeneratedComClass]
internal sealed partial class WebResourceRequestedEventHandler : ICoreWebView2WebResourceRequestedEventHandler
{
    private readonly Func<nint, nint, int> _callback;

    public WebResourceRequestedEventHandler(Func<nint, nint, int> callback)
    {
        _callback = callback;
    }

    public int Invoke(nint sender, nint args)
    {
        try
        {
            return _callback(sender, args);
        }
        catch
        {
            return 0;
        }
    }
}

[GeneratedComClass]
internal sealed partial class WebResourceResponseReceivedEventHandler : ICoreWebView2WebResourceResponseReceivedEventHandler
{
    private readonly Func<nint, nint, int> _callback;

    public WebResourceResponseReceivedEventHandler(Func<nint, nint, int> callback)
    {
        _callback = callback;
    }

    public int Invoke(nint sender, nint args)
    {
        try
        {
            return _callback(sender, args);
        }
        catch
        {
            return 0;
        }
    }
}

[GeneratedComClass]
internal sealed partial class CdpCompletedEventHandler : ICoreWebView2CallDevToolsProtocolMethodCompletedHandler
{
    private readonly Func<int, nint, int> _callback;

    public CdpCompletedEventHandler(Func<int, nint, int> callback)
    {
        _callback = callback;
    }

    public int Invoke(int errorCode, nint returnObjectAsJson)
    {
        try
        {
            return _callback(errorCode, returnObjectAsJson);
        }
        catch
        {
            return 0;
        }
    }
}

[GeneratedComClass]
internal sealed partial class ExecuteScriptCompletedEventHandler : ICoreWebView2ExecuteScriptCompletedHandler
{
    private readonly Func<int, nint, int> _callback;

    public ExecuteScriptCompletedEventHandler(Func<int, nint, int> callback)
    {
        _callback = callback;
    }

    public int Invoke(int errorCode, nint resultObjectAsJson)
    {
        try
        {
            return _callback(errorCode, resultObjectAsJson);
        }
        catch
        {
            return 0;
        }
    }
}

[GeneratedComClass]
internal sealed partial class GetContentCompletedEventHandler : ICoreWebView2WebResourceResponseViewGetContentCompletedHandler
{
    private readonly Func<int, nint, int> _callback;

    public GetContentCompletedEventHandler(Func<int, nint, int> callback)
    {
        _callback = callback;
    }

    public int Invoke(int errorCode, nint contentStream)
    {
        try
        {
            return _callback(errorCode, contentStream);
        }
        catch
        {
            return 0;
        }
    }
}

[GeneratedComClass]
internal sealed partial class ServerCertificateErrorDetectedEventHandler : ICoreWebView2ServerCertificateErrorDetectedEventHandler
{
    private readonly Func<nint, nint, int> _callback;

    public ServerCertificateErrorDetectedEventHandler(Func<nint, nint, int> callback)
    {
        _callback = callback;
    }

    public int Invoke(nint sender, nint args)
    {
        try
        {
            return _callback(sender, args);
        }
        catch
        {
            return 0;
        }
    }
}

[GeneratedComInterface]
[Guid("2913da94-833d-4de0-8dca-900fc524a1a4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2FaviconChangedEventHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

[GeneratedComClass]
internal sealed partial class FaviconChangedEventHandler : ICoreWebView2FaviconChangedEventHandler
{
    private readonly Func<nint, nint, int> _callback;

    public FaviconChangedEventHandler(Func<nint, nint, int> callback)
    {
        _callback = callback;
    }

    public int Invoke(nint sender, nint args)
    {
        try
        {
            return _callback(sender, args);
        }
        catch
        {
            return 0;
        }
    }
}

[GeneratedComInterface]
[Guid("a2508329-7da8-49d7-8c05-fa125e4aee8d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2GetFaviconCompletedHandler
{
    [PreserveSig]
    int Invoke(int errorCode, nint stream);
}

[GeneratedComClass]
internal sealed partial class GetFaviconCompletedEventHandler : ICoreWebView2GetFaviconCompletedHandler
{
    private readonly Func<int, nint, int> _callback;

    public GetFaviconCompletedEventHandler(Func<int, nint, int> callback)
    {
        _callback = callback;
    }

    public int Invoke(int errorCode, nint stream)
    {
        try
        {
            return _callback(errorCode, stream);
        }
        catch
        {
            return 0;
        }
    }
}
