// ============================================================================
//  RynthSuite - Plugins/Shared/MoveAudit.cs
//  Movement calls with a caller tag: host.SetAutoRunBy("Nav", true) and friends.
//  Linked into the plugins that move the character, like UseAudit.cs.
//
//  Why (2026-10-05, Drakkon's stuck casts on DreamWeave): a movement packet sent
//  while a cast winds up can cancel the cast, or leave the server holding it open.
//  The engine logged only one movement call in eight and never said who sent it,
//  so "which subsystem sent SetAutoRun during Buffing" could not be answered.
//  Every call is now one log line:
//
//    [Move] RynthAi/Nav SetAutoRun(False)
//
//  Identical calls from the same subsystem in a row (the nav servo turns every
//  tick; any angle counts as identical for turns) are counted instead of written
//  one by one, and the count goes out with the next line that is written (the
//  next different call, or the next repeat after RepeatWindowMs):
//
//    [Move] RynthAi/Arbiter StopCompletely() (+14 more TurnToHeading, last 230.5 before this, over 480 ms)
//
//  The engine logs the same calls (with the plugin's name) behind its own move
//  trace switch; this line adds the subsystem, which only the plugin knows.
// ============================================================================

using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

internal static class MoveAudit
{
    /// <summary>Off = the calls go through without a line. On by default.</summary>
    public static volatile bool Enabled = true;

    /// <summary>The plugin's name in every line (UseAudit.PluginName is set at Init too).</summary>
    public static string PluginName => UseAudit.PluginName;

    /// <summary>Clock (ms); tests replace it.</summary>
    public static Func<long> NowMs = () => Environment.TickCount64;

    // Identical calls closer than this to the last WRITTEN line are only counted.
    private const long RepeatWindowMs = 1000;

    private static readonly object Gate = new();
    private static string _lastKey = "";
    private static long _lastWrittenMs = long.MinValue;
    private static int _repeats;
    private static string _repeatLastArg = "";
    private static long _repeatFirstMs;

    public static bool SetAutoRunBy(this RynthCoreHost host, string who, bool on)
    {
        if (on && HeldForUse(host, who, "SetAutoRun(True)")) return false;
        if (HeldForAction(host, who, on ? "SetAutoRun(True)" : "SetAutoRun(False)", isStop: !on)) return false;
        Note(host, who, "SetAutoRun", on ? "True" : "False", collapseByKindOnly: false);
        lock (Gate) { _autoRunOn = on; if (on) _lastStartMs = NowMs(); }
        return host.SetAutoRun(on);
    }

    public static bool StopCompletelyBy(this RynthCoreHost host, string who)
    {
        if (HeldForAction(host, who, "StopCompletely()", isStop: true)) return false;
        Note(host, who, "StopCompletely", "", collapseByKindOnly: false);
        lock (Gate) { _autoRunOn = false; _motionsOn.Clear(); }
        return host.StopCompletely();
    }

    public static bool SetMotionBy(this RynthCoreHost host, string who, uint motion, bool on)
    {
        if (on && HeldForUse(host, who, $"SetMotion(0x{motion:X8}, True)")) return false;
        if (HeldForAction(host, who, $"SetMotion(0x{motion:X8}, {(on ? "True" : "False")})", isStop: !on, motion)) return false;
        Note(host, who, "SetMotion", $"0x{motion:X8}, {(on ? "True" : "False")}", collapseByKindOnly: false);
        lock (Gate) { if (on) { _motionsOn.Add(motion); _lastStartMs = NowMs(); } else _motionsOn.Remove(motion); }
        return host.SetMotion(motion, on);
    }

    public static bool DoMovementBy(this RynthCoreHost host, string who, uint motion, float speed, int holdKey)
    {
        if (HeldForUse(host, who, $"DoMovement(0x{motion:X8})")) return false;
        if (HeldForAction(host, who, $"DoMovement(0x{motion:X8})", isStop: false)) return false;
        Note(host, who, "DoMovement", $"0x{motion:X8}, {speed:0.##}, {holdKey}", collapseByKindOnly: false);
        lock (Gate) { _motionsOn.Add(motion); _lastStartMs = NowMs(); }
        return host.DoMovement(motion, speed, holdKey);
    }

