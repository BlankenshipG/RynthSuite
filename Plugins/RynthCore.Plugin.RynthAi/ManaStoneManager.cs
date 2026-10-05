using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Mana stone tapping state machine.
///
/// Each think tick from Idle picks one of two actions:
///   1. If any worn item has CurrentMana below the threshold AND we have a
///      charged stone in inventory, use the stone on the player.
///   2. Otherwise, if we have an empty stone and an item the looter picked up
///      as "ManaTap" (looted only to be drained) still holds at least the
///      threshold of CurrentMana, drain it. Nothing else is ever drained.
///
/// Single-shot semantics:
///   - Using an empty stone on an item destroys the item and charges the stone.
///   - Using a charged stone on the player may also destroy the stone — if so
///     we just loot another next corpse.
/// </summary>
internal sealed class ManaStoneManager
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache _objectCache;

    private enum TapState { Idle, TappingItem, UsingOnPlayer }

    private TapState _state        = TapState.Idle;
    private int      _activeStoneId;
    private int      _activeItemId;
    private long     _actionIssuedAt;
    private long     _lastThinkAt;

    // After a use-on-player times out we blacklist the offending stone
    // for a while so we don't re-select it next tick and loop forever.
    private readonly Dictionary<int, long> _stoneUseOnPlayerCooldownUntil = new();
    private const long StoneUseOnPlayerCooldownMs = 5 * 60 * 1000;

    // Same idea for tap targets: an item that won't drain (wielded server-side,
    // already at 0 mana, etc.) gets shelved for 5 minutes so we move on.
    private readonly Dictionary<int, long> _tapTargetCooldownUntil = new();
    private const long TapTargetCooldownMs = 5 * 60 * 1000;

    // And for the stone half of the pair: a "stone" that AC silently refuses
    // to use (often because it's actually charged but our cache reads its
    // CurrentMana as 0/unreadable) gets parked so FindEmptyStone moves on.
    // Short cooldown — most failures are transient (server lag, mid-action),
    // and a genuinely-charged stone is filtered by the cur>0 check anyway.
    private readonly Dictionary<int, long> _stoneTapCooldownUntil = new();
    private const long StoneTapCooldownMs = 30 * 1000;

    // Stones we've positively confirmed are charged via AC chat ("is already
    // full of mana"). Distinct from the cooldown so the cache's stale CurrentMana
    // reading doesn't auto-clear them. Only cleared when the stone disappears
    // from cache (consumed via use-on-player).
    private readonly HashSet<int> _stoneKnownCharged = new();

    // Worn equipment is only treated as "needs refill" when current mana drops
    // below this fraction of its MaxMana. Prevents the bot from continually
    // dumping stones on the player just because an item dipped slightly.
    private const double WornRefillPctOfMax = 0.25;
    // With an Eternal Mana Charge (never used up) there's no reason to wait for 25%: top
    // gear up below 75% (Lucy carried one on her Items list and it was never used, 2026-09-30).
    private const double WornRefillPctOfMaxEternal = 0.75;
    private long _lastGearDiagAt;
    private readonly Dictionary<int, long> _manaIdRequestedAt = new();

    private const long ThinkIntervalMs  = 600;
    private const long ActionTimeoutMs  = 8000;

    private static long NowMs => Environment.TickCount64;

    /// <summary>
    /// Set by the plugin: true when a Keep or Keep # rule in the active loot profile
    /// matches the item. Draining destroys the item, so kept loot is never a tap target.
    /// </summary>
    public Func<WorldObject, bool>? IsLootKept { get; set; }

    // Items the looter picked up as "ManaTap" (looted only to be drained), with the
    // time each was recorded. Draining destroys the item, so these are the ONLY tap
    // targets: gear, quest items, bought items and loot kept by a rule are never in
    // here. Session memory only: a hot reload or relog builds a new manager and the
    // set starts empty, so anything looted for tapping before that just stays in
    // the pack undrained (the safe direction).
    private readonly Dictionary<int, long> _lootedForTap = new();
    // An entry is not pruned for "not in the pack" this soon after it was recorded:
    // the pickup is recorded when it is sent, before the item arrives.
    private const long LootedForTapGraceMs = 30_000;
    private const long LootedForTapPruneIntervalMs = 1_000;
    private long _lootedForTapPrunedAt;

    public ManaStoneManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache objectCache)
    {
        _host        = host;
        _settings    = settings;
        _objectCache = objectCache;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void OnHeartbeat(int busyCount)
    {
        // Before the early returns: items sold, given away or dropped while tapping is
        // off or the macro is stopped must leave the set too.
        PruneLootedForTap(NowMs);

        // An Eternal Mana Charge refills gear for free, so it works with tapping off too.
        if (!_settings.EnableManaTapping && !HasEternalCharge()) { Reset(); return; }
        if (!_settings.IsMacroRunning)    { Reset(); return; }

        long now = NowMs;

        // Positive completion check: if the stone or target item is no longer
        // in the cache, the action effectively completed (stone was destroyed
        // mid-use, item drained and destroyed). Don't wait for chat — clear
        // the state so we can issue the next action.
        if (_state == TapState.TappingItem)
        {
            bool itemGone  = _activeItemId  != 0 && _objectCache[_activeItemId]  == null;
            bool stoneGone = _activeStoneId != 0 && _objectCache[_activeStoneId] == null;
            if (itemGone || stoneGone)
            {
                _host.Log($"[RynthAi] ManaStone: tap completion detected (itemGone={itemGone} stoneGone={stoneGone}).");
                if (itemGone) _lootedForTap.Remove(_activeItemId); // drained and destroyed
                GoIdle();
            }
        }
        else if (_state == TapState.UsingOnPlayer)
        {
            if (_activeStoneId != 0 && _objectCache[_activeStoneId] == null)
            {
                _host.Log($"[RynthAi] ManaStone: use-on-player completion detected (stone destroyed).");
                Reset();
            }
        }

        // Check the action timeout BEFORE the busy-count gate. busyCount can get
        // stuck > 0 (a server-side decrement that never lands), and if we waited
        // for it we'd never recover. Without this, a single stuck use-on-player
        // blocks the entire mana stone subsystem indefinitely.
        if (_state != TapState.Idle && now - _actionIssuedAt > ActionTimeoutMs)
        {
            _host.Log($"[RynthAi] ManaStone: action timeout in state {_state} after {now - _actionIssuedAt}ms (stone=0x{(uint)_activeStoneId:X8} item=0x{(uint)_activeItemId:X8}), resetting.");

            // If a use-on-player keeps timing out with the same stone, blacklist
            // the stone so subsequent ticks don't pick it again. Otherwise we'd
            // re-select it every tick and never get to Priority 2 (drain looted).
            if (_state == TapState.UsingOnPlayer && _activeStoneId != 0)
            {
                _stoneUseOnPlayerCooldownUntil[_activeStoneId] = now + StoneUseOnPlayerCooldownMs;
                _host.Log($"[RynthAi] ManaStone: blacklisting stone 0x{(uint)_activeStoneId:X8} for use-on-player ({StoneUseOnPlayerCooldownMs / 1000}s cooldown).");
            }
            // For the tap path, the STONE is the more likely culprit when an
            // action silently fails — AC won't drain into a charged stone, so
            // a "looks empty in cache" stone that's really charged keeps
            // failing on every tap target. Park the stone, not the item.
            if (_state == TapState.TappingItem && _activeStoneId != 0)
            {
                _stoneTapCooldownUntil[_activeStoneId] = now + StoneTapCooldownMs;
                _host.Log($"[RynthAi] ManaStone: blacklisting stone 0x{(uint)_activeStoneId:X8} for tap ({StoneTapCooldownMs / 1000}s cooldown).");
            }
            // ALSO shelve the TARGET item. A target that never completes a drain
            // (server can't resolve the stone guid, item is retained/undrainable,
            // it's a casting wand, etc.) is otherwise re-paired with the next
            // empty stone on the very next think tick and loops forever — and
            // every attempt issues UseObjectOn, which cancels the player's
            // move-to chains and bumps m_cBusy, locking the character (and the
            // bot's own casts) out of movement/combat. Shelving only the stone
            // (above) does NOT break this loop because the WAND keeps getting
            // selected. See [rynthai_respawn_blind_wedge] / 2026-06-22 wedge.
            if (_state == TapState.TappingItem && _activeItemId != 0)
            {
                _tapTargetCooldownUntil[_activeItemId] = now + TapTargetCooldownMs;
                _host.Log($"[RynthAi] ManaStone: shelving tap target 0x{(uint)_activeItemId:X8} for {TapTargetCooldownMs / 1000}s (drain never completed).");
            }
            Reset();
            return; // let things settle before starting a new action this tick
        }

        if (!_host.HasUseObjectOn) return;
        if (busyCount > 0) return;

        if (now - _lastThinkAt < ThinkIntervalMs) return;
        _lastThinkAt = now;

        if (_state == TapState.Idle)
            TryBeginAction();
    }

    public void OnChatWindowText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_state == TapState.TappingItem)
        {
            // "The Mana Stone drains X points of mana from the ..." — single drain,
            // item is destroyed, stone is now charged. Back to idle so the next think
            // tick can decide whether worn gear needs the charge yet.
            if (text.Contains("The Mana Stone drains")
                || text.Contains("is destroyed")
                || text.Contains("has been destroyed"))
            {
                if (_activeItemId != 0) _lootedForTap.Remove(_activeItemId); // drained and destroyed
                GoIdle();
                return;
            }

            // "The X is already full of mana." — AC's response when we used a
            // CHARGED stone on a non-equipped item (or a fully-loaded item). Our
            // cache mis-read this stone as empty. Blacklist it for the tap path
            // immediately so the next think tick picks a different stone.
            if (text.Contains("is already full of mana") && _activeStoneId != 0)
            {
                _stoneKnownCharged.Add(_activeStoneId);
                _stoneTapCooldownUntil.Remove(_activeStoneId); // supersede cooldown
                _host.Log($"[RynthAi] ManaStone: stone 0x{(uint)_activeStoneId:X8} is actually charged (server: 'already full') — marking known-charged.");
                GoIdle();
                return;
            }
            return;
        }

        if (_state == TapState.UsingOnPlayer)
        {
            // "The Mana Stone gives X points of mana to ..." — transfer complete.
            // The stone may also have been destroyed in the process; either way
            // reset and let the next tick pick a fresh stone.
            if (text.Contains("The Mana Stone gives")
                || text.Contains("The Mana Stone is destroyed"))
            {
                Reset();
            }
        }
    }

    /// <summary>
    /// CorpseOpenController calls this when it picks an item up as "ManaTap" (no loot
    /// rule matched, it holds enough mana and an empty stone is free): when the pickup
    /// is sent, and again when it is confirmed. Only these items are ever drained.
    /// </summary>
    public void MarkLootedForTap(int itemId)
    {
        if (itemId != 0) _lootedForTap[itemId] = NowMs;
    }

    /// <summary>
    /// Drops entries whose item has left the pack: drained (destroyed), sold, given
    /// away, salvaged, dropped, or never arrived. Also drops items the player has
    /// since put on or added to the Weapons tab: that is the player choosing to keep
    /// it, so it stops being tap fodder for good.
    /// </summary>
    private void PruneLootedForTap(long now)
    {
        if (_lootedForTap.Count == 0) return;
        if (now - _lootedForTapPrunedAt < LootedForTapPruneIntervalMs) return;
        _lootedForTapPrunedAt = now;

        int playerId = unchecked((int)_host.GetPlayerId());
        List<int>? drop = null;
        foreach (var kv in _lootedForTap)
        {
            WorldObject? item = _objectCache[kv.Key];
            bool keptByPlayer = item != null
                && ((playerId != 0 && item.Wielder == playerId)
                    || WorldObjectCache.IsWieldedByPlayer(_host, item)
                    || _settings.ItemRules.Any(r => r.Id == item.Id));
            if (!keptByPlayer)
            {
                if (now - kv.Value < LootedForTapGraceMs) continue; // pickup may still be arriving
                if (item != null && IsOwnedByPlayer(item, playerId)) continue;
            }
            (drop ??= new List<int>()).Add(kv.Key);
        }
        if (drop == null) return;
        foreach (int id in drop)
            _lootedForTap.Remove(id);
        _host.Log($"[RynthAi] ManaStone: {drop.Count} looted-for-tap item(s) left the pack or were kept by the player; {_lootedForTap.Count} left to drain.");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void TryBeginAction()
    {
        int threshold = _settings.ManaTapMinMana;

        // Priority 1 (was 2): drain an item looted for tapping into an empty stone.
        // This wins over refilling worn gear because:
        //   * draining is the user's primary goal (loot-for-tap behavior),
        //   * the use-on-player path only accepts certain equipment slots and
        //     can loop forever on items like wielded weapons that AC won't
        //     refill via stone — that loop blacklists stones one by one and
        //     starves the drain path entirely.
        int stoneId  = _settings.EnableManaTapping ? FindEmptyStone() : 0;
        int targetId = stoneId != 0 ? FindTapTarget(threshold) : 0;

        if (stoneId != 0 && targetId != 0)
        {
            _activeStoneId = stoneId;
            _activeItemId  = targetId;
            _host.Log($"[RynthAi] ManaStone: draining 0x{(uint)targetId:X8} with stone 0x{(uint)stoneId:X8}");
            IssueTapItem(stoneId, targetId);
            return;
        }

        // Priority 2 (was 1): refill worn gear with a charged stone, but only
        // when there is nothing to drain right now.
        if (WornEquipmentNeedsMana(threshold))
        {
            int chargedStone = FindChargedStone();
            if (chargedStone != 0)
            {
                _activeStoneId = chargedStone;
                _activeItemId  = 0;
                IssueUseOnPlayer();
                return;
            }
        }

    }

    private void IssueTapItem(int stoneId, int itemId)
    {
        _state           = TapState.TappingItem;
        _actionIssuedAt  = NowMs;
        _host.UseOnFor(unchecked((uint)stoneId), unchecked((uint)itemId), "ManaStone", "tap an item into a mana stone");
    }

    private void IssueUseOnPlayer()
    {
        uint playerId = _host.GetPlayerId();
        if (playerId == 0) { Reset(); return; }

        // Confirm the stone still exists in inventory
        if (_objectCache[_activeStoneId] == null) { Reset(); return; }

        _host.Log($"[RynthAi] ManaStone: using charged stone 0x{(uint)_activeStoneId:X8} on player");
        _state          = TapState.UsingOnPlayer;
        _actionIssuedAt = NowMs;
        _host.UseOnFor(unchecked((uint)_activeStoneId), playerId, "ManaStone", "charged mana stone on yourself");
    }

    private void GoIdle()
    {
        _state          = TapState.Idle;
        _actionIssuedAt = 0;
    }

    private void Reset()
    {
        _state          = TapState.Idle;
        _activeStoneId  = 0;
        _activeItemId   = 0;
        _actionIssuedAt = 0;
    }

    /// <summary>
    /// True if at least one configured mana stone in the player's inventory has zero charge.
    /// CorpseOpenController uses this to decide whether to loot mana-bearing items for draining.
    /// </summary>
    public bool HasEmptyManaStone() => FindEmptyStone() != 0;

    /// <summary>
    /// Number of empty (zero-charge) configured mana stones currently in inventory.
    /// </summary>
    public int CountEmptyStones()
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;
        int count = 0;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (!IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;
            if (_stoneKnownCharged.Contains(item.Id)) continue;

            bool readMana = _host.TryGetObjectIntProperty(unchecked((uint)item.Id),
                                                          (uint)AcIntProperty.CurrentMana, out int cur);
            if (readMana && cur == 0)
                _stoneTapCooldownUntil.Remove(item.Id);

            if (_stoneTapCooldownUntil.TryGetValue(item.Id, out long until) && now < until) continue;
            if (readMana && cur > 0) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Items looted for tapping that are in the player's inventory and match the
    /// tap-target criteria (mana ≥ threshold, not equipped, not on the Weapons tab,
    /// not loot-kept, not blacklisted). Each one
    /// will eventually consume one empty stone, so they count against the
    /// "free slots" quota that CorpseOpenController uses for ManaTap pickup.
    /// </summary>
    public int CountPendingDrainItems()
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        int threshold = _settings.ManaTapMinMana;
        if (threshold <= 0) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;
        int count = 0;

        foreach (int id in _lootedForTap.Keys) // keep in sync with FindTapTarget
        {
            WorldObject? item = _objectCache[id];
            if (item == null) continue;
            if (IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;
            if (IsWandLike(item)) continue; // wands are never tap targets — keep this in sync with FindTapTarget
            if (_tapTargetCooldownUntil.TryGetValue(item.Id, out long until) && now < until) continue;
            if (_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.WieldedSlot, out int wield)
                && wield != 0) continue;
            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int curMana)
                || curMana < threshold) continue;
            if (IsProtectedFromTap(item, playerId)) continue; // keep in sync with FindTapTarget
            count++;
        }
        return count;
    }

    /// <summary>
    /// Items a drain must never touch (the drain destroys the item): gear the player
    /// is wearing or wielding, weapons and wands on the Weapons tab (the bow sits in
    /// the pack while the wand buffs), and loot the profile keeps. Stype 10 can read 0
    /// for a wielded item, so worn gear is also checked by its wielder.
    /// </summary>
    private bool IsProtectedFromTap(WorldObject item, int playerId)
    {
        if (playerId != 0 && item.Wielder == playerId) return true;
        if (WorldObjectCache.IsWieldedByPlayer(_host, item)) return true;
        foreach (var rule in _settings.ItemRules)
            if (rule.Id == item.Id) return true;
        var kept = IsLootKept;
        if (kept != null)
        {
            try { if (kept(item)) return true; }
            catch { return true; }
        }
        return false;
    }

    /// <summary>
    /// Free empty-stone slots = empty stones in inventory minus drain candidates
    /// already in inventory waiting to be tapped. CorpseOpenController only
    /// looks for new ManaTap pickups when this is &gt; 0.
    /// </summary>
    public int FreeEmptyStoneSlots() => Math.Max(0, CountEmptyStones() - CountPendingDrainItems());

    /// <summary>
    /// True when the item is a mana stone.
    ///   * If the user configured one or more ManaStone consumable rules, only
    ///     stones whose name matches a rule qualify (explicit opt-in to specific
    ///     stones — the user narrowed it on purpose).
    ///   * If there are no ManaStone rules and tapping is enabled, an item
    ///     qualifies if it's classified ObjectClass.ManaStone OR named
    ///     "Mana Stone" — so looting and tapping work with zero configuration.
    ///     The name fallback is load-bearing: items still on a corpse are
    ///     usually unappraised, AC hasn't sent ITEM_TYPE, so the cache can only
    ///     classify them Unknown. Name survives where ITEM_TYPE doesn't.
    /// CorpseOpenController shares this predicate for both the loot gate and the
    /// KeepCount inventory tally — they must agree or the cap stops working.
    /// </summary>
    public bool IsManaStone(WorldObject item)
    {
        if (IsEternal(item)) return true;
        bool hasRule = false;
        foreach (var rule in _settings.ConsumableRules)
        {
            if (!rule.Type.Equals("ManaStone", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(rule.Name)) continue;
            hasRule = true;
            if (rule.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (hasRule) return false;                      // explicit rules → match by name only
        if (!_settings.EnableManaTapping) return false; // no rules → auto-detect only when tapping is on

        if (item.ObjectClass == AcObjectClass.ManaStone) return true;
        return !string.IsNullOrEmpty(item.Name)
               && (item.Name.Contains("Mana Stone", StringComparison.OrdinalIgnoreCase)
                   || item.Name.Contains("Mana Charge", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An unlimited mana charge (Eternal Mana Charge: 10,000 mana, never used up). Always
    /// "charged", never an empty stone to drain into, and preferred for refilling gear.
    /// </summary>
    private static bool IsEternal(WorldObject item) =>
        !string.IsNullOrEmpty(item.Name) && item.Name.StartsWith("Eternal Mana", StringComparison.OrdinalIgnoreCase);

    private bool HasEternalCharge()
    {
        int playerId = unchecked((int)_host.GetPlayerId());
        foreach (var item in _objectCache.GetDirectInventory(forceRefresh: false))
            if (IsEternal(item) && IsOwnedByPlayer(item, playerId)) return true;
        return false;
    }

    /// <summary>True if the item's container chain roots at the player's pack.</summary>
    // What the player owns, rebuilt at most every 600 ms: the pack walk (GetContainerContents,
    // what kits and potions already use) plus the cache's inventory set (wielded items too).
    // The per-object container/wielder fields below read 0 off AC's thread, so every item
    // looked like it lay on the ground: gear-check logged worn=0 and eternal=False for a
    // character wearing gear with an Eternal Mana Charge in the pack (2026-09-30).
    private readonly HashSet<int> _ownedIds = new();
    private long _ownedIdsAt;

    private HashSet<int> OwnedIds()
    {
        long now = NowMs;
        if (now - _ownedIdsAt < ThinkIntervalMs && _ownedIds.Count > 0) return _ownedIds;
        _ownedIdsAt = now;
        _ownedIds.Clear();
        foreach (var wo in _objectCache.GetDirectInventory(forceRefresh: false)) _ownedIds.Add(wo.Id);
        foreach (var wo in _objectCache.GetInventory()) _ownedIds.Add(wo.Id);
        return _ownedIds;
    }

    private bool IsOwnedByPlayer(WorldObject item, int playerId)
    {
        if (item.ObjectClass == AcObjectClass.Corpse) return false;
        if (OwnedIds().Contains(item.Id)) return true;
        if (item.Wielder != 0 && playerId != 0 && item.Wielder != playerId) return false;
        // In no container and wielded by nobody = lying on the ground. Those passed as
        // owned (the scans walk every known object), so a Mana Stone on a dungeon floor
        // became the "empty stone": the tap could never complete, timed out after 8 s
        // and shelved the real tap target for 5 minutes. Only when the engine can read
        // ownership; without it every object reads 0 and the old behaviour stays.
        if (item.Container == 0 && item.Wielder == 0 && playerId != 0 && _host.HasGetObjectOwnershipInfo)
            return false;
        if (item.Container != 0 && playerId != 0 && item.Container != playerId)
        {
            var owner = _objectCache[item.Container];
            if (owner == null || owner.ObjectClass == AcObjectClass.Corpse) return false;
            if (owner.Wielder != playerId && owner.Container != playerId) return false;
        }
        return true;
    }

    /// <summary>Find a stone with no charge (CurrentMana == 0) — only those can drain an item.</summary>
    private int FindEmptyStone()
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (!IsManaStone(item) || IsEternal(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;

            // Stones AC has explicitly told us are charged ("already full of
            // mana") are never empty until they're consumed/destroyed.
            if (_stoneKnownCharged.Contains(item.Id)) continue;

            bool readMana = _host.TryGetObjectIntProperty(unchecked((uint)item.Id),
                                                          (uint)AcIntProperty.CurrentMana, out int curMana);

            // If we can read CurrentMana and it's truly 0, the stone IS empty —
            // clear any stale blacklist entry. This makes the cooldown self-heal
            // once the cache catches up to the actual state.
            if (readMana && curMana == 0)
                _stoneTapCooldownUntil.Remove(item.Id);

            // Skip stones that recently failed a tap so we don't loop on the
            // same broken pair. The auto-clear above lets a stone return as
            // soon as its real state is known.
            if (_stoneTapCooldownUntil.TryGetValue(item.Id, out long until) && now < until)
                continue;

            // Treat the stone as empty if CurrentMana is 0 OR unreadable.
            // Unreadable happens for stones whose CBaseQualities InqInt isn't
            // populated yet (cache race, partial classification). Being too
            // strict here makes us blind to real empty stones; the stone-tap
            // blacklist handles the misfires when an "empty-looking" stone
            // is actually charged server-side.
            if (_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int cur)
                && cur > 0)
                continue;

            return item.Id;
        }

        return 0;
    }

    /// <summary>Find the most-charged mana stone in inventory (any positive charge).</summary>
    private int FindChargedStone()
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;

        int bestId   = 0;
        int bestMana = 0;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (!IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;

            // Skip stones that timed out on use-on-player recently.
            if (_stoneUseOnPlayerCooldownUntil.TryGetValue(item.Id, out long until) && now < until)
                continue;

            // Eternal: always charged (its mana often can't be read), never used up — first choice.
            if (IsEternal(item)) return item.Id;
            if (!_settings.EnableManaTapping) continue;   // tapping off: only the free charge

            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int cur))
                continue;
            if (cur <= 0) continue;

            if (cur > bestMana) { bestMana = cur; bestId = item.Id; }
        }

        return bestId;
    }

    /// <summary>
    /// The next item to drain. Only items the looter picked up as "ManaTap" are
    /// candidates (the drain destroys the item); every check below is a second
    /// safety net on top of that.
    /// </summary>
    private int FindTapTarget(int threshold)
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        if (_lootedForTap.Count == 0) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;

        foreach (int id in _lootedForTap.Keys)
        {
            WorldObject? item = _objectCache[id];
            if (item == null) continue;
            if (IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;

            // Never tap a casting implement (wand/staff/orb). ACE refuses to
            // drain mana out of these into a stone, so the tap can never complete
            // and the loop spins forever (each attempt cancels movement + bumps
            // busy). This is most often the character's OWN casting wand mis-read
            // as a loose item — WieldedSlot can read 0 from a stale cache, so the
            // equipped-skip below misses it. Mirrors CombatManager.IsWandObject.
            if (IsWandLike(item)) continue;

            // Skip items that recently failed to drain.
            if (_tapTargetCooldownUntil.TryGetValue(item.Id, out long until) && now < until)
                continue;

            // Skip equipped items
            if (_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.WieldedSlot, out int wield)
                && wield != 0)
                continue;

            // Must currently hold at least the threshold worth of mana to be worth a drain
            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int curMana)
                || curMana < threshold)
                continue;

            // Worn gear (stype 10 can read 0), Weapons-tab items and kept loot.
            if (IsProtectedFromTap(item, playerId))
                continue;

            return item.Id;
        }

        return 0;
    }

    /// <summary>
    /// True for casting implements (wand/staff/orb). ACE will not drain mana out
    /// of these into a mana stone, so they must never be selected as a tap target
    /// — otherwise the tap can never complete and loops forever. ObjectClass alone
    /// is unreliable for unappraised items, so we fall back to the name, matching
    /// CombatManager/BuffManager.IsWandObject.
    /// </summary>
    private static bool IsWandLike(WorldObject item)
        => item.ObjectClass == AcObjectClass.WandStaffOrb || IsWandName(item.Name);

    private static bool IsWandName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.IndexOf("Orb",      StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Staff",    StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Wand",     StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Scepter",  StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Sceptre",  StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Baton",    StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Crozier",  StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// True when at least one currently-worn item is BELOW its MaxMana AND
    /// its CurrentMana sits under the configured refill threshold. The
    /// MaxMana floor matters: an item with MaxMana=500 and CurrentMana=500
    /// is full — refilling it is impossible, so it must not register as
    /// "needs mana" or we get an infinite Priority-1 loop that blocks
    /// Priority-2 looted-item draining.
    /// </summary>
    private bool WornEquipmentNeedsMana(int threshold)
    {
        if (!_host.HasGetObjectIntProperty) return false;
        int playerId = unchecked((int)_host.GetPlayerId());
        bool eternal = HasEternalCharge();
        double pct = eternal ? WornRefillPctOfMaxEternal : WornRefillPctOfMax;
        int worn = 0, readable = 0, lowestPct = 101;
        string lowestName = "";
        bool needs = false;
        bool requestedThisPass = false;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;

            // The live slot read can fail off AC's thread; the cache's copy (what /ra wielded
            // uses) is the fallback.
            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.WieldedSlot, out int wield) || wield == 0)
                wield = item.Values(LongValueKey.CurrentWieldedLocation, 0);
            if (wield == 0)
                continue;
            worn++;

            // Only items that actually consume mana matter. Off AC's thread an item's mana is
            // only known once it has been identified (appraisal cache); worn gear often never
            // is, so nothing ever read as low and no stone or charge was used. Identify one such
            // item per check, each at most every 5 minutes (a request bumps the busy count).
            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.MaxMana, out int maxMana))
            {
                long nowMs = NowMs;
                if (!requestedThisPass && _host.HasRequestId
                    && (!_manaIdRequestedAt.TryGetValue(item.Id, out long at) || nowMs - at > 300_000))
                {
                    _manaIdRequestedAt[item.Id] = nowMs;
                    _host.RequestId(unchecked((uint)item.Id));
                    requestedThisPass = true;
                }
                continue;
            }
            if (maxMana <= 0)
                continue;

            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int curMana))
                continue;
            readable++;
            _manaIdRequestedAt.Remove(item.Id);
            int p = (int)(curMana * 100L / maxMana);
            if (p < lowestPct) { lowestPct = p; lowestName = item.Name ?? ""; }

            // Refill only when an item has dropped to a fraction of its MaxMana (25%, or
            // 75% with an Eternal charge). The configured threshold is for *draining* loose
            // items, not for "the player's robe is at 88% of full" — using
            // min(threshold, maxMana) caused the bot to dump charged stones constantly on
            // near-full equipment.
            int floor = (int)(maxMana * pct);
            if (curMana < floor)
                needs = true;
        }

        // Once a minute: what it can see, so "the charge is never used" can be told apart
        // from "the gear's mana can't be read".
        long now = NowMs;
        if (now - _lastGearDiagAt > 60_000)
        {
            _lastGearDiagAt = now;
            _host.Log($"[RynthAi] ManaStone: gear check - worn={worn} manaReadable={readable} lowest={(readable > 0 ? $"{lowestPct}% ({lowestName})" : "n/a")} " +
                      $"refillBelow={(int)(pct * 100)}% eternal={eternal} needs={needs} tapping={_settings.EnableManaTapping}");
        }

        return needs;
    }
}
