using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Combat's weapon swaps, one at a time, confirmed, bounded, and never back and forth. Pure:
/// the caller reads what's in hand and the server's replies and passes them in with the time.
///
///   * One swap in flight: after <see cref="Sent"/> nothing else is sent until the weapon is
///     seen in hand (confirmed), the server refuses (UseDone with an error, or a WeenieError:
///     <see cref="Refused"/>), or <see cref="ResolveMs"/> passes.
///   * Retry cap: a weapon that doesn't reach the hand after <see cref="MaxTries"/> sends is
///     rested for <see cref="GiveUpMs"/>; combat carries on with what's in hand (logged once).
///   * No flip-flop: having swapped A → B, swapping back to A within <see cref="MinDwellMs"/>
///     for the same fight (the same <c>forKey</c>, the target) is held, and so is any swap once
///     <see cref="MaxSwapsPerWindow"/> landed inside <see cref="SwapWindowMs"/>. Combat keeps
///     fighting with B. A NEW target that wants A back gets it at once (2026-10-12, Drakkon:
///     Lightning Quadrelle for a Giant Maggot, then the next target, a Grub weak to Bludgeon,
///     was fought with the Quadrelle for 5-10 s until the 20 s dwell from the last swap ran out
///     - "he attacks for about 5 seconds with the wrong element weapon"). The window cap still
///     bounds a fight that alternates between two kinds of monster.
///   * A swap counts as landed when its weapon reaches the hand, even if the choice moved on
///     while it was in flight.
/// </summary>
internal sealed class WeaponSwapTracker
{
    public enum Step
    {
        /// <summary>The chosen weapon is in hand (or none is wanted): fight.</summary>
        InHand,
        /// <summary>A swap is in flight: hold the attack.</summary>
        Wait,
        /// <summary>Send the wield now (then call <see cref="Sent"/>).</summary>
        Swap,
        /// <summary>Don't swap: fight with what's in hand.</summary>
        Hold,
    }

    public int MaxTries { get; set; } = 3;
    public double ResolveMs { get; set; } = 2500;
    public double GiveUpMs { get; set; } = 120_000;
    public double MinDwellMs { get; set; } = 20_000;
    public int MaxSwapsPerWindow { get; set; } = 4;
    public double SwapWindowMs { get; set; } = 60_000;

    private int _pendingId, _pendingFrom;
    private DateTime _pendingAt;
    private readonly Dictionary<int, int> _tries = new();
    private readonly Dictionary<int, DateTime> _restUntil = new();
    private readonly List<DateTime> _landed = new();
    private int _lastFrom, _lastTo;
    private DateTime _lastLandedAt = DateTime.MinValue;
    private int _askKey, _pendingKey, _lastKey;   // the fight (target id) each swap was for; 0 = unknown

    /// <summary>The weapon a swap is in flight for (0 = none).</summary>
    public int PendingId => _pendingId;
    /// <summary>Wields sent for <paramref name="id"/> since it was last in hand.</summary>
    public int TriesFor(int id) => _tries.TryGetValue(id, out int t) ? t : 0;
    /// <summary>Why the last Hold happened, for the log ("" = none).</summary>
    public string HoldReason { get; private set; } = "";
    /// <summary>A line to log once (a give-up), then cleared by the caller reading it.</summary>
    public string? Notice { get; set; }

