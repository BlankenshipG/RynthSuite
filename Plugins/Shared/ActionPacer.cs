// ============================================================================
//  RynthSuite - Plugins/Shared/ActionPacer.cs
//  One server action at a time: RynthAi's pacing of uses, casts and combat-mode
//  changes on the server's answers (2026-10-06).
//
//  Why: Drakkon's "can't use anything" and "stuck in the cast animation" stalls on
//  DreamWeave (ops/overnight/2026-10-06-action-pacing.md). Every stall began with a
//  combat-mode change sent while a corpse use was still unanswered by the server
//  (4 of 4 onsets; 0 stalls after ~920 uses with no such overlap). Once in that state
//  the server finishes nothing that waits on an animation until a relog, and the bot
//  made it worse by firing the next cast/use every 0.4-2.5 s because nothing waited
//  for the server's answer: the client busy count it paced on was zeroed and repaired
//  hundreds of times an hour, and "cast resolved" meant the enchantment, not the
//  server's UseDone.
//
//  The rule here:
//    * A use, a use-on, a cast or a combat-mode change that RynthAi sends is the ONE
//      outstanding server action until the server answers it:
//        - use / use-on / cast: the next UseDone (GameEvent 0x01C7) after the send,
//          whatever its error (a refusal is an answer too); a wield also counts as
//          answered when the item reads wielded (a wield gets no UseDone);
//        - combat mode: the client reads the new mode and ModeSettleMs has passed
//          (the stance animation; the server sends no UseDone for it);
//      or its time limit passes (logged; nothing is force-cleared).
//    * While one is outstanding, RynthAi starts no other one (MayStart; mode changes
//      are refused right at ChangeCombatModeFor) and moves or turns the character
//      only to stop real movement (MoveAudit asks HoldsMovement).
//    * Heals keep some priority: at Emergency Heal At a heal may go ahead of a corpse/door
//      use the server hasn't answered after EmergencyAfterWorldUseMs, and one heal may
//      follow a cast whose result is already in (the server queues one cast behind the
//      recoil). A heal under the plain Heal line waits for the use's answer (2026-10-06,
//      below: an action during the use's server turn stops that turn for good).
//    * And nothing starts while the character is still animating: the client's own motion
//      queue (CMotionInterp pending motions, the engine's cast gate: GetCastBusyState) must
//      be empty, for uses, kits, potions, casts, stance changes, wields and the item
//      managers' moves (GestureHolds). Melee/missile attack repeats are exempt (MayStart
//      with gestureGate false). Time limit GestureHoldMaxMs (GestureEmergencyMaxMs for an
//      emergency heal), logged, like Combat/CastGateWatchdog for casts.
//
//  Why the animation gate (2026-10-06 afternoon, ops/overnight/2026-10-06-turnto-stall.md,
//  replayed through ACE's own physics code): on ACE a use in reach is finished by the
//  server's MoveToManager turning the character (TurnRight on the server's copy of the
//  motion state). Every server motion for the character (a swing, a kit, a cast gesture,
//  a stance) is applied with MotionInterp.apply_raw_movement, which rewrites the
//  interpreted TurnCommand from the raw state (none) and so stops that TurnRight; the
//  MoveToManager is never told (its FailProgressCount is counted but never acted on) and
//  stays Initialized, which keeps the player "moving or animating", so Player_Tick never
//  runs the use's callback either. The use is answered only when the next use fails it:
//  "UseDone with no error, nothing opens", the stall's signature. Starting nothing while
//  the character animates, and nothing while a use's turn is unanswered, keeps every
//  server motion out of every server turn.
//
//  Opt-in (Enabled, RynthAi sets it at Init); off = every call goes through as before.
//  Linked into RynthAi only. UseAudit reports its sends through UseAudit.AfterSend.
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

