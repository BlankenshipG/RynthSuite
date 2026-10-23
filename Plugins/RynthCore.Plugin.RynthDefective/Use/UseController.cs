using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;

namespace RynthCore.Plugin.RynthDefective.Use;

/// <summary>
/// What the controller needs from the game. The host implements it (HostDefWorld); the tests
/// script it. Plugin pump thread only.
/// </summary>
internal interface IDefWorld
{
    long NowMs { get; }
    /// <summary>A character is in the world and not in portal space.</summary>
    bool InWorld { get; }
    uint PlayerId { get; }
    uint SelectedId { get; }
    /// <summary>The client's busy count is above zero (an item action it sent has not been answered).</summary>
    bool ClientBusy { get; }
    /// <summary>A cast or use gesture is animating.</summary>
    bool Animating { get; }
    /// <summary>RynthAi is waiting on the server for an action it sent (null = RynthAi isn't there).</summary>
    bool? BotWaitingOnServer { get; }
    /// <summary>RynthAi's macro is running (null = RynthAi isn't there).</summary>
    bool? BotMacroRunning { get; }
    /// <summary>Count of the server's UseDone events; 0 when the engine can't report them.</summary>
    int UseDoneSeq { get; }
    /// <summary>1 NonCombat, 2 Melee, 4 Missile, 8 Magic.</summary>
    int CombatMode { get; }
    /// <summary>The server's refusals so far (0x028A / 0x00A0 ... count), to tell a new one.</summary>
    int RefusalSeq { get; }
    /// <summary>The newest refusal named this object (a refused wield: InventoryServerSaveFailed).</summary>
    bool RefusalNamed(uint objectId);

    /// <summary>Everything carried and worn, as the client has it now.</summary>
    IReadOnlyList<CarriedItem> Items();
    /// <summary>The item as the client has it this instant (wielded or not); null if it is gone.</summary>
    CarriedItem? Live(uint id);

    bool UseObject(uint id);
    bool UseObjectOn(uint id, uint target);
    bool SetCombatMode(int mode);
}

/// <summary>One thing the player asked to use: an exact item (a slot's) or a name typed in a command.</summary>
internal sealed record UseRequest(ItemRef? Item, string? Name, UseMode Mode, string Source, long AtMs)
{
    /// <summary>Same item, same way: pressing the key again must not queue a second one.</summary>
    public bool SameAs(UseRequest o) =>
        Mode == o.Mode
        && ((Item != null && o.Item != null && Item.SameAs(o.Item))
            || (Name != null && o.Name != null && string.Equals(Name, o.Name, StringComparison.OrdinalIgnoreCase)));

    public string Label => Item?.FullName ?? Name ?? "item";
}

/// <summary>The combat stance a weapon swap leaves the character in.</summary>
internal static class Stance
{
    public const int NonCombat = 1, Melee = 2, Missile = 4, Magic = 8;

    /// <summary>
    /// The stance to ask for after <paramref name="weapon"/> was wielded, 0 = leave it alone.
    /// A character at peace stays at peace. One in a fighting stance goes to the new weapon's:
    /// Melee, Missile (a launcher needs its ammunition in hand: without it the client refuses the
    /// stance, so the character goes to peace rather than being left in a stance the weapon can't
    /// use), or Magic for a caster. Shields, armor and ammunition keep the stance they are in.
    /// </summary>
    public static int Wanted(ItemKind kind, bool launcherNeedsAmmo, bool ammoInHand, int modeBefore)
    {
        if (modeBefore == NonCombat || modeBefore == 0) return 0;
        switch (kind)
        {
            case ItemKind.Melee: return Melee;
            case ItemKind.WarWand:
            case ItemKind.VoidWand: return Magic;
            case ItemKind.Missile: return launcherNeedsAmmo && !ammoInHand ? NonCombat : Missile;
            default: return 0;
        }
    }

    public static string Name(int mode) => mode switch
    {
        NonCombat => "peace",
        Melee => "melee",
        Missile => "missile",
        Magic => "magic",
        _ => "mode " + mode,
    };
}

/// <summary>
/// One use at a time. A request (a slot pressed, /rdef use, the picker) is held until the
/// character is free, then sent; the next waits for the server's answer. A second request while one
/// is waiting replaces it (the newest wish wins) unless it is the same one (a key held down does
/// not queue twice). Nothing is retried in a loop: a refused or unanswered action is reported once.
///
/// What is sent (the host calls RynthAi's own one-touch heal and weapon swap use):
///   kits         UseObjectOn(kit, you)                      answered by the server's UseDone
///   consumables  UseObject(item)                            answered by UseDone
///   weapons etc. UseObject(item): the client's double-click, which takes what is in the way off;
///                answered when the item reads wielded (a wield sends no UseDone), then the stance
///                is put right (<see cref="Stance.Wanted"/>) once the character is still.
/// Busy: nothing is sent while the client's busy count is up, a gesture animates, RynthAi waits on
/// the server, the last action is unanswered, or for <see cref="MinGapMs"/> after it - the "one item
/// action at a time" rule RynthAi's ActionPacer keeps. Weapon swaps also wait out a running RynthAi
/// macro unless the player allowed them (it would swap straight back).
/// </summary>
internal sealed class UseController
{
    /// <summary>How long a request waits for the character to be free before it gives up and says why.</summary>
    public const long WaitLimitMs = 6000;
    /// <summary>A use with no UseDone by now is let go (RynthAi waits 8 s for a kit's result).</summary>
    public const long UseTimeoutMs = 8000;
    /// <summary>A use the client no longer holds busy, with no UseDone, is let go after this.</summary>
    public const long UseQuietMs = 1500;
    public const long WieldTimeoutMs = 3500;
    public const long MinGapMs = 250;
    public const long StanceSettleMs = 500;
    public const long StanceTimeoutMs = 4000;

