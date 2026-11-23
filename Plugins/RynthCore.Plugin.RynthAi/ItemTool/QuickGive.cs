// ============================================================================
//  QuickGive.cs - the Item Tool's "Quick give".
//
//  Tom picks an item in his pack and a target (a player or an NPC) in the world, presses Give, and
//  every matching item in the packs is handed to that target, one give after another, until none
//  is left or Stop. A pure state machine over IQuickGiveEnv (the plugin implements it,
//  RynthAiPlugin.ItemTool.cs; the host tests fake it).
//
//  This is NOT the profile give (actiontrygiveprofile, ItemGiver\ files, /ra ig): that one is
//  locked to NPCs and a folder because a shared meta could run it. Quick give shares nothing with
//  it but AC's give action itself (GiveObjectTo, one whole object per call), and it only starts
//  from the Item Tool panel's button or a /ra itemtool command Tom types (the plugin checks the
//  source before Start).
//
//  Pacing: one give in flight at a time. The next goes after the last LANDED (the item left the
//  pack, or its stack shrank) plus PaceMs; a give with no answer in ConfirmMs, or one the server
//  refused (WeenieError / InventoryServerSaveFailed), counts as a refusal and the item is left out
//  for the rest of the run. MaxRefusals in a row stop the run.
//
//  Items are matched by exactly what Tom picked: the same weenie class id (when the engine can
//  read it) and the same name. Nothing else is ever given.
//
//  Amount and Every (2026-11-15, Silentkelpie: "add in a option to give x number of a item and a
//  timer of how often every x seconds"). Amount is how many units a round gives (0 = all of them);
//  a stack bigger than what is left of the round is split (AC's give with an amount). Every is the
//  seconds from the end of one round to the start of the next (0 = one round, as before). With Every
//  set the run goes on until Stop: having none of the item, the target out of range or a fight only
//  skip to the next round; a changed target, refusals or a client that won't send still end it.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.ItemTool;

/// <summary>What the picked item is: its weenie class id (0 = unreadable) and its exact name.</summary>
internal readonly record struct GiveItemKey(uint Wcid, string Name);

internal interface IQuickGiveEnv
{
    long Now { get; }
    /// <summary>The target is in the object cache: its current name and distance in metres (-1 unknown).</summary>
    bool TargetLoaded(int targetId, out string name, out double meters);
    bool InCombat();
    /// <summary>Why a give can't go out now (the client is busy, the server owes an answer); null = free.</summary>
    string? HoldReason();
    /// <summary>The next carried (never worn) item matching <paramref name="key"/> that is not in <paramref name="skip"/>; <paramref name="count"/> is its stack size.</summary>
    bool NextItem(GiveItemKey key, ISet<int> skip, out int itemId, out int count);
    /// <summary>How many matching items are still carried (whole objects).</summary>
    int CountCarried(GiveItemKey key);
    /// <summary>The item left the pack, or its stack shrank below <paramref name="count"/>.</summary>
    bool Landed(int itemId, int count);
    /// <summary>AC's give action: <paramref name="amount"/> units of the stack, 0 = the whole object. False when the engine refused to send it.</summary>
    bool Send(int itemId, int targetId, int amount);
    /// <summary>A count of the server's refusals so far (rises when one arrives).</summary>
    int RefusalSeq();
    void Say(string text);
    void Log(string text);
}

internal sealed class QuickGive
{
    public enum Phase { Idle, Pending, InFlight, Waiting }

    /// <summary>The target must be this near to start; it ends the run if it gets farther than <see cref="StopMeters"/>.</summary>
    public const double StartMeters = 10, StopMeters = 15;
    public const long PaceMs = 350, ConfirmMs = 6000, RefuseBackoffMs = 1000, HoldMaxMs = 60_000, GoneMs = 1500;
    public const int MaxRefusals = 3, MaxSendFails = 5, MaxGives = 2000;
    /// <summary>The largest Amount (units a round) and Every (seconds between rounds).</summary>
    public const int MaxAmount = 1_000_000, MaxEverySec = 86_400;
    private const long ThinkMs = 50;