internal enum PacedKind
{
    None,
    /// <summary>A door, corpse, portal or NPC: the server walks/turns the character to it first.</summary>
    WorldUse,
    /// <summary>A kit, potion, stone, wand or weapon (wield), scroll... in the character's own packs.</summary>
    ItemUse,
    /// <summary>A spell cast.</summary>
    Cast,
    /// <summary>A combat-mode (stance) change.</summary>
    CombatMode,
}

internal enum HealNeed
{
    None,
    /// <summary>Health under a heal line.</summary>
    Heal,
    /// <summary>Health at or under Emergency Heal At.</summary>
    Emergency,
}

/// <summary>The one server action RynthAi is waiting on.</summary>
internal sealed class PacedAction
{
    public PacedKind Kind;
    public uint Id;
    public string Label = "";
    public string Who = "";
    public long SentMs;
    public int SeqAtSend;
    public int ModeTarget;
    public bool WieldedAtSend;
    public bool ResultSeen;      // a cast's result is in (enchantment / chat); the server is in its recoil
    public bool HealQueued;      // the one heal allowed behind that recoil went out
    public long ModeSeenMs;      // when the client first read the requested mode
    public long WieldSeenMs;     // when a wield first read done
    public readonly HashSet<string> LoggedWaits = new();
}

internal static class ActionPacer
{
    /// <summary>Off = nothing is paced (every call goes through as before). RynthAi turns it on.</summary>
    public static volatile bool Enabled;

    /// <summary>Clock (ms); tests replace it.</summary>
    public static Func<long> NowMs = () => Environment.TickCount64;

    // Time limits: the longest the server took to answer in 3.7 h of healthy logs (10-05/10-06,
    // p99 / max): corpse use 3.0 / 3.7 s, kit 2.5 s, buff cast 3.6 s, vital cast 4.7 / 5.0 s.
    public const long WorldUseMaxMs = 5000;
    public const long ItemUseMaxMs = 3500;
    public const long CastMaxMs = 7000;
    public const long ModeMaxMs = 2000;
    /// <summary>The stance animation after the client reads the new mode: no action before it ends.</summary>
    public const long ModeSettleMs = 800;
    /// <summary>A wield that reads done: a short settle for the wield motion.</summary>
    public const long WieldSettleMs = 150;
    /// <summary>At Emergency Heal At a heal may go ahead of a door/corpse use unanswered this long. It stops
    /// the server's walk/turn to that object (the use is then answered by the next one); a plain heal waits.</summary>
    public const long EmergencyAfterWorldUseMs = 300;
    /// <summary>The client's motion queue reading busy this long is taken as stuck: the action goes (logged).
    /// Same backstop as Combat/CastGateWatchdog (no real gesture, stance or swing lasts this long).</summary>
    public const long GestureHoldMaxMs = 4000;
    /// <summary>An emergency heal waits for the character's animation at most this long.</summary>
    public const long GestureEmergencyMaxMs = 1500;
    /// <summary>A heal behind a stance change: once the client has read the new mode this long.</summary>
    public const long HealAfterModeReadMs = 300;
    /// <summary>An engine without UseDone: an action counts as answered after this (no real pacing).</summary>
    public const long NoUseDoneFallbackMs = 600;

    private static readonly object Gate = new();
    private static PacedAction? _cur;

    // Counters for /ra pace and the periodic summary.
    private static long _started, _answered, _timedOut, _sentOverOutstanding, _modeRefused, _modeSkipped;
    private static long _heldStarts, _heldMoves, _healsAhead;
    private static long _gestureHolds, _gestureLimits;
    private static long _gestureBusySince;      // when the client's motion queue was first seen busy (0 = clear)
    private static bool _gestureOverride;       // the hold limit passed: open until the queue reads clear
    private static readonly HashSet<string> _gestureLogged = new();
    private static long _maxWaitMs;
    private static string _maxWaitWhat = "";
    private static readonly Dictionary<string, int> _holdsByWho = new();
    private static string _lastTimeout = "";