    private enum Stage { Sent, WaitWield, Stance }

    private sealed class Flight
    {
        public UseRequest Request = null!;
        public CarriedItem Item = null!;
        public UseAction Action;
        public ItemKind Kind;
        public long SentMs;
        public int UseDoneAtSend, RefusalAtSend, ModeBefore;
        public Stage Stage;
        public long StageMs;
    }

    private UseRequest? _pending;
    private Flight? _flight;
    private long _lastDoneMs = long.MinValue / 4;
    private string _lastWait = string.Empty;

    /// <summary>Plain information ("Using Peerless Healing Kit ..."): the status line, not the chat.</summary>
    public Action<string>? Say;
    /// <summary>Something went wrong or didn't happen: the status line and the chat.</summary>
    public Action<string>? Warn;
    public Action<string>? Log;
    /// <summary>A weapon was wielded (the picker's recent list).</summary>
    public Action<CarriedItem>? Wielded;

    public bool HasPending => _pending != null;
    public bool InFlight => _flight != null;
    public UseRequest? Pending => _pending;

    /// <summary>The item a request in flight or waiting is about, for highlighting its slot.</summary>
    public bool Involves(ItemRef r) =>
        (_pending?.Item != null && _pending.Item.SameAs(r)) || (_flight?.Request.Item != null && _flight.Request.Item.SameAs(r));

    public DefSettings Settings = new();

    /// <summary>Queues a request. False when it is the one already waiting or in flight (a held key).</summary>
    public bool Request(UseRequest r)
    {
        if (_pending != null && _pending.SameAs(r)) return false;
        if (_flight != null && _flight.Action == UseAction.Equip && _flight.Request.SameAs(r)) return false;   // already wielding it
        _pending = r;
        _lastWait = string.Empty;
        return true;
    }

    /// <summary>Drops what is waiting and forgets what is in flight (logout, portal space, shutdown).</summary>
    public void Reset()
    {
        _pending = null;
        _flight = null;
        _lastWait = string.Empty;
    }

    public void Tick(IDefWorld w)
    {
        if (!w.InWorld)
        {
            if (_pending != null || _flight != null) Reset();
            return;
        }
        long now = w.NowMs;
        if (_flight != null && !AdvanceFlight(w, now)) return;   // still in flight: nothing new goes out
        if (_pending == null) return;

        UseRequest req = _pending;
        string? wait = WaitReason(w, now);
        if (wait != null)
        {
            _lastWait = wait;
            if (now - req.AtMs >= WaitLimitMs)
            {
                _pending = null;
                Warn?.Invoke($"{req.Label}: not used, {wait}.");
            }
            return;
        }
        _pending = null;
        Send(w, req, now);
    }

    private string? WaitReason(IDefWorld w, long now)
    {
        if (now - _lastDoneMs < MinGapMs) return "the last action just finished";
        if (w.ClientBusy) return "the character is busy";
        if (w.Animating) return "an animation is playing";
        if (w.BotWaitingOnServer == true) return "RynthAi is waiting on the server";
        return null;
    }

    // ── Sending ─────────────────────────────────────────────────────────────

