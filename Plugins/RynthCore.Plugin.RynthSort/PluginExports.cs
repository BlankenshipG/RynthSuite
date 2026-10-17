using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthSort;

/// <summary>
/// C ABI exports. Nothing may throw out of an [UnmanagedCallersOnly] method (it would take
/// acclient.exe down): every export goes through RynthPluginRuntime, which catches and logs.
/// RynthSort draws through the engine's script windows (host calls from its own tick), so it
/// exports no render callback and keeps no buffer the engine reads later.
/// </summary>
public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<RynthSortPlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown() => Runtime.Shutdown();

    // UTF-8 literals live in the module's read-only data: nothing to allocate or free.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => LiteralPointer("RynthSort"u8);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => LiteralPointer("0.3.2"u8);

    private static IntPtr LiteralPointer(ReadOnlySpan<byte> literal)
    {
        fixed (byte* p = literal)
            return (IntPtr)p;
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    // A vendor window (for engines without vendor info, API v67, these are the only signal).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnVendorOpen", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnVendorOpen(uint vendorId) => Runtime.OnVendorOpen(vendorId);

    // The engine's RynthAi dashboard toggle: polls the auto-sort state (-1 not logged in,
    // 0 off, 1 on; twice a second, any thread) and sends "on quiet" / "off quiet" (UTF-8,
    // without "/rsort"; queued, runs on the tick).
    [UnmanagedCallersOnly(EntryPoint = "RynthSortAutoState", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int AutoState()
    {
        try { return Runtime.Plugin?.AutoState ?? -1; } catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthSortCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Command(IntPtr utf8)
    {
        try { if (utf8 != IntPtr.Zero) Runtime.Plugin?.EnqueueCommand(Marshal.PtrToStringUTF8(utf8)); } catch { }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnVendorClose", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnVendorClose(uint vendorId) => Runtime.OnVendorClose(vendorId);
}
