using System;
using System.Runtime.InteropServices;

namespace ZeroFall.Platform.Services;

/// <summary>Dock 面板内嵌 WebView2（AI 聊天、Markdown 预览等）：禁用原生右键与浏览器快捷键，避免误刷新。</summary>
public static unsafe class WebView2PanelHostSettings
{
    private static readonly Guid IidCoreWebView2Settings = new("35272d8c-7313-453f-a1b8-5a387a75eb84");

    private const int CoreWebView2GetSettingsSlot = 39;
    private const int SettingsPutAreDefaultContextMenusEnabledSlot = 7;

    public static void ApplyLockedDownHost(nint coreWebView2)
    {
        if (coreWebView2 == IntPtr.Zero)
            return;

        nint settings = IntPtr.Zero;
        try
        {
            var coreVtable = *(nint*)coreWebView2;
            var getSettings = Marshal.GetDelegateForFunctionPointer<GetSettingsDelegate>(
                *(nint*)(coreVtable + CoreWebView2GetSettingsSlot * nint.Size));

            var hr = getSettings(coreWebView2, out settings);
            if (hr != 0 || settings == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[WebView2PanelHostSettings] get_Settings failed: 0x{hr:X8}");
                return;
            }

            PutBool(settings, SettingsPutAreDefaultContextMenusEnabledSlot, false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2PanelHostSettings] Apply failed: {ex.Message}");
        }
        finally
        {
            if (settings != IntPtr.Zero)
                Release(settings);
        }
    }

    private static void PutBool(nint settings, int slot, bool value)
    {
        var settingsVtable = *(nint*)settings;
        var put = Marshal.GetDelegateForFunctionPointer<PutBoolDelegate>(
            *(nint*)(settingsVtable + slot * nint.Size));
        var hr = put(settings, value ? 1 : 0);
        if (hr != 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[WebView2PanelHostSettings] put_bool slot={slot} failed: 0x{hr:X8}");
        }
    }

    private static void Release(nint ptr)
    {
        if (ptr == IntPtr.Zero)
            return;

        var vtable = *(nint*)ptr;
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(*(nint*)(vtable + 2 * nint.Size));
        release(ptr);
    }

    private delegate int GetSettingsDelegate(nint self, out nint settings);
    private delegate int PutBoolDelegate(nint self, int value);
    private delegate uint ReleaseDelegate(nint self);
}