    private void Send(IDefWorld w, UseRequest req, long now)
    {
        IReadOnlyList<CarriedItem> items = w.Items();
        CarriedItem? item = req.Item != null
            ? ItemMatcher.Find(req.Item, items, out _)
            : ItemMatcher.FindByName(req.Name ?? string.Empty, items);
        if (item == null)
        {
            Warn?.Invoke(req.Item != null
                ? $"{req.Label}: you aren't carrying one."
                : $"No item named '{req.Name}' in your pack.");
            return;
        }

        ItemKind kind = ItemKinds.Classify(item);
        UseAction action = UseModes.ActionFor(req.Mode, kind);
        string name = item.FullName;

        if (action == UseAction.Equip)
        {
            if (item.Wielded)
            {
                Say?.Invoke($"{name} is already in your hands.");
                return;
            }
            if (ItemKinds.IsWeapon(kind) && !Settings.SwapWhileBotRuns && w.BotMacroRunning == true)
            {
                Warn?.Invoke($"{name}: not wielded, RynthAi's macro is running and picks the weapon itself (stop it, or allow swaps in the Defective settings).");
                return;
            }
        }

        uint target = 0;
        if (action == UseAction.UseOnSelf) target = w.PlayerId;
        else if (action == UseAction.UseOnSelected)
        {
            target = w.SelectedId;
            if (target == 0)
            {
                Warn?.Invoke($"{name}: select a target first.");
                return;
            }
        }
        if ((action == UseAction.UseOnSelf || action == UseAction.UseOnSelected) && target == 0)
        {
            Warn?.Invoke($"{name}: the character isn't ready.");
            return;
        }

        bool sent = target != 0 ? w.UseObjectOn(item.Id, target) : w.UseObject(item.Id);
        if (!sent)
        {
            Warn?.Invoke($"{name}: the client didn't take the request (nothing was sent).");
            return;
        }

        _flight = new Flight
        {
            Request = req,
            Item = item,
            Action = action,
            Kind = kind,
            SentMs = now,
            StageMs = now,
            UseDoneAtSend = w.UseDoneSeq,
            RefusalAtSend = w.RefusalSeq,
            ModeBefore = w.CombatMode,
            Stage = action == UseAction.Equip ? Stage.WaitWield : Stage.Sent,
        };
        Log?.Invoke($"[RynthDefective] {req.Source}: {(action == UseAction.Equip ? "wield" : target != 0 ? "use on " + (target == w.PlayerId ? "you" : "0x" + target.ToString("X8")) : "use")} {item} (stack {item.Stack}, mode {Stance.Name(w.CombatMode)})");
        Say?.Invoke(action == UseAction.Equip ? $"Wielding {name}..." : $"Using {name}...");
    }

    // ── In flight ───────────────────────────────────────────────────────────

    /// <summary>Steps the action in flight; true when it is finished (a new one may go out this tick).</summary>
    private bool AdvanceFlight(IDefWorld w, long now)
    {
        Flight f = _flight!;
        long age = now - f.SentMs;
        switch (f.Stage)
        {
            case Stage.Sent:
            {
                bool answered = w.UseDoneSeq != f.UseDoneAtSend;
                bool quiet = age >= UseQuietMs && !w.ClientBusy && !w.Animating;
                if (answered || quiet || age >= UseTimeoutMs)
                {
                    if (!answered && age >= UseTimeoutMs) Log?.Invoke($"[RynthDefective] no UseDone for {f.Item} after {age} ms; carrying on");
                    Finish(now);
                    return true;
                }
                return false;
            }
            case Stage.WaitWield:
            {
                CarriedItem? live = w.Live(f.Item.Id);
                if (live != null && live.Wielded)
                {
                    Wielded?.Invoke(live);
                    f.Item = live;
                    f.Stage = Stage.Stance;
                    f.StageMs = now;
                    return AdvanceStance(w, f, now);
                }
                if (w.RefusalSeq != f.RefusalAtSend && w.RefusalNamed(f.Item.Id))
                {
                    Warn?.Invoke($"{f.Item.FullName}: the server wouldn't let you wield it (a skill, level or slot requirement?).");
                    Finish(now);
                    return true;
                }
                if (age >= WieldTimeoutMs)
                {
                    Warn?.Invoke($"{f.Item.FullName}: not in your hands after {age / 1000.0:0.0} s - something may be in the way, or the character was busy. Try again.");
                    Finish(now);
                    return true;
                }
                return false;
            }
            default:
                return AdvanceStance(w, f, now);
        }
    }

    private bool AdvanceStance(IDefWorld w, Flight f, long now)
    {
        bool launcher = f.Kind == ItemKind.Missile && ItemKinds.NeedsAmmo(f.Item);
        int want = Stance.Wanted(f.Kind, launcher, AmmoInHand(w), f.ModeBefore);
        if (want == 0 || w.CombatMode == want)
        {
            Say?.Invoke($"{f.Item.FullName} is in your hands.");
            Finish(now);
            return true;
        }
        long since = now - f.StageMs;
        if (since >= StanceTimeoutMs)
        {
            Warn?.Invoke($"{f.Item.FullName} is wielded but the {Stance.Name(want)} stance wasn't taken ({Stance.Name(w.CombatMode)} now).");
            Finish(now);
            return true;
        }
        if (since < StanceSettleMs || w.ClientBusy || w.Animating) return false;
        if (w.SetCombatMode(want))
        {
            Log?.Invoke($"[RynthDefective] stance {Stance.Name(w.CombatMode)} -> {Stance.Name(want)} for {f.Item}");
            Say?.Invoke(want == Stance.NonCombat && f.ModeBefore != Stance.NonCombat
                ? $"{f.Item.FullName} is in your hands; no ammunition in hand, so you are back at peace."
                : $"{f.Item.FullName} is in your hands, {Stance.Name(want)} stance.");
            Finish(now);
            return true;
        }
        return false;   // the engine turned the request away this tick (a teleport, a pending action): ask again
    }

    private static bool AmmoInHand(IDefWorld w)
    {
        foreach (CarriedItem it in w.Items())
            if (it.Wielded && ItemKinds.Classify(it) == ItemKind.Ammo) return true;
        return false;
    }

    private void Finish(long now)
    {
        _flight = null;
        _lastDoneMs = now;
    }
}