    // -- Hold while any paced server action is unanswered (2026-10-06, ActionPacer) ----------
    // A use, cast or stance change the server is still animating is what a movement packet
    // disturbs: on ACE every client MoveToState is applied to the server's physics, a mode change
    // or a stop cancels the walk/turn to a used object, and a cast's end waits for its gesture.
    // While ActionPacer has one outstanding:
    //   * starting to move or turning is held (returns false, one log line per action and caller);
    //   * a stop goes through only when RynthAi itself has the character moving (autorun, or the
    //     motion it turned on); a stop with nothing of ours moving would only send a redundant
    //     movement packet in the middle of the action, so it is held as well;
    //   * the deliberate kick for a held cast (Buff/Nudge), a macro stop and typed commands pass.
    // Off unless ActionPacer.Enabled (RynthAi only).

    private static bool _autoRunOn;
    private static long _lastStartMs = long.MinValue / 2;
    /// <summary>Our "moving" state counts only this long after the last start we sent: nav re-sends
    /// autorun every second while it drives, and AC can stop on its own (arrival, a portal) without
    /// RynthAi sending a stop, so an old "on" must not hold casts back forever.</summary>
    internal const long OwnMovementFreshMs = 3000;
    private static readonly System.Collections.Generic.HashSet<uint> _motionsOn = new();
    private static readonly System.Collections.Generic.HashSet<string> _actionHoldLogged = new();
    private static long _actionHoldLoggedFor = long.MinValue;

    /// <summary>Callers whose movement is never held: the cast nudge, a macro stop, typed commands.</summary>
    internal static bool IsExemptFromActionHold(string who) =>
        who == "Buff/Nudge" || who == "MacroStop" || who == "Command";

    /// <summary>True when RynthAi itself has the character moving (autorun or a motion it turned on).</summary>
    internal static bool OwnMovementActive
    {
        get { lock (Gate) return (_autoRunOn || _motionsOn.Count > 0) && NowMs() - _lastStartMs < OwnMovementFreshMs; }
    }

    /// <summary>True when the call must wait for an unanswered paced action (internal for the tests).</summary>
    internal static bool HeldForAction(RynthCoreHost host, string who, string call, bool isStop, uint motion = 0)
    {
        if (!ActionPacer.Enabled || IsExemptFromActionHold(who)) return false;
        if (isStop)
        {
            bool moving;
            lock (Gate) moving = (motion != 0 ? _motionsOn.Contains(motion) : (_autoRunOn || _motionsOn.Count > 0))
                                 && NowMs() - _lastStartMs < OwnMovementFreshMs;
            if (moving) return false;   // a real stop always goes through
        }
        var pending = ActionPacer.Current(host);
        if (pending == null) return false;
        if (!ActionPacer.HoldsMovement(host, out string what)) return false;
        bool log;
        lock (Gate)
        {
            if (_actionHoldLoggedFor != pending.SentMs) { _actionHoldLoggedFor = pending.SentMs; _actionHoldLogged.Clear(); }
            log = Enabled && _actionHoldLogged.Add(who + (isStop ? "|stop" : "|move"));
        }
        if (log)
        {
            string kind = isStop ? "redundant stop, nothing of ours is moving" : "move/turn";
            try { host.Log($"[Move] {PluginName}/{who} {call} held ({kind}): the server is still answering {what}"); }
            catch { }
        }
        return true;
    }

    // ── Hold while a door/corpse use is in flight (2026-10-06) ─────────────────────
    // A move or a turn sent while the server is still walking/turning the character to a used
    // door or corpse cancels that use (ACE stops its move-to chain on a client MoveToState).
    // UseAudit knows when one is in flight (opt-in, UseAudit.TrackWorldUses); until the server
    // answers (UseDone) or UseAudit.WorldUseInFlightMaxMs passes, starting to move or turning
    // is skipped and the call returns false. Stops always go through. One line per held use.

