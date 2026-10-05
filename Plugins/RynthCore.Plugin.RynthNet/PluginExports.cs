using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthNet;

public static unsafe class PluginExports
{
    internal static readonly RynthPluginRuntime<RynthNetPlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown()
    {
        Runtime.Shutdown();   // stops the transport: joins its thread and tasks
        NetInterface.Release();
        ReleaseSnapshot();
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => RynthNetPlugin.NamePointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => RynthNetPlugin.VersionPointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    // Typed interface for other plugins (host GetPluginInterface, API v68): "RynthNet.Net" v1.
    // Fixed signature: void* (const char* iface, uint version).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginQueryInterface", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr QueryInterface(IntPtr ifaceAnsi, uint version)
    {
        try
        {
            string iface = ifaceAnsi == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ifaceAnsi) ?? "";
            return NetInterface.Query(iface, version);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // Commands from other plugins (host SendPluginCommand): RynthAi forwards UtilityBelt's
    // /ub bc, /ub bct, /ub netclients as ("ub", "bc ..."). Copied and queued; run in OnTick.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginApplyRemoteCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ApplyRemoteCommand(IntPtr actionAnsi, IntPtr valueAnsi)
    {
        try
        {
            string action = actionAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(actionAnsi) ?? "" : "";
            string value = valueAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(valueAnsi) ?? "" : "";
            if (action.Length > 0) Runtime.Plugin?.EnqueueForwarded(action, value);
        }
        catch { }
    }

    // Snapshot for panels (host GetPluginSnapshotJson): {online,node,char,tags,clients:[...]}.
    // Polled from other threads: a per-thread buffer, so no caller frees another's pointer
    // (the RynthAi pattern). The string itself is rebuilt by OnTick twice a second.
    [ThreadStatic] private static IntPtr _snapshotPtr;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetSnapshotJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSnapshotJson()
    {
        try
        {
            string json = Runtime.Plugin?.Snapshot ?? "{\"online\":false,\"clients\":[]}";
            IntPtr fresh = Marshal.StringToHGlobalAnsi(json);
            IntPtr old = _snapshotPtr;
            _snapshotPtr = fresh;
            if (old != IntPtr.Zero) Marshal.FreeHGlobal(old);
            return fresh;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Frees this thread's snapshot buffer (Shutdown runs on the pump thread).</summary>
    private static void ReleaseSnapshot()
    {
        IntPtr old = Interlocked.Exchange(ref _snapshotPtr, IntPtr.Zero);
        if (old != IntPtr.Zero) Marshal.FreeHGlobal(old);
    }
}
