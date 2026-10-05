using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthVision;

/// <summary>
/// C ABI exports. Nothing may throw out of an [UnmanagedCallersOnly] method (it
/// would take acclient.exe down): the lifecycle and event exports go through
/// RynthPluginRuntime, which catches and logs; the rest catch here.
/// </summary>
public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<RynthVisionPlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown()
    {
        Runtime.Shutdown();
        try
        {
            // The engine copies the settings string as soon as it gets it, on
            // the same (pump) thread that shuts us down, so nothing still
            // reads this buffer.
            IntPtr old = Interlocked.Exchange(ref _settingsPtr, IntPtr.Zero);
            if (old != IntPtr.Zero)
                Marshal.FreeHGlobal(old);
        }
        catch { }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    // Name/version are UTF-8 literals: the compiler stores them null-terminated
    // in the module's read-only data, so there is nothing to allocate or free,
    // and they stay valid for as long as the module is loaded (the engine
    // copies them right after loading, before Init).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => LiteralPointer("RynthVision"u8);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => LiteralPointer("0.1.0"u8);

    private static IntPtr LiteralPointer(ReadOnlySpan<byte> literal)
    {
        fixed (byte* p = literal)
            return (IntPtr)p;
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    // ── Settings bridge for the engine's Vision panels (UI/Data/VisionData.cs) ──
    // The engine calls these on the plugin pump thread (UiDataHub). The
    // pointer swap (alloc-new → swap → free-old) keeps the previous buffer
    // alive until the next call; the engine copies it immediately.

    private static IntPtr _settingsPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthVisionGetSettingsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSettingsJson()
    {
        try
        {
            string json = Runtime.Plugin?.BuildSettingsJson() ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _settingsPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Applies any subset of the settings keys (the panels send only the changed field).</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthVisionSetSettings", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetSettings(IntPtr ansiJson)
    {
        try
        {
            if (ansiJson == IntPtr.Zero) return;
            string? json = Marshal.PtrToStringAnsi(ansiJson);
            if (!string.IsNullOrEmpty(json))
                Runtime.Plugin?.ApplySettingsJson(json);
        }
        catch { }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthVisionInspectTerrain", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void InspectTerrain()
    {
        try { Runtime.Plugin?.InspectTerrain(); }
        catch { }
    }
}
