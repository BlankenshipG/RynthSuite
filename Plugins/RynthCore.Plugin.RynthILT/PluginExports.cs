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
    public static void Shutdown()
    {
        Runtime.Shutdown();
        IntPtr old = Interlocked.Exchange(ref _sectionsPtr, IntPtr.Zero);
        if (old != IntPtr.Zero) Marshal.FreeCoTaskMem(old);
        _sectionsJson = null;
    }

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

    // ── ILT settings (IltSettings/) ─────────────────────────────────────────

    /// <summary>The standalone ILT settings window ("/ilt settings"). Render thread.</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRender", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Render()
    {
        try { Runtime.OnRender(); }
        catch { /* an exception crossing UnmanagedCallersOnly would fail-fast the client */ }
    }

    /// <summary>
    /// The engine's plugin-command broker: "servermenu" from RynthCore (2026.10.5.47+) and
    /// "iltfeatures" from RynthAi (0.5.56+). The ANSI strings are copied before returning (the
    /// caller frees them); the payloads are only parsed, so any thread is fine.
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
    /// RynthCore's Mini Remote sections: "section:ilt-settings" (":popout" added in a popped-out
    /// window) draws the ILT settings into the engine's current window. 1 = drawn, 0 = not ready
    /// or not ours.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRenderEmbed", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int RenderEmbed(byte* surface, float width, float height, float* wantWidth, float* wantHeight)
    {
        try
        {
            if (surface == null) return 0;
            ReadOnlySpan<byte> name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(surface);
            if (name.EndsWith(":popout"u8)) name = name[..^":popout"u8.Length];
            if (!name.SequenceEqual("section:ilt-settings"u8)) return 0;
            var plugin = Runtime.Plugin;
            if (plugin == null || !plugin.RenderEmbeddedSettings(out float wantH)) return 0;
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
    /// RynthCore 2026.10.5.46+ (optional): what RynthILT adds to the Mini Remote's sections (UTF-8
    /// JSON, schema 1). Null before login. An unchanged manifest keeps the same buffer.
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
            return Utf8Swap(ref _sectionsPtr, json);
        }
        catch { return IntPtr.Zero; }
    }

    private static IntPtr Utf8Swap(ref IntPtr slot, string json)
    {
        IntPtr newPtr = Marshal.StringToCoTaskMemUTF8(json);
        IntPtr oldPtr = Interlocked.Exchange(ref slot, newPtr);
        if (oldPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(oldPtr);
        return newPtr;
    }
}