    private static uint _heldLoggedFor;
    private static long _heldLoggedAt;

    /// <summary>True when the call must wait for an in-flight door/corpse use (internal for the tests).</summary>
    internal static bool HeldForUse(RynthCoreHost host, string who, string call)
    {
        if (!UseAudit.WorldUseInFlight(host, out uint objectId)) return false;
        bool log;
        lock (Gate)
        {
            long now = NowMs();
            log = Enabled && (objectId != _heldLoggedFor || now - _heldLoggedAt > UseAudit.WorldUseInFlightMaxMs);
            if (log) { _heldLoggedFor = objectId; _heldLoggedAt = now; }
        }
        if (log)
        {
            try { host.Log($"[Move] {PluginName}/{who} {call} held: the server is still answering the use of 0x{objectId:X8} (moving now would cancel it)"); }
            catch { }
        }
        return true;
    }

    public static bool StopMovementBy(this RynthCoreHost host, string who, uint motion, int holdKey)
    {
        if (HeldForAction(host, who, $"StopMovement(0x{motion:X8})", isStop: true, motion)) return false;
        Note(host, who, "StopMovement", $"0x{motion:X8}, {holdKey}", collapseByKindOnly: false);
        lock (Gate) _motionsOn.Remove(motion);
        return host.StopMovement(motion, holdKey);
    }

    /// <summary>A heading snap. Successive turns from one subsystem collapse whatever the angle.</summary>
    public static bool TurnToHeadingBy(this RynthCoreHost host, string who, float headingDegrees)
    {
        if (HeldForUse(host, who, "TurnToHeading")) return false;
        if (HeldForAction(host, who, "TurnToHeading", isStop: false)) return false;
        Note(host, who, "TurnToHeading", headingDegrees.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), collapseByKindOnly: true);
        return host.TurnToHeading(headingDegrees);
    }

    /// <summary>Writes (or counts) one call. Internal for the tests.</summary>
    internal static string? Note(RynthCoreHost host, string who, string call, string arg, bool collapseByKindOnly)
    {
        if (!Enabled) return null;
        string? line = null;
        lock (Gate)
        {
            long now = NowMs();
            string key = collapseByKindOnly ? $"{who}|{call}" : $"{who}|{call}({arg})";
            if (key == _lastKey && now - _lastWrittenMs < RepeatWindowMs)
            {
                if (_repeats == 0) _repeatFirstMs = now;
                _repeats++;
                _repeatLastArg = arg;
                return null;
            }

            string more = _repeats > 0
                ? $" (+{_repeats} more {RepeatName()} before this, over {now - _repeatFirstMs} ms)"
                : "";
            line = $"[Move] {PluginName}/{who} {call}({arg}){more}";
            _lastKey = key;
            _lastWrittenMs = now;
            _repeats = 0;
            _repeatLastArg = "";
        }
        try { host.Log(line); } catch { }
        return line;
    }

    private static string RepeatName()
    {
        int bar = _lastKey.IndexOf('|');
        string last = bar >= 0 ? _lastKey.Substring(bar + 1) : _lastKey;
        return _lastKey.EndsWith(")", StringComparison.Ordinal) ? last : $"{last}, last {_repeatLastArg}";
    }

    /// <summary>Back to a fresh load (Init, tests).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _lastKey = "";
            _lastWrittenMs = long.MinValue;
            _repeats = 0;
            _repeatLastArg = "";
            _heldLoggedFor = 0;
            _heldLoggedAt = 0;
            _autoRunOn = false;
            _motionsOn.Clear();
            _lastStartMs = long.MinValue / 2;
            _actionHoldLogged.Clear();
            _actionHoldLoggedFor = long.MinValue;
        }
    }
}