    public static long Started => _started;
    public static long Answered => _answered;
    public static long TimedOut => _timedOut;
    public static long SentOverOutstanding => _sentOverOutstanding;
    public static long ModeChangesRefused => _modeRefused;
    public static long ModeChangesSkipped => _modeSkipped;
    public static long HeldStarts => _heldStarts;
    public static long HeldMoves => _heldMoves;
    public static long HealsAhead => _healsAhead;
    public static long GestureHoldCount => _gestureHolds;
    public static long GestureLimits => _gestureLimits;

    /// <summary>Back to a fresh load (Init, logout, tests). Enabled is left as it is.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _cur = null;
            _started = _answered = _timedOut = _sentOverOutstanding = _modeRefused = _modeSkipped = 0;
            _heldStarts = _heldMoves = _healsAhead = 0;
            _gestureHolds = _gestureLimits = 0;
            _swingSentMs = 0; _swingSeenBusy = false; _swingHolds = 0; _swingLogged.Clear();
            _gestureBusySince = 0;
            _gestureOverride = false;
            _gestureLogged.Clear();
            _maxWaitMs = 0;
            _maxWaitWhat = "";
            _holdsByWho.Clear();
            _lastTimeout = "";
        }
    }

    /// <summary>Forget the outstanding action (logout, death, portal): the server dropped it.</summary>
    public static void Forget(string why)
    {
        lock (Gate) { _cur = null; }
    }

    // ── Recording what went out ──────────────────────────────────────────────

    /// <summary>
    /// Records an action that just went out. <paramref name="seqAtSend"/> is the UseDone count read
    /// BEFORE the send. Logs once when it went out over another unanswered action (a player command,
    /// a meta, or a heal let ahead).
    /// </summary>
    public static void Begin(RynthCoreHost host, PacedKind kind, uint id, string label, string who, int seqAtSend, int modeTarget = 0)
    {
        if (!Enabled || kind == PacedKind.None) return;
        PacedAction? prev = Current(host);
        long now = NowMs();
        var a = new PacedAction
        {
            Kind = kind, Id = id, Label = label, Who = who, SentMs = now, SeqAtSend = seqAtSend, ModeTarget = modeTarget,
        };
        if (kind == PacedKind.ItemUse) a.WieldedAtSend = IsWieldedByPlayer(host, id);
        string? over = null;
        lock (Gate)
        {
            _started++;
            if (prev != null)
            {
                _sentOverOutstanding++;
                over = $"[Pace] {who}: {Describe(kind, label)} went out while {Describe(prev.Kind, prev.Label)} ({prev.Who}) was unanswered for {now - prev.SentMs} ms";
            }
            _cur = a;
        }
        if (over != null) Log(host, over);
    }

    /// <summary>A cast's result (enchantment landed, "You cast ...") is in: the server is in its recoil.</summary>
    public static void NoteCastResult()
    {
        lock (Gate)
        {
            if (_cur is { Kind: PacedKind.Cast }) _cur.ResultSeen = true;
        }
    }

    // ── The outstanding action ───────────────────────────────────────────────

    /// <summary>
    /// The action still waiting for the server's answer, or null. Each call settles answers and
    /// time limits first, so it is always current.
    /// </summary>
    public static PacedAction? Current(RynthCoreHost host)
    {
        if (!Enabled) return null;
        PacedAction? a;
        lock (Gate) a = _cur;
        if (a == null) return null;

        long now = NowMs();
        long age = now - a.SentMs;
        string? answeredHow = null;
        bool timedOut = false;

        switch (a.Kind)
        {
            case PacedKind.WorldUse:
            case PacedKind.ItemUse:
            case PacedKind.Cast:
            {
                bool hasSeq = false;
                int seq = 0;
                try { hasSeq = host.HasUseDoneSeq; if (hasSeq) seq = host.GetUseDoneSeq(); } catch { hasSeq = false; }
                if (hasSeq && seq != a.SeqAtSend)
                {
                    answeredHow = "UseDone";
                }
                else if (a.Kind == PacedKind.ItemUse && IsWieldedByPlayer(host, a.Id) != a.WieldedAtSend)
                {
                    if (a.WieldSeenMs == 0) a.WieldSeenMs = now;
                    if (now - a.WieldSeenMs >= WieldSettleMs) answeredHow = "wield";
                }
                else if (!hasSeq && age >= NoUseDoneFallbackMs)
                {
                    answeredHow = "time (no UseDone on this engine)";
                }
                long max = a.Kind switch { PacedKind.WorldUse => WorldUseMaxMs, PacedKind.Cast => CastMaxMs, _ => ItemUseMaxMs };
                if (answeredHow == null && age >= max) timedOut = true;
                break;
            }
            case PacedKind.CombatMode:
            {
                int mode = -1;
                try { if (host.HasGetCurrentCombatMode) mode = host.GetCurrentCombatMode(); } catch { mode = -1; }
                if (mode == a.ModeTarget || mode < 0)
                {
                    if (a.ModeSeenMs == 0) a.ModeSeenMs = now;
                    if (age >= ModeSettleMs) answeredHow = "mode";
                }
                if (answeredHow == null && age >= ModeMaxMs) timedOut = true;
                break;
            }
        }

        if (answeredHow == null && !timedOut) return a;

        string? line = null;
        lock (Gate)
        {
            if (!ReferenceEquals(_cur, a)) return _cur;   // replaced meanwhile
            _cur = null;
            if (timedOut)
            {
                _timedOut++;
                _lastTimeout = $"{Describe(a.Kind, a.Label)} ({a.Who}) after {age} ms";
                line = $"[Pace] no answer from the server to {Describe(a.Kind, a.Label)} ({a.Who}) in {age} ms - going on (nothing cleared)";
            }
            else
            {
                _answered++;
            }
        }
        if (line != null) Log(host, line);
        return null;
    }

    /// <summary>True while an action is waiting for the server (out: it).</summary>
    public static bool Outstanding(RynthCoreHost host, out PacedAction? action)
    {
        action = Current(host);
        return action != null;
    }

    // ── Gates ───────────────────────────────────────────────────────────────

    /// <summary>
    /// May <paramref name="who"/> send a use, a cast or a mode change now? False (with why) while
    /// another action waits for the server, except the heal rules in the file header, and while
    /// the character is still animating (<see cref="GestureHolds"/>; skipped with
    /// <paramref name="gestureGate"/> false: melee/missile attack repeats). One log line per
    /// outstanding action (or animation) and subsystem.
    /// </summary>
    public static bool MayStart(RynthCoreHost host, string who, HealNeed heal, out string why, bool gestureGate = true)
    {
        if (!Enabled) { why = ""; return true; }
        if (!OutstandingAllows(host, who, heal, out why)) return false;
        if (gestureGate && GestureHolds(host, who, heal, out why)) return false;
        return true;
    }

    private static bool OutstandingAllows(RynthCoreHost host, string who, HealNeed heal, out string why)
    {
        why = "";
        PacedAction? a = Current(host);
        if (a == null) return true;
        long age = NowMs() - a.SentMs;

        if (heal != HealNeed.None)
        {
            if (a.Kind == PacedKind.WorldUse && heal == HealNeed.Emergency && age >= EmergencyAfterWorldUseMs)
            {
                lock (Gate) _healsAhead++;
                Log(host, $"[Pace] {who}: the heal goes ahead of {Describe(a.Kind, a.Label)} (unanswered for {age} ms)");
                return true;
            }
            // A stance change (most often the one this heal asked for: the wand swap, or peace for
            // a kit): the heal goes once the client has read the new mode for HealAfterModeReadMs,
            // like BuffManager's emergency settle, not the full ModeSettleMs.
            if (a.Kind == PacedKind.CombatMode && a.ModeSeenMs != 0 && NowMs() - a.ModeSeenMs >= HealAfterModeReadMs)
                return true;
            if (a.Kind == PacedKind.Cast && a.ResultSeen && !a.HealQueued)
            {
                a.HealQueued = true;
                lock (Gate) _healsAhead++;
                return true;
            }
        }

        why = $"{Describe(a.Kind, a.Label)} sent {age} ms ago by {a.Who} has no answer yet";
        bool log;
        lock (Gate)
        {
            _heldStarts++;
            _holdsByWho[who] = _holdsByWho.TryGetValue(who, out int n) ? n + 1 : 1;
            if (age > _maxWaitMs) { _maxWaitMs = age; _maxWaitWhat = Describe(a.Kind, a.Label); }
            log = a.LoggedWaits.Add(who);
        }
        if (log) Log(host, $"[Pace] {who} waits: {why}");
        return false;
    }

    /// <summary>
    /// True (with why) while the character is still animating: the client's own motion queue
    /// (CMotionInterp pending motions, read by the engine as GetCastBusyState) is not empty.
    /// The client animates exactly the motions the server sends for the character, a moment
    /// after the server applies them, so an empty queue means the server's copy is done too.
    /// Past GestureHoldMaxMs (GestureEmergencyMaxMs for an emergency heal) the action goes,
    /// logged once per busy spell. No opinion (false) on an engine without the gate.
    /// </summary>
    public static bool GestureHolds(RynthCoreHost host, string who, HealNeed heal, out string why)
    {
        why = "";
        if (!Enabled) return false;
        bool busy;
        try { busy = host.HasGetCastBusyState && host.GetCastBusyState() != 0; } catch { busy = false; }
        long now = NowMs();
        if (SwingHolds(host, who, heal, busy, now, out why)) return true;
        string? line = null;
        lock (Gate)
        {
            if (!busy)
            {
                if (_gestureOverride)
                    line = "[Pace] the character's animation queue reads clear again";
                _gestureBusySince = 0;
                _gestureOverride = false;
                _gestureLogged.Clear();
            }
            else
            {
                if (_gestureBusySince == 0) _gestureBusySince = now;
                long age = now - _gestureBusySince;
                if (!_gestureOverride && age >= GestureHoldMaxMs)
                {
                    _gestureOverride = true;
                    _gestureLimits++;
                    line = $"[Pace] {who}: the character's animation queue has read busy for {age} ms - taken as stuck, going on";
                }
                if (_gestureOverride)
                {
                    busy = false;
                }
                else if (heal == HealNeed.Emergency && age >= GestureEmergencyMaxMs)
                {
                    _gestureLimits++;
                    line = $"[Pace] {who}: emergency heal after {age} ms of animation - going on";
                    busy = false;
                }
                else
                {
                    _gestureHolds++;
                    _holdsByWho[who] = _holdsByWho.TryGetValue(who, out int n) ? n + 1 : 1;
                    why = $"the character is still animating ({age} ms)";
                    if (_gestureLogged.Add(who))
                        line = $"[Pace] {who} waits: {why}";
                }
            }
        }
        if (line != null) Log(host, line);
        return busy;
    }

    // A swing sent but not animating yet (Drakkon 2026-10-06 16:45:37): the wand's use went 110 ms
    // after a MeleeAttack, before the swing showed in the client's motion queue, so the motion gate
    // let it through; the switch landed on the swing the server was starting and the next cast was
    // held open. After CombatManager sends an attack, nothing else starts until the client's motion
    // queue has read busy and then clear (the swing played), or SwingStartMaxMs passes.
    public const long SwingStartMaxMs = 1500;
    public const long SwingEmergencyMaxMs = 500;
    private static long _swingSentMs;
    private static bool _swingSeenBusy;
    private static long _swingHolds;
    private static readonly HashSet<string> _swingLogged = new();
    public static long SwingHoldCount => _swingHolds;

    /// <summary>CombatManager sent a melee or missile attack.</summary>
    public static void NoteAttackSent()
    {
        lock (Gate)
        {
            _swingSentMs = NowMs();
            _swingSeenBusy = false;
            _swingLogged.Clear();
        }
    }

    private static bool SwingHolds(RynthCoreHost host, string who, HealNeed heal, bool busy, long now, out string why)
    {
        why = "";
        string? line = null;
        bool hold = false;
        lock (Gate)
        {
            if (_swingSentMs == 0) return false;
            long age = now - _swingSentMs;
            if (busy) _swingSeenBusy = true;
            if ((_swingSeenBusy && !busy) || age >= SwingStartMaxMs)
            {
                _swingSentMs = 0;
            }
            else if (!_swingSeenBusy && !(heal == HealNeed.Emergency && age >= SwingEmergencyMaxMs))
            {
                hold = true;
                _swingHolds++;
                why = $"a swing sent {age} ms ago hasn't started animating yet";
                if (_swingLogged.Add(who)) line = $"[Pace] {who} waits: {why}";
            }
        }
        if (line != null) Log(host, line);
        return hold;
    }

    /// <summary>
    /// True while the outstanding action is one the server animates (all of them: the walk/turn
    /// to a used object, a heal or wield motion, a cast, a stance change). MoveAudit holds
    /// RynthAi's movement starts and turns meanwhile.
    /// </summary>
    public static bool HoldsMovement(RynthCoreHost host, out string what)
    {
        what = "";
        PacedAction? a = Current(host);
        if (a == null) return false;
        what = $"{Describe(a.Kind, a.Label)} ({a.Who}, {NowMs() - a.SentMs} ms)";
        lock (Gate) _heldMoves++;
        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    internal static string Describe(PacedKind kind, string label) => kind switch
    {
        PacedKind.WorldUse => $"the use of {label}",
        PacedKind.ItemUse => $"the use of {label}",
        PacedKind.Cast => $"the cast of {label}",
        PacedKind.CombatMode => $"the change to {label} mode",
        _ => label,
    };

    internal static string ModeName(int mode) => mode switch
    {
        1 => "Peace", 2 => "Melee", 4 => "Missile", 8 => "Magic", _ => $"mode {mode}",
    };

    internal static bool IsWieldedByPlayer(RynthCoreHost host, uint id)
    {
        try
        {
            if (id == 0 || !host.HasGetObjectWielderInfo || !host.HasGetPlayerId) return false;
            uint player = host.GetPlayerId();
            return player != 0 && host.TryGetObjectWielderInfo(id, out uint wielder, out _) && wielder == player;
        }
        catch { return false; }
    }

    /// <summary>World object (no container, no wielder) = the server walks to it; else an item of ours.</summary>
    internal static PacedKind ClassifyUse(RynthCoreHost host, uint id, bool doorOrCorpse)
    {
        if (doorOrCorpse) return PacedKind.WorldUse;
        try
        {
            if (host.HasGetObjectOwnershipInfo && host.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out _))
                return container == 0 && wielder == 0 ? PacedKind.WorldUse : PacedKind.ItemUse;
        }
        catch { }
        return PacedKind.ItemUse;
    }

    internal static int UseDoneSeqNow(RynthCoreHost host)
    {
        try { return host.HasUseDoneSeq ? host.GetUseDoneSeq() : 0; } catch { return 0; }
    }

    private static void Log(RynthCoreHost host, string line)
    {
        try { host.Log(line); } catch { }
    }

    /// <summary>Lines for /ra pace.</summary>
    public static string[] DescribeLines(RynthCoreHost host)
    {
        var lines = new List<string>();
        PacedAction? a = Current(host);
        lines.Add($"Action pacing: {(Enabled ? "on" : "off")} (one server action at a time; see ops note 2026-10-06-action-pacing)");
        lines.Add(a == null
            ? "  Waiting on: nothing"
            : $"  Waiting on: {Describe(a.Kind, a.Label)} from {a.Who}, sent {NowMs() - a.SentMs} ms ago{(a.ResultSeen ? " (result in, recoil)" : "")}");
        lock (Gate)
        {
            lines.Add($"  Sent {_started}, answered {_answered}, no answer in time {_timedOut}{(_lastTimeout.Length > 0 ? $" (last: {_lastTimeout})" : "")}");
            lines.Add($"  Starts held {_heldStarts}, moves held {_heldMoves}, heals let ahead {_healsAhead}, sent over an unanswered one {_sentOverOutstanding}");
            lines.Add($"  Mode changes refused while waiting {_modeRefused}, skipped (already in that mode) {_modeSkipped}");
            lines.Add($"  Held while the character was animating {_gestureHolds}, animation time limits {_gestureLimits}");
            if (_maxWaitMs > 0) lines.Add($"  Longest wait seen: {_maxWaitMs} ms ({_maxWaitWhat})");
            if (_holdsByWho.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in _holdsByWho) parts.Add($"{kv.Key} {kv.Value}");
                lines.Add("  Holds by subsystem: " + string.Join(", ", parts));
            }
        }
        return lines.ToArray();
    }

    // ── The paced sends that are not uses ────────────────────────────────────

    /// <summary>
    /// CastSpell, recorded as the outstanding action. RynthAi's callers check MayStart first;
    /// <paramref name="asked"/> (a player command or meta) is never held, only recorded.
    /// </summary>
    public static bool CastSpellFor(this RynthCoreHost host, uint targetId, int spellId, string who, string label, bool asked = false)
    {
        int seq = UseDoneSeqNow(host);
        bool ok = host.CastSpell(targetId, spellId);
        if (ok) Begin(host, PacedKind.Cast, unchecked((uint)spellId), label, who, seq);
        return ok;
    }

    /// <summary>
    /// ChangeCombatMode with the pacing rules: nothing is sent when the client already reads that
    /// mode (the engine would push a raw 0x53 to the server anyway, and on ACE every mode change
    /// cancels the walk/turn of a pending use and fails a cast in progress) unless
    /// <paramref name="force"/>; and nothing while another server action is unanswered unless
    /// <paramref name="force"/>. Returns false only when the pacing held the request back (or the
    /// client already reads that mode); otherwise the request was made, as a plain ChangeCombatMode
    /// call was before (whose result the callers never read), so their retry timers run as before.
    /// </summary>
    public static bool ChangeCombatModeFor(this RynthCoreHost host, int mode, string who, string why, bool force = false)
    {
        if (!host.HasChangeCombatMode) return true;   // nothing to send or pace (the callers went on before too)
        if (Enabled && !force)
        {
            int current = -1;
            try { if (host.HasGetCurrentCombatMode) current = host.GetCurrentCombatMode(); } catch { current = -1; }
            if (current == mode)
            {
                lock (Gate) _modeSkipped++;
                return false;
            }
            PacedAction? a = Current(host);
            if (a != null)
            {
                bool log;
                lock (Gate)
                {
                    _modeRefused++;
                    log = a.LoggedWaits.Add("mode:" + who);
                }
                if (log)
                    Log(host, $"[Pace] {who}: change to {ModeName(mode)} mode held ({why}): {Describe(a.Kind, a.Label)} sent {NowMs() - a.SentMs} ms ago by {a.Who} has no answer yet (a mode change now would cancel it on the server)");
                return false;
            }
            if (GestureHolds(host, "mode:" + who, HealNeed.None, out _))
            {
                lock (Gate) _modeRefused++;
                return false;
            }
        }
        int seq = UseDoneSeqNow(host);
        if (host.ChangeCombatMode(mode))
            Begin(host, PacedKind.CombatMode, 0, ModeName(mode), who, seq, mode);
        return true;
    }
}