    /// <summary>
    /// What to do about <paramref name="chosenId"/> with <paramref name="inHandId"/> in hand
    /// (0 = nothing readable in hand). <paramref name="forKey"/> is the fight the weapon is
    /// chosen for (the target id; 0 = unknown, treated as the same fight as the last swap).
    /// </summary>
    public Step Next(int chosenId, int inHandId, DateTime now, Func<int, string>? nameOf = null, int forKey = 0)
    {
        HoldReason = "";
        _askKey = forKey;
        // The weapon in flight reached the hand although the choice has moved on (a new target
        // wants another one). It still landed: without this the swap timed out as "not seen in
        // hand", the A -> B landing was never recorded, and the swap straight back to A wasn't
        // held (Fire -> Piercing -> Fire crossbow 2.5 s apart, 2026-10-14 11:41:06-08).
        if (_pendingId != 0 && inHandId == _pendingId && inHandId != chosenId)
        {
            _tries.Remove(_pendingId);
            Land(now);
        }
        if (chosenId == 0) return Step.InHand;

        if (inHandId == chosenId)
        {
            if (_pendingId == chosenId) Land(now);
            _tries.Remove(chosenId);
            return Step.InHand;
        }

        if (_pendingId != 0)
        {
            if ((now - _pendingAt).TotalMilliseconds < ResolveMs) return Step.Wait;
            Fail(now, "not seen in hand", nameOf);
        }

        if (_restUntil.TryGetValue(chosenId, out DateTime rest) && now < rest)
        {
            HoldReason = "resting after failed tries";
            return Step.Hold;
        }

        if (inHandId != 0)
        {
            bool sameFight = forKey == 0 || _lastKey == 0 || forKey == _lastKey;
            if (sameFight && chosenId == _lastFrom && inHandId == _lastTo && (now - _lastLandedAt).TotalMilliseconds < MinDwellMs)
            {
                HoldReason = "just swapped away from it";
                return Step.Hold;
            }
            _landed.RemoveAll(t => (now - t).TotalMilliseconds > SwapWindowMs);
            if (_landed.Count >= MaxSwapsPerWindow)
            {
                HoldReason = $"{_landed.Count} swaps in {SwapWindowMs / 1000:0}s";
                return Step.Hold;
            }
        }
        return Step.Swap;
    }

    /// <summary>A wield of <paramref name="id"/> was sent with <paramref name="fromId"/> in hand.</summary>
    public void Sent(int id, int fromId, DateTime now)
    {
        _pendingKey = _askKey;   // the fight the last Next was asked about
        _pendingId = id;
        _pendingFrom = fromId;
        _pendingAt = now;
        _tries[id] = (_tries.TryGetValue(id, out int t) ? t : 0) + 1;
    }

    /// <summary>The server refused the action in flight (UseDone error / WeenieError).</summary>
    public void Refused(DateTime now, Func<int, string>? nameOf = null)
    {
        if (_pendingId != 0) Fail(now, "refused by the server", nameOf);
    }

    /// <summary>The chosen weapon was seen in hand some other way (the wield-location read).</summary>
    public void Confirmed(int id, DateTime now)
    {
        if (_pendingId == id) Land(now);
        _tries.Remove(id);
    }

    public void Reset()
    {
        _pendingId = _pendingFrom = 0;
        _tries.Clear();
        _restUntil.Clear();
        _landed.Clear();
        _lastFrom = _lastTo = 0;
        _lastLandedAt = DateTime.MinValue;
        _askKey = _pendingKey = _lastKey = 0;
        HoldReason = "";
        Notice = null;
    }

    private void Land(DateTime now)
    {
        if (_pendingFrom != 0 && _pendingFrom != _pendingId)
        {
            _lastFrom = _pendingFrom;
            _lastTo = _pendingId;
            _lastKey = _pendingKey;
            _lastLandedAt = now;
            _landed.Add(now);
        }
        _pendingId = _pendingFrom = 0;
    }

    private void Fail(DateTime now, string why, Func<int, string>? nameOf)
    {
        int id = _pendingId;
        _pendingId = _pendingFrom = 0;
        if (!_tries.TryGetValue(id, out int tries) || tries < MaxTries) return;
        _tries.Remove(id);
        _restUntil[id] = now.AddMilliseconds(GiveUpMs);
        string name = nameOf?.Invoke(id) ?? "";
        Notice = $"0x{(uint)id:X8} '{name}' didn't wield after {MaxTries} tries ({why}) — fighting with what's in hand for {GiveUpMs / 60000:0.#} min";
    }
}
