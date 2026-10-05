using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthTracker;

public sealed class RynthTrackerPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthTracker");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.2.1");

    private uint _playerId;
    private bool _loginComplete;
    private DateTime _sessionStart;

    private long _xpTotal;
    private long _lumTotal;
    private int  _killsTotal;
    private int  _deathsTotal;

    // XP per kill counts only XP that lands just after one of YOUR kills. Kills are the
    // server's killer notification, which goes only to the player who landed the killing
    // blow: a fellow's kill, a summon's kill or a mob someone else finished never counts,
    // and neither does the XP they bring (damage share, fellowship share, quests). That XP
    // still counts toward the totals and XP/hour.
    private long _killXp;
    private int  _xpKills;               // kills that closed their window (the per-kill divisor)
    private int  _openKills;             // kills whose window is still open
    private DateTime _killWindowUntil;   // XP rises seen until then belong to those kills
    private const double KillWindowSec = 2.5;

    private int _tickCounter;

    // Everything comes from the player's own properties, never from chat text:
    // TotalExperience 1 and AvailableLuminance 6 (the engine serves them to the pump since
    // 2026.9.29.3), and on Aelrynth the Bank mod's session totals (custom ids, 2026-09-30):
    // Radiance earned, Luminance auto-banked (it never lands on the character) and
    // Luminance drawn from the bank (it lands on the character but wasn't earned).
    private const uint Int64TotalExperience = 1;
    private const uint Int64AvailableLuminance = 6;
    private const uint Int64RadianceEarned = 9101;
    private const uint Int64LuminanceBanked = 9102;
    private const uint Int64LuminanceDrawn = 9103;
    // Retail's level-275 total. Past it a character may still be earning (awakened caps).
    private const long MaxLevelXp = 191_226_310_247;

    private long _radTotal;
    private bool _maxLevel;
    // TrackerRise (TrackerSessionStore.cs): the rise since the last read; drops only move the baseline.
    private TrackerRise _xpRise, _lumRise, _radRise, _bankedRise, _drawnRise;

    // Session persistence across plugin/engine reloads (TrackerSessionStore.cs). Saved every
    // ~30 s and on Shutdown while logged in; restored in OnLoginComplete for the same client
    // process and character within 10 minutes; deleted on a real logout.
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);
    private DateTime _nextSaveUtc;
    private bool _saveFailedLogged;
    private string? _saveDirectory;

    // Deep-audit finding #21 (2026-06-18): the RynthTrackerReset export used to
    // call ResetSession() directly, which Clear()s _killedIds from the
    // Avalonia UI thread (no Dispatcher hop) while the AC pump thread
    // concurrently Add()s/Remove()s the same HashSet in
    // OnUpdateHealth/OnDeleteObject — HashSet<uint> isn't concurrency-safe;
    // a racing Clear() can throw or corrupt its internal arrays. Mirrors the
    // engine's DispatchQueued* idiom: the export just sets a flag, OnTick
    // (pump thread, same thread as the HashSet's other mutators) performs
    // the actual reset.
    private volatile bool _pendingReset;

    // Written on the game thread every ~0.5s, read on the Avalonia UI thread.
    // Reference reads are atomic on x86 so volatile is sufficient; no lock needed.
    private volatile string _snapshot = EmptySnapshot;

    private const string EmptySnapshot =
        "{\"ss\":0.0,\"xt\":0,\"xh\":0,\"lt\":0,\"lh\":0,\"kt\":0,\"kh\":0.0,\"xk\":0,\"dt\":0,\"rt\":0,\"rh\":0,\"ml\":0}";

    public override int Initialize()
    {
        Host.Log("[RynthTracker] Plugin initialized.");
        return 0;
    }

    // The engine calls OnLoginComplete on a real login AND on a freshly (re)loaded plugin
    // while already in game, so the saved file is what tells a reload from a new login.
    public override void OnLoginComplete()
    {
        _playerId = Host.GetPlayerId();
        if (TryRestoreSession())
        {
            _loginComplete = true;
            _snapshot = BuildSnapshot();
            Host.Log("[RynthTracker] Session carried over from before the reload.");
        }
        else
        {
            ResetSession();
            _loginComplete = true;
            CleanupSavedSessions();
            Host.Log("[RynthTracker] Session started.");
        }
        _nextSaveUtc = DateTime.UtcNow + SaveInterval;
    }

    public override void OnLogout()
    {
        _loginComplete = false;
        // A real logout ends the session: the next login starts fresh, never picks this up.
        DeleteSavedSession();
        Host.Log("[RynthTracker] Session ended.");
    }

    // Plugin unload (hot reload, RynthSuite update, client shutting down). Save only while
    // logged in, never delete: a copy unloaded before its OnLoginComplete must not wipe the
    // save its predecessor left.
    public override void Shutdown()
    {
        if (_loginComplete) SaveSession();
    }

    // The server's killer notification (not chat): you landed the killing blow.
    public override void OnKillNotification(string? deathMessage)
    {
        if (!_loginComplete) return;
        _killsTotal++;
        _openKills++;
        _killWindowUntil = DateTime.UtcNow.AddSeconds(KillWindowSec);
    }

    public override void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        if (!_loginComplete) return;

        // maxHealth > 0 guards against spurious 0/0 packets during login.
        if (targetId == _playerId && currentHealth == 0 && maxHealth > 0)
            _deathsTotal++;
    }

    public override void OnTick()
    {
        if (_pendingReset)
        {
            _pendingReset = false;
            ResetSession();
            // Save the zeros now, so a reload right after a reset can't bring the old totals back.
            if (_loginComplete) { SaveSession(); _nextSaveUtc = DateTime.UtcNow + SaveInterval; }
            Host.Log("[RynthTracker] Session reset.");
        }

        if (++_tickCounter < 30) return; // rebuild every ~0.5s at 60Hz
        _tickCounter = 0;
        if (_loginComplete) PollExperience();
        _snapshot = BuildSnapshot();

        // Saved right after a poll, so totals and baselines in the file always match.
        if (_loginComplete && DateTime.UtcNow >= _nextSaveUtc)
        {
            _nextSaveUtc = DateTime.UtcNow + SaveInterval;
            SaveSession();
        }
    }

    /// <summary>Adds what experience, luminance and (on Aelrynth) Radiance went up by since the last read.</summary>
    private void PollExperience()
    {
        if (_playerId == 0 || !Host.HasGetObjectQuadProperty) return;
        bool inKillWindow = _openKills > 0 && DateTime.UtcNow <= _killWindowUntil;
        if (Host.TryGetObjectQuadProperty(_playerId, Int64TotalExperience, out long xp) && xp > 0)
        {
            long gained = _xpRise.Take(xp);
            _xpTotal += gained;
            if (inKillWindow) _killXp += gained;
            // Capped only when past retail's 275 total AND kills keep bringing nothing: an
            // awakened character's cap is higher (Aelrynth), so the total alone can't say.
            _maxLevel = xp >= MaxLevelXp && _xpKills >= 3 && _xpTotal == 0;
        }
        // Close the window once it has run out: its kills join the per-kill average (with
        // whatever XP they brought, 0 at the level cap).
        if (_openKills > 0 && !inKillWindow)
        {
            _xpKills += _openKills;
            _openKills = 0;
        }
        // Luminance earned = the rise on the character, minus what the bank put there, plus
        // what the bank took before it landed (auto-bank). Spending lowers it: only rises count.
        long lumUp = Host.TryGetObjectQuadProperty(_playerId, Int64AvailableLuminance, out long lum) && lum >= 0
            ? _lumRise.Take(lum) : 0;
        if (Host.TryGetObjectQuadProperty(_playerId, Int64LuminanceDrawn, out long drawn))
            lumUp -= _drawnRise.Take(drawn);
        if (Host.TryGetObjectQuadProperty(_playerId, Int64LuminanceBanked, out long banked))
            lumUp += _bankedRise.Take(banked);
        if (lumUp > 0) _lumTotal += lumUp;

        if (Host.TryGetObjectQuadProperty(_playerId, Int64RadianceEarned, out long rad))
            _radTotal += _radRise.Take(rad);
    }

    internal string GetSnapshot() => _snapshot;

    /// <summary>Called from the RynthTrackerReset export (Avalonia UI thread) —
    /// just flags a reset; OnTick performs it on the pump thread.</summary>
    internal void Reset() => _pendingReset = true;

    private void ResetSession()
    {
        _sessionStart = DateTime.UtcNow;
        _xpTotal    = 0;
        _lumTotal   = 0;
        _killsTotal  = 0;
        _deathsTotal = 0;
        _killXp = 0;
        _xpKills = 0;
        _openKills = 0;
        _radTotal = 0;
        // The next read of each is the new baseline.
        _xpRise.Clear(); _lumRise.Clear(); _radRise.Clear(); _bankedRise.Clear(); _drawnRise.Clear();
        _snapshot = EmptySnapshot;
    }

    // ── Session persistence ─────────────────────────────────────────────────
    //
    // Reload-gap rule: the Rise baselines are restored as saved, so the first read after a
    // reload counts everything from the last save up to now, which includes what was earned
    // while the plugin was unloaded. Totals and baselines are saved together after the same
    // poll, so that span is in neither the saved totals nor anywhere else: it is counted
    // exactly once, never twice and never lost. (Kills in the gap aren't seen, since the
    // kill notification went to no one, so the gap's XP joins the totals and XP/hour but not
    // XP per kill.)

    private string SaveDirectory => _saveDirectory ??= TrackerSessionStore.DefaultDirectory();

    // The client's start time, so a new client that happens to get a closed one's process
    // id never picks up its save. 0 if Windows won't say (both sides then match on id alone).
    private static long? _processStartTicks;
    private static long ProcessStartTicks
    {
        get
        {
            if (_processStartTicks is long t) return t;
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                t = p.StartTime.ToUniversalTime().Ticks;
            }
            catch { t = 0; }
            _processStartTicks = t;
            return t;
        }
    }

    private void SaveSession()
    {
        if (_playerId == 0) return;
        try
        {
            TrackerSessionStore.Save(SaveDirectory, CaptureState());
            _saveFailedLogged = false;
        }
        catch (Exception ex)
        {
            // Every 30 s otherwise; one line until a save works again.
            if (!_saveFailedLogged)
            {
                _saveFailedLogged = true;
                Host.Log($"[RynthTracker] Couldn't save the session: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private bool TryRestoreSession()
    {
        if (_playerId == 0) return false;
        try
        {
            int pid = Environment.ProcessId;
            if (!TrackerSessionStore.TryLoad(SaveDirectory, pid, _playerId, out TrackerSessionState s))
                return false;
            if (!TrackerSessionStore.ShouldRestore(s, pid, ProcessStartTicks, _playerId, DateTime.UtcNow))
                return false;
            ApplyState(s);
            return true;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthTracker] Couldn't read the saved session, starting fresh: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void DeleteSavedSession()
    {
        if (_playerId == 0) return;
        try { TrackerSessionStore.Delete(SaveDirectory, Environment.ProcessId, _playerId); }
        catch (Exception ex) { Host.Log($"[RynthTracker] Couldn't delete the saved session: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>A fresh session: this client's older saves (any character) and files left by
    /// clients closed long ago go.</summary>
    private void CleanupSavedSessions()
    {
        try { TrackerSessionStore.Cleanup(SaveDirectory, Environment.ProcessId, DateTime.UtcNow); }
        catch { }
    }

    private TrackerSessionState CaptureState() => new()
    {
        ProcessId = Environment.ProcessId,
        ProcessStartUtcTicks = ProcessStartTicks,
        CharacterId = _playerId,
        SavedUtcTicks = DateTime.UtcNow.Ticks,
        SessionStartUtcTicks = _sessionStart.Ticks,
        XpTotal = _xpTotal,
        LumTotal = _lumTotal,
        RadTotal = _radTotal,
        KillsTotal = _killsTotal,
        DeathsTotal = _deathsTotal,
        KillXp = _killXp,
        XpKills = _xpKills,
        OpenKills = _openKills,
        KillWindowUntilUtcTicks = _killWindowUntil.Ticks,
        MaxLevel = _maxLevel,
        XpBase = _xpRise.Baseline,
        LumBase = _lumRise.Baseline,
        RadBase = _radRise.Baseline,
        BankedBase = _bankedRise.Baseline,
        DrawnBase = _drawnRise.Baseline,
    };

    private void ApplyState(TrackerSessionState s)
    {
        _sessionStart = new DateTime(s.SessionStartUtcTicks, DateTimeKind.Utc);
        _xpTotal = s.XpTotal;
        _lumTotal = s.LumTotal;
        _radTotal = s.RadTotal;
        _killsTotal = s.KillsTotal;
        _deathsTotal = s.DeathsTotal;
        _killXp = s.KillXp;
        _xpKills = s.XpKills;
        _openKills = s.OpenKills;   // an expired window closes on the first poll, as usual
        _killWindowUntil = new DateTime(s.KillWindowUntilUtcTicks, DateTimeKind.Utc);
        _maxLevel = s.MaxLevel;
        _xpRise.Restore(s.XpBase);
        _lumRise.Restore(s.LumBase);
        _radRise.Restore(s.RadBase);
        _bankedRise.Restore(s.BankedBase);
        _drawnRise.Restore(s.DrawnBase);
    }

    private string BuildSnapshot()
    {
        double ss   = _loginComplete ? (DateTime.UtcNow - _sessionStart).TotalSeconds : 0.0;
        double hrs  = ss / 3600.0;
        long   xpHr   = hrs > 0.0 ? (long)(_xpTotal  / hrs) : 0;
        long   lumHr  = hrs > 0.0 ? (long)(_lumTotal  / hrs) : 0;
        double killHr = hrs > 0.0 ? _killsTotal / hrs : 0.0;
        long   xpKill = _xpKills > 0 ? _killXp / _xpKills : 0;   // your kills only, see _killXp
        long   radHr  = hrs > 0.0 ? (long)(_radTotal / hrs) : 0;

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        return "{\"ss\":"  + ss.ToString("F1", ic)
             + ",\"xt\":"  + _xpTotal
             + ",\"xh\":"  + xpHr
             + ",\"lt\":"  + _lumTotal
             + ",\"lh\":"  + lumHr
             + ",\"kt\":"  + _killsTotal
             + ",\"kh\":"  + killHr.ToString("F1", ic)
             + ",\"xk\":"  + xpKill
             + ",\"dt\":"  + _deathsTotal
             + ",\"rt\":"  + _radTotal       // Radiance (Aelrynth); 0 elsewhere
             + ",\"rh\":"  + radHr
             + ",\"ml\":"  + (_maxLevel ? 1 : 0)   // at the level cap: experience can't rise
             + "}";
    }
}
