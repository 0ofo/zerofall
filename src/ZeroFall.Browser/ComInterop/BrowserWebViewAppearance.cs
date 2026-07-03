using System;
using System.Runtime.InteropServices;

namespace ZeroFall.Browser.ComInterop;

/// <summary>
/// Content 区浏览器 WebView2 外观：白底画布 + Profile 恢复 Auto（不跟随应用暗色强制变暗）。
/// </summary>
internal static unsafe class BrowserWebViewAppearance
{
    private static readonly Guid IidCoreWebView2Controller2 = new("c979903e-d4ca-4228-92eb-47ee3fa96eab");
    private static readonly Guid IidCoreWebView2_13 = new("f75f09a8-667e-4983-88d6-c8773f315e84");

    private const int ControllerPutDefaultBackgroundColorSlot = 27;
    private const int CoreWebView2_13GetProfileSlot = 105;
    private const int ProfilePutPreferredColorSchemeSlot = 9;
    private const int PreferredColorSchemeAuto = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct CoreWebView2Color
    {
        public byte A;
        public byte R;
        public byte G;
        public byte B;
    }

    public static void ApplyFixedLight(nint coreWebView2, nint controller)
    {
        TrySetDefaultBackgroundColor(controller);
        TrySetPreferredColorSchemeAuto(coreWebView2);
    }

    private static void TrySetDefaultBackgroundColor(nint controller)
    {
        if (controller == IntPtr.Zero)
            return;

        var hr = ComHelper.QueryInterface(controller, in IidCoreWebView2Controller2, out var controller2);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BrowserWebViewAppearance] QueryInterface ICoreWebView2Controller2 failed: 0x{hr:X8}");
            return;
        }

        try
        {
            var vtable = *(nint*)controller2;
            var putColor = Marshal.GetDelegateForFunctionPointer<PutDefaultBackgroundColorDelegate>(
                *(nint*)(vtable + ControllerPutDefaultBackgroundColorSlot * nint.Size));

            var white = new CoreWebView2Color { A = 255, R = 255, G = 255, B = 255 };
            hr = putColor(controller2, white);
            if (hr != 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BrowserWebViewAppearance] put_DefaultBackgroundColor failed: 0x{hr:X8}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BrowserWebViewAppearance] DefaultBackgroundColor: {ex.Message}");
        }
        finally
        {
            ComHelper.Release(controller2);
        }
    }

    private static void TrySetPreferredColorSchemeAuto(nint coreWebView2)
    {
        if (coreWebView2 == IntPtr.Zero)
            return;

        var hr = ComHelper.QueryInterface(coreWebView2, in IidCoreWebView2_13, out var core13);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BrowserWebViewAppearance] QueryInterface ICoreWebView2_13 failed: 0x{hr:X8}");
            return;
        }

        nint profile = IntPtr.Zero;
        try
        {
            var vtable = *(nint*)core13;
            var getProfile = Marshal.GetDelegateForFunctionPointer<GetProfileDelegate>(
                *(nint*)(vtable + CoreWebView2_13GetProfileSlot * nint.Size));

            hr = getProfile(core13, out profile);
            if (hr != 0 || profile == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BrowserWebViewAppearance] get_Profile failed: 0x{hr:X8}");
                return;
            }

            var profileVtable = *(nint*)profile;
            var putScheme = Marshal.GetDelegateForFunctionPointer<PutPreferredColorSchemeDelegate>(
                *(nint*)(profileVtable + ProfilePutPreferredColorSchemeSlot * nint.Size));

            hr = putScheme(profile, PreferredColorSchemeAuto);
            if (hr != 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BrowserWebViewAppearance] put_PreferredColorScheme(Auto) failed: 0x{hr:X8}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BrowserWebViewAppearance] PreferredColorScheme: {ex.Message}");
        }
        finally
        {
            if (profile != IntPtr.Zero)
                ComHelper.Release(profile);
            ComHelper.Release(core13);
        }
    }

    private delegate int PutDefaultBackgroundColorDelegate(nint self, CoreWebView2Color value);
    private delegate int GetProfileDelegate(nint self, out nint profile);
    private delegate int PutPreferredColorSchemeDelegate(nint self, int value);
}