    private readonly IQuickGiveEnv _env;
    private readonly HashSet<int> _skip = new();

    public QuickGive(IQuickGiveEnv env) => _env = env;

    public int ItemId { get; private set; }
    public GiveItemKey Item { get; private set; }
    public int TargetId { get; private set; }
    public string TargetName { get; private set; } = "";
    public Phase State { get; private set; } = Phase.Idle;
    public bool Running => State != Phase.Idle;
    /// <summary>Items (whole objects) the server took this run, and the units in them.</summary>
    public int Given { get; private set; }
    public int Units { get; private set; }
    public string LastResult { get; private set; } = "";
    /// <summary>Units a round gives; 0 = every one carried. Changed any time; a running round uses the new value.</summary>
    public int Amount { get; private set; }
    /// <summary>Seconds from the end of a round to the start of the next; 0 = one round.</summary>
    public int EverySec { get; private set; }
    /// <summary>Rounds started this run, and the units given in the current one.</summary>
    public int Rounds { get; private set; }
    public int RoundUnits { get; private set; }
    /// <summary>Waiting: milliseconds until the next round (0 when not waiting).</summary>
    public long NextRoundInMs => State == Phase.Waiting ? Math.Max(0, _roundAt - _env.Now) : 0;

    public void SetAmount(int units) => Amount = Math.Clamp(units, 0, MaxAmount);
    public void SetEvery(int seconds) => EverySec = Math.Clamp(seconds, 0, MaxEverySec);
    private bool Repeats => EverySec > 0;

    private long _lastThink, _nextAt, _sentAt, _goneSince, _holdSince, _leftAt;
    private int _inFlightId, _inFlightCount, _inFlightUnits, _refusalSeqAtSend, _refusals, _sendFails, _left, _roundGives;
    private long _roundAt;
    private string _waitWhy = "";
    private string _holdWhy = "";

    public void SetItem(int id, GiveItemKey key) { if (!Running) { ItemId = id; Item = key; } }
    public void SetTarget(int id, string name) { if (!Running) { TargetId = id; TargetName = name; } }
    public void Clear() { if (!Running) { ItemId = 0; Item = default; TargetId = 0; TargetName = ""; LastResult = ""; } }

    public bool Start(out string why)
    {
        why = "";
        if (Running) { why = "Quick give is already running"; return false; }
        if (ItemId == 0 || Item.Name.Length == 0) { why = "pick an item first (select it in your pack, then 'Use selected as item')"; return Refuse(why); }
        if (TargetId == 0) { why = "pick a target first (select the player or NPC in the world, then 'Use selected as target')"; return Refuse(why); }
        if (!_env.TargetLoaded(TargetId, out string name, out double meters)) { why = $"{TargetName} isn't loaded (out of view?)"; return Refuse(why); }
        if (!string.Equals(name, TargetName, StringComparison.OrdinalIgnoreCase)) { why = $"the target changed ('{TargetName}' is now '{name}'); pick it again"; return Refuse(why); }
        if (meters < 0 || meters > StartMeters) { why = $"{TargetName} is {(meters < 0 ? "too far" : meters.ToString("0") + " m away")} (give range is {StartMeters:0} m); walk closer"; return Refuse(why); }
        if (_env.InCombat()) { why = "you are in a fight"; return Refuse(why); }
        int have = _env.CountCarried(Item);
        if (have == 0 && !Repeats) { why = $"no '{Item.Name}' in your packs"; return Refuse(why); }

        Given = 0; Units = 0; _refusals = 0; _sendFails = 0; _goneSince = 0; _holdSince = 0; _nextAt = 0; _lastThink = 0; _left = have; _leftAt = _env.Now;
        Rounds = 0;
        LastResult = "";
        StartRound();
        string what = Amount > 0 ? $"{Amount} of '{Item.Name}'" : $"every '{Item.Name}'";
        string when = Repeats ? $", then again every {EverySec} s until you stop it" : "";
        _env.Say($"[RynthAi] Quick give: giving {what} ({have} in your packs) to {TargetName}{when}. Stop: /ra itemtool givestop.");
        _env.Log($"[RynthAi] Quick give: start item '{Item.Name}' wcid 0x{Item.Wcid:X} x{have} amount {Amount} every {EverySec}s -> 0x{(uint)TargetId:X8} '{TargetName}' ({meters:0.0} m)");
        return true;
    }

