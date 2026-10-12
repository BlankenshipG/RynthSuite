using System;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthTracker;

// The session's numbers come from SessionStats (Plugins\Shared), the same code RynthAi's Mini
// Remote uses, so the Tracker panel and the Mini Remote agree (0.4.0, 2026-10-11): experience,
// luminance (with ACECustom's bank and allegiance pass-up), luminance of the last five minutes,
// kills (including a multi-target spell's extra kills found by their corpses), XP per kill,
// deaths and Radiance. SessionStatsTracker reads the game and keeps the session across reloads.
public sealed class RynthTrackerPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthTracker");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.4.0");   // previous release: 0.3.0

    private SessionStatsTracker? _session;
    private int _tickCounter;

    // Written on the game thread every ~0.5s, read on the engine's UI thread.
    // Reference reads are atomic on x86 so volatile is sufficient; no lock needed.
    private volatile string _snapshot = EmptySnapshot;

    private const string EmptySnapshot =
        "{\"ss\":0.0,\"xt\":0,\"xh\":0,\"lt\":0,\"lh\":0,\"l5\":0,\"pt\":0,\"ph\":0,\"pk\":0,"
        + "\"kt\":0,\"kh\":0.0,\"xk\":0,\"dt\":0,\"rt\":0,\"rh\":0,\"ml\":0}";

    public override int Initialize()
    {
        _session = new SessionStatsTracker(Host, "[RynthTracker]", TrackerSessionStore.DefaultDirectory());
        Host.Log("[RynthTracker] Plugin initialized.");
        return 0;
    }

    public override void OnLoginComplete() => _session?.OnLoginComplete();

    public override void OnLogout()
    {
        _session?.OnLogout();
        _snapshot = EmptySnapshot;
    }

    public override void Shutdown() => _session?.Shutdown();

    public override void OnKillNotification(string? deathMessage) => _session?.OnKillNotification(deathMessage);

    public override void OnChatWindowText(string? text, int chatType, ref int eat) => _session?.OnChatWindowText(text);

    public override void OnCreateObject(uint objectId) => _session?.OnCreateObject(objectId);

    public override void OnDeleteObject(uint objectId) => _session?.OnDeleteObject(objectId);

    public override void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
        => _session?.OnUpdateHealth(targetId, currentHealth, maxHealth);

    public override void OnTick()
    {
        var session = _session;
        if (session == null) return;
        session.Tick();
        if (++_tickCounter < 30) return; // rebuild every ~0.5s at 60Hz
        _tickCounter = 0;
        _snapshot = session.Active ? BuildSnapshot(session.View, DateTime.UtcNow) : EmptySnapshot;
    }

    internal string GetSnapshot() => _snapshot;

    /// <summary>Called from the RynthTrackerReset export (engine UI thread): just flags a reset;
    /// the next OnTick performs it on the pump thread.</summary>
    internal void Reset() => _session?.RequestReset();

    private static string BuildSnapshot(SessionStatsView v, DateTime now)
    {
        // The session clock runs between polls; the totals and rates are the poll's.
        double ss = Math.Max(0, (now - v.SessionStartUtc).TotalSeconds);
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        return "{\"ss\":"  + ss.ToString("F1", ic)
             + ",\"xt\":"  + v.Xp
             + ",\"xh\":"  + (long)v.XpPerHour
             + ",\"lt\":"  + v.Lum
             + ",\"lh\":"  + (long)v.LumPerHour
             + ",\"l5\":"  + v.LumLast5Min              // luminance of the last five minutes
             + ",\"pt\":"  + v.LumPassUp                // allegiance pass-up (ACECustom bank)
             + ",\"ph\":"  + (long)v.PassUpPerHour
             + ",\"pk\":"  + (v.PassUpTracked ? 1 : 0)  // 1 = the bank is read, pass-up is known
             + ",\"kt\":"  + v.Kills
             + ",\"kh\":"  + v.KillsPerHour.ToString("F1", ic)
             + ",\"xk\":"  + v.XpPerKill                // your kills only
             + ",\"dt\":"  + v.Deaths
             + ",\"rt\":"  + v.Rad                      // Radiance (Aelrynth); 0 elsewhere
             + ",\"rh\":"  + (long)v.RadPerHour
             + ",\"ml\":"  + (v.MaxLevel ? 1 : 0)       // at the level cap: experience can't rise
             + "}";
    }
}
