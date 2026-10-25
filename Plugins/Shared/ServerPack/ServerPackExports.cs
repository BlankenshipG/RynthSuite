// ServerPackExports.cs - The native exports every server plugin has (compiled into each one).
// UnmanagedCallersOnly entry points can't live in a generic or shared assembly, so each server
// plugin compiles this file itself, around its own ServerPackPlugin.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.ServerPack;

public static unsafe class ServerPackExports
{
    private static readonly RynthPluginRuntime<ServerPackPlugin> Runtime = new();

    /// <summary>The live plugin (null before init and after shutdown).</summary>
    internal static ServerPackPlugin? Plugin => Runtime.Plugin;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    /// <summary>
    /// Plugin API v68 plugin-to-plugin interfaces: "RynthServer.Profile" v1 (ServerPackInterface),
    /// the server's settings for any plugin RynthCore's GetServerStateJson points at this one.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginQueryInterface", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr QueryInterface(IntPtr ifaceAnsi, uint version)
    {
        try
        {
            string iface = ifaceAnsi == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ifaceAnsi) ?? "";
            return ServerPackInterface.Query(iface, version);
        }
        catch
        {
            return IntPtr.Zero;   // an exception crossing UnmanagedCallersOnly would fail-fast the client
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown()
    {
        try { ServerPackInterface.Release(); } catch { }
        Runtime.Shutdown();
        IntPtr old = Interlocked.Exchange(ref _sectionsPtr, IntPtr.Zero);
        if (old != IntPtr.Zero) Marshal.FreeCoTaskMem(old);
        _sectionsJson = null;
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => ServerPackPlugin.NamePointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => ServerPackPlugin.VersionPointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    // "/rserver ..." commands.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    /// <summary>The standalone server card ("/rserver"). Render thread.</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRender", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Render()
    {
        try { Runtime.OnRender(); }
        catch { /* an exception crossing UnmanagedCallersOnly would fail-fast the client */ }
    }

    /// <summary>
    /// The engine's plugin-command broker: "servermenu" from RynthCore (2026.10.5.48+). The ANSI
    /// strings are copied before returning (the caller frees them); the payload is only parsed, so
    /// any thread is fine.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginApplyRemoteCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ApplyRemoteCommand(IntPtr actionAnsi, IntPtr valueAnsi)
    {
        try
        {
            string action = actionAnsi != IntPtr.Zero ? (Marshal.PtrToStringAnsi(actionAnsi) ?? string.Empty) : string.Empty;
            if (action.Length == 0 || action.Length > 32) return;
            string value = valueAnsi != IntPtr.Zero ? (Marshal.PtrToStringAnsi(valueAnsi) ?? string.Empty) : string.Empty;
            Runtime.Plugin?.ApplyRemoteCommand(action, value);
        }
        catch { /* never let a managed exception cross the native boundary */ }
    }

    /// <summary>
    /// RynthCore's Mini Remote sections: "section:server-&lt;id&gt;" (":popout" added in a popped-out
    /// window) draws the server card into the engine's current window. 1 = drawn, 0 = not ready or
    /// not ours.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRenderEmbed", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int RenderEmbed(byte* surface, float width, float height, float* wantWidth, float* wantHeight)
    {
        try
        {
            if (surface == null) return 0;
            string name = Marshal.PtrToStringUTF8((IntPtr)surface) ?? string.Empty;
            if (name.EndsWith(":popout", StringComparison.Ordinal)) name = name[..^":popout".Length];
            if (!name.Equals(ServerPackPlugin.SettingsSurface, StringComparison.Ordinal)) return 0;
            var plugin = Runtime.Plugin;
            if (plugin == null || !plugin.RenderEmbeddedCard(out float wantH)) return 0;
            if (wantWidth != null) *wantWidth = width;
            if (wantHeight != null) *wantHeight = wantH;
            return 1;
        }
        catch
        {
            return 0;   // an exception crossing UnmanagedCallersOnly would fail-fast the client
        }
    }

    private static IntPtr _sectionsPtr = IntPtr.Zero;
    private static string? _sectionsJson;

    /// <summary>
    /// RynthCore 2026.10.5.46+: the server and the card's Mini Remote spot (UTF-8 JSON, schema 1).
    /// Null before login. An unchanged manifest keeps the same buffer.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetSectionsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSectionsJson()
    {
        try
        {
            string? json = Runtime.Plugin?.BuildSectionsJson();
            if (json == null) return IntPtr.Zero;
            if (json == _sectionsJson && _sectionsPtr != IntPtr.Zero) return _sectionsPtr;
            _sectionsJson = json;
            IntPtr newPtr = Marshal.StringToCoTaskMemUTF8(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _sectionsPtr, newPtr);
            if (oldPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(oldPtr);
            return newPtr;
        }
        catch { return IntPtr.Zero; }
    }
}