    private void StartRound()
    {
        Rounds++;
        RoundUnits = 0;
        _roundGives = 0;
        _refusals = 0;
        _holdSince = 0;
        _holdWhy = "";
        _waitWhy = "";
        _skip.Clear();
        State = Phase.Pending;
    }

    /// <summary>The round is over: with Every set, the next one starts EverySec later; without, the run ends.</summary>
    private void EndRound(string why)
    {
        if (!Repeats) { Finish(why); return; }
        _inFlightId = 0;
        _roundAt = _env.Now + EverySec * 1000L;
        _waitWhy = "";
        State = Phase.Waiting;
        _env.Log($"[RynthAi] Quick give: round {Rounds} over - {why}; {RoundUnits} unit(s) this round; next in {EverySec} s");
        _env.Say($"[RynthAi] Quick give: round {Rounds} - {why} ({RoundUnits} unit(s)). Next round in {EverySec} s.");
    }

    private bool Refuse(string why)
    {
        _env.Say($"[RynthAi] Quick give: can't start - {why}.");
        return false;
    }

    public void Stop(string why = "stopped")
    {
        if (!Running) { _env.Say("[RynthAi] Quick give: not running."); return; }
        Finish(why);
    }

    private void Finish(string why)
    {
        State = Phase.Idle;
        _inFlightId = 0;
        LastResult = $"stopped: {why} ({Given} item(s), {Units} unit(s) given to {TargetName})";
        _env.Say($"[RynthAi] Quick give {LastResult}.");
        _env.Log($"[RynthAi] Quick give: finish - {why}; gave {Given} ({Units} units)");
    }

    public string Status
    {
        get
        {
            switch (State)
            {
                case Phase.Idle: return LastResult.Length > 0 ? $"Quick give {LastResult}." : "Idle.";
                case Phase.Waiting:
                    string wait = _waitWhy.Length > 0 ? $" (then waits: {_waitWhy})" : "";
                    return $"'{Item.Name}' to {TargetName}: {Units} unit(s) in {Rounds} round(s); next round in {(NextRoundInMs + 999) / 1000} s{wait}, {_left} in your packs.";
                default:
                    string hold = _holdWhy.Length > 0 ? $" (waiting: {_holdWhy})" : "";
                    string round = Amount > 0 ? $" Round {Rounds}: {RoundUnits} of {Amount}." : Repeats ? $" Round {Rounds}." : "";
                    return $"Giving '{Item.Name}' to {TargetName}: {Given} given ({Units} units), {_left} left{hold}.{round}";
            }
        }
    }

    public void Tick()
    {
        if (State == Phase.Idle) return;
        long now = _env.Now;
        if (now - _lastThink < ThinkMs) return;
        _lastThink = now;

        if (now - _leftAt >= 500) { _left = _env.CountCarried(Item); _leftAt = now; }
        if (State == Phase.Waiting) { TickWaiting(now); return; }

        // The target must stay the one Tom picked, loaded and near.
        if (!_env.TargetLoaded(TargetId, out string name, out double meters))
        {
            if (_goneSince == 0) _goneSince = now;
            else if (now - _goneSince >= GoneMs) EndRound($"{TargetName} is gone");
            return;
        }
        _goneSince = 0;
        if (!string.Equals(name, TargetName, StringComparison.OrdinalIgnoreCase)) { Finish($"the target changed ('{TargetName}' is now '{name}')"); return; }
        if (meters < 0 || meters > StopMeters) { EndRound($"{TargetName} is out of give range ({(meters < 0 ? "position unknown" : meters.ToString("0") + " m")})"); return; }
        if (_env.InCombat()) { EndRound("a fight started"); return; }

        if (State == Phase.InFlight) { TickInFlight(now); return; }
        TickPending(now);
    }

