using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthILT;

public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<RynthILTPlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown() => Runtime.Shutdown();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => RynthILTPlugin.NamePointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => RynthILTPlugin.VersionPointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    // Bounty lines (and eating the reply to the tracker's own /bounty list).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatWindowText", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatWindowText(IntPtr textUtf16, int chatType, IntPtr eatFlag)
        => Runtime.OnChatWindowText(textUtf16, chatType, eatFlag);

    // "/ilt ..." commands, and noticing a /bounty the player typed (its reply is never eaten).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag)
        => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    // This character's kills re-read the bounty list (while the Bounties window is open).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnKillNotification", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnKillNotification(IntPtr textUtf16) => Runtime.OnKillNotification(textUtf16);

    // ── Bounties bridge (the engine's ImGui Bounties window, UI/Data/BountiesData.cs) ──────
    // UTF-8 both ways. Polling the getter is what keeps the tracker's reads running, so only
    // the engine's Bounties source may call it (on the plugin pump, 1 s, on ILT only).
    // Alloc-new → swap → free-old: the previous reply stays valid until the next call.

    private static IntPtr _bountiesPtr = IntPtr.Zero;

    /// <summary>The tracked bounties as JSON (BountyBridgePayload); 0 before login.</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthILTGetBountiesJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetBountiesJson()
    {
        try
        {
            string? json = Runtime.Plugin?.Bounties?.BuildJson();
            return json == null ? IntPtr.Zero : Utf8Swap(ref _bountiesPtr, json);
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>"refresh" or "set {option} {value}" (BountyTracker.Command).</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthILTBountyCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void BountyCommand(IntPtr utf8Text)
    {
        try
        {
            if (utf8Text == IntPtr.Zero) return;
            string? text = Marshal.PtrToStringUTF8(utf8Text);
            if (string.IsNullOrEmpty(text) || text.Length > 256) return;
            Runtime.Plugin?.Bounties?.Command(text);
        }
        catch { }
    }

    private static IntPtr Utf8Swap(ref IntPtr slot, string json)
    {
        IntPtr newPtr = Marshal.StringToCoTaskMemUTF8(json);
        IntPtr oldPtr = Interlocked.Exchange(ref slot, newPtr);
        if (oldPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(oldPtr);
        return newPtr;
    }
}
