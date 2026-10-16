using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthILT;

public static unsafe class PluginExports
{
    internal static readonly RynthPluginRuntime<RynthILTPlugin> Runtime = new();

    // Typed interfaces for other plugins (host GetPluginInterface, API v68): "RynthILT.Hub" v1, the ILT
    // tabs and sections RynthAi's Mini Remote hosts (Plugins/Shared/RynthIltHubApi.cs, HubInterface.cs),
    // and "RynthILT.Server" v1, the Leaftide feature list and fellowship rules as JSON
    // (Plugins/Shared/RynthIltServerApi.cs, ServerInterface.cs).
    // Fixed signature: void* (const char* iface, uint version).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginQueryInterface", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr QueryInterface(IntPtr ifaceAnsi, uint version)
    {
        try
        {
            string iface = ifaceAnsi == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ifaceAnsi) ?? "";
            IntPtr hub = HubInterface.Query(iface, version);
            if (hub != IntPtr.Zero) return hub;
            IntPtr server = ServerInterface.Query(iface, version);
            return server != IntPtr.Zero ? server : DataInterface.Query(iface, version);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown()
    {
        // An exception escaping an UnmanagedCallersOnly export fail-fasts the client under NativeAOT.
        try { Runtime.Shutdown(); } catch { }
        try { HubInterface.Release(); } catch { }
        try { ServerInterface.Release(); } catch { }
        try { DataInterface.Release(); } catch { }
        foreach (IntPtr p in new[] { Interlocked.Exchange(ref _bountiesPtr, IntPtr.Zero), Interlocked.Exchange(ref _progressionPtr, IntPtr.Zero),
                     Interlocked.Exchange(ref _statusPtr, IntPtr.Zero), Interlocked.Exchange(ref _sectionsPtr, IntPtr.Zero) })
            if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
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

    // The ILT Hub's floating windows (Games HUD, confirmations) in the engine's ImGui frame.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRender", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Render() => Runtime.OnRender();

    /// <summary>The engine calls this instead of RynthPluginRender while its ImGui shell is off.</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRenderOverlay", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void RenderOverlay()
    {
        try { Runtime.Plugin?.OnRenderOverlay(); }
        catch { /* an exception crossing UnmanagedCallersOnly would fail-fast the client */ }
    }

    /// <summary>
    /// Engine panels drawn by RynthILT (render thread): "guardian" is the engine's Guardian ILT
    /// panel (RynthCore's GuardianFace); "section:ilt-rates" (the ILT session extras, Session Stats),
    /// "section:ilt-settings" (the ILT settings, for an engine that still asks) and
    /// "section:ilt-fellowship" (the /fship commands) are RynthCore's Mini Remote sections.
    /// ":popout" is added when one is popped out into a window of its own, and RynthILT draws in
    /// the engine's current context either way. The Guardian panel is user-sized (want echoes the
    /// size given); a section reports the height it used. 1 = drawn, 0 = not ready / unknown surface.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRenderEmbed", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int RenderEmbed(byte* surface, float width, float height, float* wantWidth, float* wantHeight)
    {
        try
        {
            if (surface == null) return 0;
            ReadOnlySpan<byte> name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(surface);
            if (name.EndsWith(":popout"u8)) name = name[..^":popout"u8.Length];
            var plugin = Runtime.Plugin;
            if (plugin == null) return 0;
            if (name.SequenceEqual("guardian"u8))
            {
                if (!plugin.RenderEmbeddedGuardian()) return 0;
                if (wantWidth != null) *wantWidth = width;
                if (wantHeight != null) *wantHeight = height;
                return 1;
            }
            string? view = name.SequenceEqual("section:ilt-settings"u8) ? RynthILTPlugin.SettingsSurface
                         : name.SequenceEqual("section:ilt-fellowship"u8) ? RynthILTPlugin.FellowshipSurface
                         : name.SequenceEqual("section:ilt-rates"u8) ? RynthILTPlugin.RatesSurface
                         : null;
            if (view == null || !plugin.RenderEmbedded(view, out float wantH)) return 0;
            if (wantWidth != null) *wantWidth = width;
            if (wantHeight != null) *wantHeight = wantH;
            return 1;
        }
        catch
        {
            return 0;   // an exception crossing UnmanagedCallersOnly would fail-fast the client
        }
    }

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

    // ── The ILT Hub for the engine (the Skills panel's Progression tab, IltInfo) ─────────

    private static IntPtr _progressionPtr = IntPtr.Zero;

    /// <summary>
    /// The Skills panel's Progression tab (ILT augmentations / enlightenment / the attribute raiser),
    /// as RynthAi's RynthPluginGetProgressionJson was: {"available":false} until the Hub exists and
    /// off ILT. UTF-8; the previous reply stays valid until the next call. Engine pump thread only.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthILTGetProgressionJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetProgressionJson()
    {
        try
        {
            string json = Runtime.Plugin?.Hub?.BuildProgressionJson() ?? "{\"available\":false}";
            return Utf8Swap(ref _progressionPtr, json);
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// One command from the engine (UTF-8): "prog ..." (the Progression tab's edits) or an ILT
    /// "/ra" verb without "/ra" ("hub refresh", "guardian handin"). Engine pump thread.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthILTCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Command(IntPtr utf8Text)
    {
        try
        {
            if (utf8Text == IntPtr.Zero) return;
            string? text = Marshal.PtrToStringUTF8(utf8Text);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 512) return;
            Runtime.Plugin?.RunEngineCommand(text);
        }
        catch { }
    }

    /// <summary>
    /// (action, value) from the engine's plugin-command broker (Host.SendPluginCommand). Copied
    /// before returning (the caller frees them). "servermenu" is RynthCore's (2026.10.5.47+) world
    /// verdict, override and ILT world names for the ILT settings view: parsed and published here
    /// (any thread). Anything else is "prog" with the Progression tab's arguments (RynthAi forwards
    /// an older engine's "prog" here) or an ILT verb, queued and run on RynthILT's next tick.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginApplyRemoteCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ApplyRemoteCommand(IntPtr actionAnsi, IntPtr valueAnsi)
    {
        try
        {
            string action = actionAnsi != IntPtr.Zero ? (Marshal.PtrToStringAnsi(actionAnsi) ?? string.Empty) : string.Empty;
            if (action.Length == 0 || action.Length > 32) return;
            string value = valueAnsi != IntPtr.Zero ? (Marshal.PtrToStringAnsi(valueAnsi) ?? string.Empty) : string.Empty;
            var plugin = Runtime.Plugin;
            if (plugin == null) return;
            if (RynthILTPlugin.IsParsedRemoteAction(action))
            {
                // A few hundred bytes; IltSettingsModel drops anything past its MaxPayload.
                plugin.ApplyRemoteCommand(action, value);
                return;
            }
            if (action.Length + value.Length > 512) return;
            plugin.EnqueueRemoteCommand(action, value);
        }
        catch { /* never let a managed exception cross the native boundary */ }
    }

    private static IntPtr _statusPtr = IntPtr.Zero;

    /// <summary>
    /// What RynthILT knows about the world (UTF-8 JSON, previous reply valid until the next call):
    /// {"loaded":true,"ilt":bool,"forced":bool,"available":bool,"world":"..."}. "ilt" is RynthILT's
    /// own verdict (its server list or this character's "Treat this world as ILT"), never the
    /// engine's verdict echoed back: the engine's IltInfo adds it to its own detection, and an
    /// echo would keep ILT on after "/rc ilt auto". Engine pump thread.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthILTGetStatusJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetStatusJson()
    {
        try
        {
            var hub = Runtime.Plugin?.Hub;
            string json = hub == null
                ? "{\"loaded\":true,\"ilt\":false,\"forced\":false,\"available\":false,\"world\":\"\"}"
                : "{\"loaded\":true,\"ilt\":" + (hub.Options.IsIltLikeWorldLocal ? "true" : "false")
                  + ",\"forced\":" + (hub.ForceIltWorld ? "true" : "false")
                  + ",\"available\":" + (hub.Available ? "true" : "false")
                  + ",\"world\":\"" + RynthCore.Plugin.RynthILT.Hub.IltStyle.JsonEscape(hub.Options.WorldName) + "\"}";
            return Utf8Swap(ref _statusPtr, json);
        }
        catch { return IntPtr.Zero; }
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