    /// <summary>Between rounds: the next starts when it is due and the target is back in range, out of a fight.</summary>
    private void TickWaiting(long now)
    {
        if (now < _roundAt) return;
        string? why = null;
        if (!_env.TargetLoaded(TargetId, out string name, out double meters)) why = $"{TargetName} isn't in view";
        else if (!string.Equals(name, TargetName, StringComparison.OrdinalIgnoreCase)) { Finish($"the target changed ('{TargetName}' is now '{name}')"); return; }
        else if (meters < 0 || meters > StartMeters) why = $"{TargetName} is {(meters < 0 ? "too far" : meters.ToString("0") + " m away")} (give range {StartMeters:0} m)";
        else if (_env.InCombat()) why = "you are in a fight";
        else if (_left == 0) why = $"no '{Item.Name}' in your packs";
        if (why != null)
        {
            if (_waitWhy != why) _env.Log($"[RynthAi] Quick give: round {Rounds + 1} waits - {why}");
            _waitWhy = why;
            return;
        }
        StartRound();
        _env.Log($"[RynthAi] Quick give: round {Rounds} starts ({_left} carried)");
    }

    private void TickPending(long now)
    {
        if (now < _nextAt) return;
        string? why = _env.HoldReason();
        if (why != null)
        {
            _holdWhy = why;
            if (_holdSince == 0) _holdSince = now;
            else if (now - _holdSince >= HoldMaxMs) EndRound($"waited {HoldMaxMs / 1000} s: {why}");
            return;
        }
        _holdSince = 0;
        _holdWhy = "";

        if (Amount > 0 && RoundUnits >= Amount) { EndRound($"gave {RoundUnits}"); return; }
        if (_roundGives + _refusals >= MaxGives) { EndRound($"reached {MaxGives} gives"); return; }
        if (!_env.NextItem(Item, _skip, out int id, out int count))
        {
            EndRound(RoundUnits > 0 ? "done - no more of that item" : "no more of that item to give");
            return;
        }
        // A stack bigger than what is left of the round is split: AC's give with an amount.
        int units = count;
        if (Amount > 0 && count > Amount - RoundUnits) units = Amount - RoundUnits;
        _refusalSeqAtSend = _env.RefusalSeq();
        if (!_env.Send(id, TargetId, units < count ? units : 0))
        {
            if (++_sendFails >= MaxSendFails) Finish("the client wouldn't send the give");
            else _nextAt = now + RefuseBackoffMs;
            return;
        }
        _sendFails = 0;
        _inFlightId = id;
        _inFlightCount = count;
        _inFlightUnits = units;
        _sentAt = now;
        State = Phase.InFlight;
    }

    private void TickInFlight(long now)
    {
        if (_env.Landed(_inFlightId, _inFlightCount))
        {
            Given++;
            _roundGives++;
            Units += _inFlightUnits;
            RoundUnits += _inFlightUnits;
            if (_inFlightUnits >= _inFlightCount)
            {
                _skip.Add(_inFlightId);   // it is the target's now, whatever a stale inventory read still says
                _left = Math.Max(0, _left - 1);
            }
            _refusals = 0;
            State = Phase.Pending;
            _nextAt = now + PaceMs;
            return;
        }
        if (_env.RefusalSeq() != _refusalSeqAtSend) { Refused(now, "the server refused it"); return; }
        if (now - _sentAt >= ConfirmMs) Refused(now, $"no answer in {ConfirmMs / 1000} s");
    }

    private void Refused(long now, string why)
    {
        _skip.Add(_inFlightId);
        _env.Log($"[RynthAi] Quick give: 0x{(uint)_inFlightId:X8} not given - {why} ({_refusals + 1} of {MaxRefusals} in a row)");
        _inFlightId = 0;
        if (++_refusals >= MaxRefusals)
        {
            Finish($"{MaxRefusals} refusals in a row (last: {why})");
            return;
        }
        State = Phase.Pending;
        _nextAt = now + RefuseBackoffMs;
    }
}
