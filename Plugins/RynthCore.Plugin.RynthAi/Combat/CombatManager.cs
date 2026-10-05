using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.Combat;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

public partial class CombatManager : IDisposable
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private SpellManager? _spellManager;

    private MainLogic? _raycastSystem;
    public bool RaycastInitialized { get; private set; }

    // Set on the raycast-init bg thread when RaycastInitialized flips
    // false→true; consumed once on the next main-thread ScanNearbyTargets to
    // clear warmup-era blacklist/timer penalties (the bg thread must not touch
    // combat state directly).
    private volatile bool _raycastReadyResetPending;

    public int activeTargetId
    {
        get => _activeTargetId;
        set { _activeTargetId = value; if (value == 0) _nativeAttackTargetId = 0; }
    }
    private int _activeTargetId;
    private DateTime lastAttackCmd = DateTime.MinValue;
    private DateTime lastStanceAttempt = DateTime.MinValue;
    // Exponential backoff for stance flips. A persistent wedge used to re-send
    // ChangeCombatMode every second forever — the 2026-05-25 stance-spam crash
    // class (256 consecutive sends before the client died); BuffManager got
    // backoff then, combat never did. Delay doubles every 4 failed sends,
    // capped at 15s, with a one-shot warning once clearly wedged. Reset
    // wherever the stance is reached or the target drops.
    private int _stanceFlipAttempts;
    private bool _stanceFlipWarned;

    private double StanceRetryDelayMs()
        => Math.Min(15_000, 1000 * Math.Pow(2, Math.Min(4, _stanceFlipAttempts / 4)));

    private void NoteStanceFlipSent()
    {
        _stanceFlipAttempts++;
        if (_stanceFlipAttempts == 16 && !_stanceFlipWarned)
        {
            _stanceFlipWarned = true;
            _host.Log($"[EquipDiag] WARNING: {_stanceFlipAttempts} consecutive ChangeCombatMode sends without reaching the stance — wedge persists despite recovery; backing off to {StanceRetryDelayMs() / 1000:0}s retries.");
        }
    }

    private void ResetStanceFlipBackoff()
    {
        _stanceFlipAttempts = 0;
        _stanceFlipWarned = false;
    }
    // Stuck-stance deadlock recovery (2026-06-10). When the wand reads as wielded
    // client-side but AC refuses to complete the NonCombat→Magic stance change, the
    // equip path would only re-send ChangeCombatMode forever (it never re-equips a
    // "wielded" wand) — a hours-long wedge with NO m_cBusy pin so no watchdog fires.
    private DateTime _stanceStuckSince   = DateTime.MinValue;
    private DateTime _lastStanceRecoverAt = DateTime.MinValue;
    private const double StanceStuckRecoverMs = 8000;
    // Re-equip attempts spent in the CURRENT deadlock episode. ⚠ UseObject on an
    // ALREADY-WIELDED wand is treated by AC as an unwield/MOVE — spamming it
    // every 8s for ~17 min (2026-06-11) jammed AC's item-action queue into a
    // permanent "you can only move one item at a time" state (character
    // unusable, no recovery but relog). The re-equip only helps the rare
    // stale-wield read, so it's capped; past the cap the recovery is
    // mode-change-only and warns once.
    private int _stanceReEquipAttempts;
    private const int StanceReEquipMaxAttempts = 2;

    // ── Combat wand wield gate ───────────────────────────────────────────────
    // The wand-equip path used to re-issue UseObject every 2s forever with no
    // confirmation step and no cap. That is the exact pattern both this file and
    // BuffManager already warn about: "UseObject on a genuinely wielded wand is a
    // MOVE to AC, and unbounded re-equip jams the item queue." Hammering it does
    // not just fail to help — it keeps the item-action queue jammed, so the wield
    // that would have succeeded never lands. Observed 2026-09-02: 47 UseObject
    // sends over 10 minutes, hands empty the whole time, combat never engaging.
    //
    // It went unnoticed because BuffManager.EnsureMagicMode also wields a wand and
    // puts the character in Magic mode; while the rebuff loop was running
    // constantly it re-established the stance every cycle and combat rode along on
    // it. Fixing the buff loop removed that accidental cover and exposed this.
    //
    // Same gate as BuffManager: issue ONE UseObject, wait for the wield to
    // confirm, cool down, and cap the attempts — so a wand that genuinely cannot
    // be wielded degrades loudly instead of wedging the queue silently.
    private int      _wandPendingWieldId;
    private DateTime _wandPendingWieldAt    = DateTime.MinValue;
    private DateTime _wandWieldCooldownUntil = DateTime.MinValue;
    private int      _wandWieldFailCount;
    private bool     _wandWieldWedgeWarned;
    private const double WandWieldResolveTimeoutMs = 2500;   // mirrors BuffManager.WieldResolveTimeoutMs
    private const double WandWieldCooldownMs       = 5000;   // mirrors BuffManager.WieldCooldownMs
    private const int    WandWieldFailMax          = 3;      // mirrors BuffManager.WieldGateFailMax
    private bool _stanceWedgeWarned;
    private DateTime _lastPeaceAttempt = DateTime.MinValue;
    private int _idlePeaceAttempts;   // idle peace requests since peace was last seen (backoff)

    // ── Bow→wand dequip-first swap state (2026-06-27) ────────────────────
    // Stock ACE will NOT auto-dequip a main-hand bow for a Held-slot wand
    // (CheckWeaponCollision refuses while mainhand != null), and it DENIES
    // ChangeCombatMode(Magic) while the bow is wielded ("GetEquippedWand()==null").
    // Combat must stow the bow into an open pack FIRST, then UseObject(wand), and
    // only request Magic once the wand is actually wielded (handled by the
    // alreadyWielded branch). Mirrors the proven BuffManager.EnsureMagicMode path.
    private int _combatBowDequipPendingId;
    private int _combatBowDequipAttempts;
    private DateTime _combatBowDequipAt = DateTime.MinValue;
    private bool _combatSwapTeardownDone;
    private const int CombatBowDequipMaxAttempts = 3;
    private const double WandSwapWieldResolveMs = 4000;

    // Clear all stance-deadlock episode state. Called wherever the stance is
    // reached or the target drops, so the next episode starts fresh.
    private void ResetStanceRecovery()
    {
        _stanceStuckSince = DateTime.MinValue;
        _stanceReEquipAttempts = 0;
        _stanceWedgeWarned = false;
        ResetWandSwapState();
        ResetStanceFlipBackoff();
    }

    // Clear the bow→wand swap episode (called once the desired stance is reached).
    private void ResetCombatWandWieldGate()
    {
        _wandPendingWieldId     = 0;
        _wandPendingWieldAt     = DateTime.MinValue;
        _wandWieldCooldownUntil = DateTime.MinValue;
        _wandWieldFailCount     = 0;
        _wandWieldWedgeWarned   = false;
    }

    private void ResetWandSwapState()
    {
        _combatBowDequipPendingId = 0;
        _combatBowDequipAttempts = 0;
        _combatSwapTeardownDone = false;
    }

    // ── Face-before-attack state ─────────────────────────────────────────
    // For ranged/magic attacks, face the target with smooth turn before firing.
    // ── Native attack state ──────────────────────────────────────
    // StartAttackRequest auto-repeats — only call once per target.
    private uint _nativeAttackTargetId;

    private bool _facingTarget;
    // True only on attack cycles where an OFFENSIVE damage spell was actually
    // cast. The blacklist miss-counter is gated on this in magic mode so it
    // counts confirmed casts, not wall-clock interval ticks (equip waits,
    // cast-gate, "no spell found", tier-down learning) which used to blacklist
    // a perfectly good target in ~3 ticks (~1.2s at SpellCastIntervalMs=400).
    private bool _offensiveCastThisCycle;
    private DateTime _faceStartTime = DateTime.MinValue;
    private const double FACE_TIMEOUT_MS = 1000.0; // give up waiting and swing anyway (non-native MELEE only)
    private const double FACE_TOLERANCE_DEG = 15.0; // heading error threshold to fire

    // ── Magic targeted-cast settle gate (root-cause fix 2026-06-19) ──────────
    // On the ACE server a TARGETED (combat) cast defers its windup behind a
    // turn-to-face; a turn/stop MoveToState that lands during that deferred
    // windup ORPHANS the cast (DoSpellWords/CreatePlayerSpell never runs) → the
    // gesture animates but 0 damage / "You're too busy!" forever. Self-buffs are
    // immune (untargeted → synchronous, no turn). The 2026-06-19 fix turned the
    // bot itself until within angle, then released and settled before the cast;
    // since 2026-09-27 the bot doesn't turn for magic at all (the server's own
    // Rotate does it), and this only settles after releasing a turn melee left held.
    private DateTime _faceSettledAt = DateTime.MinValue;
    private const double FACE_SETTLE_MS   = 140.0;   // let the turn-stop reach the server (≥ one 30Hz tick) before the cast packet

    // ── Magic cast cadence guard ────────────────────────────────────────────
    // Don't issue the next combat cast until the previous one's server windup
    // has resolved (a UseDone arrived, or a hard timeout). Re-casting into the
    // window makes the next cast's stop-thunk/turn orphan the prior cast.
    private bool _awaitingCastResolution;
    private DateTime _castResolutionDeadline = DateTime.MinValue;
    private int _useDoneSeqAtCast;
    private const double CAST_RESOLUTION_TIMEOUT_MS = 2500.0; // covers a tier-7/8 war windup+recoil; also the sole gate on an engine without UseDone observation

    // Smooth turn motions — same codes as NavigationEngine
    private const uint MotionTurnRight = 0x6500000D;
    private const uint MotionTurnLeft  = 0x6500000E;

    // Grace period: keep activeTargetId alive for this long after it disappears from scan,
    // so a single bad LOS result or scan gap doesn't hand control to navigation.
    private DateTime _targetLostScanTime = DateTime.MinValue;
    private const double TARGET_SCAN_GRACE_MS = 1500.0;

    // Target lock — once committed to a mob, hold it until confirmed dead/gone.
    // Prevents spinning caused by target thrashing when world-filter has a transient null
    // or when a second mob briefly becomes slightly closer between scan ticks.
    private int _lockedTargetId = 0;

    private bool _wasMacroRunning;

    private DateTime _lastSpellCast = DateTime.MinValue;
    // Chat throttles for "No spell found" and "Ring spell not found" (per element).
    private DateTime _lastNoSpellChatAt = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _ringMissingChatAt = new(StringComparer.OrdinalIgnoreCase);
    private const double NoSpellChatIntervalMs = 30_000;
    private bool _lastCastWasRing = false;
    private const double ATTACK_SPELL_COOLDOWN_MS = 100.0;

    private bool _returnToPhysicalCombat = false;
    private int _savedWeaponId = 0;
    private bool _physicalDebuffSkipWarned;   // once per session: melee/missile debuffs skipped

    // Shared cross-subsystem weapon-swap serializer (set by RynthAiPlugin).
    // Prevents combat weapon equips from racing the buff wand-equip, and also
    // serializes CombatManager's own re-equip + EquipWeaponAndSetStance so two
    // equips can't fire in one tick.
    private WeaponSwapGate? _weaponSwapGate;
    public void SetWeaponSwapGate(WeaponSwapGate gate) => _weaponSwapGate = gate;

    /// <summary>Current combat mode — read live from AC client each access to avoid event-drop drift.</summary>
    public int CurrentCombatMode =>
        _host.HasGetCurrentCombatMode ? _host.GetCurrentCombatMode() : CombatMode.NonCombat;

    /// <summary>Client busy count — set by plugin from OnBusyCountIncremented/Decremented.
    /// When > 0, combat must not send any game actions (SelectItem, attack, cast) — unless
    /// <see cref="_busyGate"/> shows the count is a leftover the server already finished.</summary>
    public int BusyCount
    {
        get => _busyCount;
        set
        {
            _busyCount = value;
            _busyGate.NoteCount(value, _host.HasUseDoneSeq ? _host.GetUseDoneSeq() : 0);
        }
    }
    private int _busyCount;
    private readonly CombatBusyGate _busyGate = new();

    /// <summary>Resets the plugin's shared busy mirror (not AC's real field) once combat finds the
    /// count is a leftover: the same thing BuffManager's cast-resolved callback does for buffs.
    /// Set by RynthAiPlugin; the argument says why.</summary>
    private Action<string>? _onStaleBusy;
    public void SetStaleBusyCallback(Action<string> cb) => _onStaleBusy = cb;

    private readonly CombatLatencyTracker _latency = new();
    private CombatLatencyTracker.Wait _tickWait = CombatLatencyTracker.Wait.Other;
    private bool _attackedThisTick;
    private int _lastAttackCmdTargetId;   // the target the last attack command (lastAttackCmd) was for

    /// <summary>D4 (record-only): human-readable reason combat did NOT cast on the
    /// most recent tick (or "cast" when it did). Set at every magic skip site; read
    /// by /ra why and the status feed. Purely diagnostic — never gates behavior.</summary>
    public string LastCombatSkipReason { get; private set; } = "";

    private CharacterSkills? _charSkills;

    private readonly WorldObjectCache _worldFilter;

    private static readonly Dictionary<string, string[]> VulnSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fire",      new[] { "Fire Vulnerability Other" } },
        { "Cold",      new[] { "Cold Vulnerability Other" } },
        { "Lightning", new[] { "Lightning Vulnerability Other" } },
        { "Acid",      new[] { "Acid Vulnerability Other" } },
        { "Blade",     new[] { "Blade Vulnerability Other" } },
        { "Slash",     new[] { "Blade Vulnerability Other" } },
        { "Pierce",    new[] { "Piercing Vulnerability Other" } },
        { "Bludgeon",  new[] { "Bludgeoning Vulnerability Other" } },
    };

    private readonly BlacklistManager _blacklistManager = new();
    private int _lastAttackedTargetId = 0;
    // Blacklist is driven by CONFIRMED no-damage casts, not wall-clock time.
    // A cast is queued in _pendingJudge* when issued and only judged a "miss"
    // after BlacklistCastSettleMs with no damage on the target (so the damage
    // packet has time to arrive). BlacklistAttempts consecutive misses → drop.
    private int _consecutiveCastMisses = 0;
    private DateTime _pendingJudgeCastAt = DateTime.MinValue;
    private int _pendingJudgeTargetId = 0;
    private bool _damageSincePendingCast;
    private DateTime _targetLockedAt     = DateTime.MinValue;
    private DateTime _lastDamageDealtAt  = DateTime.MinValue;

    // ── Server attack binding (the 2026-09-29 melee stall) ───────────────────
    // ACE binds a melee attack to Player.MeleeTarget and, while that creature is
    // alive, drops every further TargetedMeleeAttack ("already in melee loop?" in
    // HandleActionTargetedMeleeAttack) — it never switches targets on its own. When
    // the bound mob walked out of reach (the Olthoi in the next cell, 4-6 m away) the
    // server sat in its move-to for it and ignored the bot's attacks on the mob in
    // front of her: combat mode, action Combat, no swings, until the wand heal's
    // CancelAttack + mode change ended the server's attack. So whenever the bot moves
    // its physical attack to a new target while the old one may still be alive, it
    // sends CancelAttack first and attacks the new target on the next cycle.
    private int      _serverAttackTargetId;       // target of the last melee/missile attack request
    private int      _attackRebinds;               // CancelAttacks sent for a target change (status)
    private DateTime _lastRebindLogAt = DateTime.MinValue;
    private int      _rebindLogSuppressed;

    // Swing evidence for the stall watchdog: our hits (0x01B1), "X evaded your
    // attack." and kills. Normal melee produces one every 1-2 s.
    private DateTime _lastSwingEvidenceAt = DateTime.MinValue;
    private DateTime _lastHitAt  = DateTime.MinValue;
    private DateTime _lastMissAt = DateTime.MinValue;
    private DateTime _lastKillAt = DateTime.MinValue;
    private string   _lastSwingEvidenceKind = "none";

    private readonly CombatStallWatchdog _stallWatch = new();
    private DateTime _stallToggleAt = DateTime.MinValue;        // step 3 sent peace; the flip back follows
    private DateTime _lastMissileNoAmmoAt = DateTime.MinValue;   // missile mode waiting on ammo is not a stall
    private int      _stallRewieldId;                            // step 4 in progress (weapon id)
    private bool     _stallRewieldWieldSent;
    private int      _stallRewieldWieldTries;
    private DateTime _stallRewieldAt = DateTime.MinValue;
    private DateTime _stallRewieldCheckAt = DateTime.MinValue;
    private const double StallRewieldStepTimeoutMs = 5000;
    private const int    StallRewieldMaxWieldTries = 3;

    // Ids to skip in the scanner for a short window — both confirmed kills
    // (KillerNotification 0x01AD) and predicted kill-shot swaps. Value = the
    // EXPIRY time, so the longer confirmed window and the shorter predicted
    // window can coexist. The death signal reaches us at the lethal hit —
    // before the mob's health reads 0 or it reclassifies to a corpse (ACE
    // delays CreateCorpse by the full death-animation length) — so without this
    // the scanner would re-lock the dead-but-not-yet-corpse mob and burn another
    // cast on it. Self-prunes at the top of ScanNearbyTargets.
    private readonly Dictionary<int, DateTime> _recentlyKilled = new();
    private readonly List<int> _killPruneScratch = new();
    private const double RECENTLY_KILLED_SUPPRESS_MS = 4000.0;  // confirmed kill
    private const double PREDICTED_SWAP_SUPPRESS_MS  = 2000.0;  // predicted kill (re-acquirable if it survived)

    // ── Kill-shot prediction (damage-based, selection-free) ──────────────────
    // Combat can't read an unselected mob's live health (ACE only pushes vitals
    // for the SELECTED target, and combat is selection-free). Instead we learn
    // from the EXACT per-hit damage the engine pushes (AttackerNotification
    // 0x01B1): accumulate damage dealt this fight, compare to the mob's HP
    // (appraised MaxHealth from the shared CreatureProfileStore, else learned
    // damage-to-kill per wcid), and when the cast we just fired is expected to
    // finish it, swap to the next target instead of wasting the following cast
    // during this projectile's flight.
    private CreatureProfileStore? _creatureStore;   // shared: MaxHealth + resists by wcid
    private MonsterDamageStore?   _damageStore;     // per-character: avg damage + learned HP by wcid
    private int    _fightTargetId;          // the id _fightDamage is being accumulated for
    private uint   _fightTargetWcid;        // its weenie-class id (stable monster-type key)
    private string _fightTargetName = "";   // its name (stored in the learning file for readability)
    private double _fightTargetMaxHp;       // 0 = not yet known
    private double _fightDamage;            // cumulative confirmed damage dealt this fight
    private int    _fightCastCount;         // offensive casts fired this fight (for casts-to-kill learning)
    private bool   _fightAppraiseRequested; // one appraisal attempt per fight to fill unknown HP
    private bool   _fightHpEstimated;       // _fightTargetMaxHp is tier 0's scaled by the tier, not measured
    private AwakenedTier? _awakenedTier;    // Aelrynth's difficulty tier where we stand (0 elsewhere)

    // Predicted one-shots are swapped away BEFORE their own KillerNotification
    // arrives, so OnKillNotification (which keys off the CURRENT target) can't
    // attribute them — they'd go uncounted and bias casts-to-kill HIGH (the
    // recorded average ends up built only from the messier non-swap kills).
    // Stash each predicted kill here; record it to the store only when its death
    // is CONFIRMED (so a wrong prediction expires unrecorded), and consume the
    // matching death-notification so it doesn't drop the (different) live target.
    private readonly List<PendingKill> _predictedKillPending = new();
    private readonly record struct PendingKill(
        string Name, uint Wcid, int CastCount, uint WeaponId, string Element, int Tier, double Damage, DateTime Expiry,
        DateTime Started = default, string? Summon = null, int Id = 0);

    // Ring of recently-fought objects (by id). The single _lastFight* slot only credits a kill
    // that lands one-back; when we've advanced PAST the dying mob (rapid multi-mob melee) its
    // death message matches neither the current target nor _lastFight, and the kill is lost
    // (KillDbg how=miss). This ring credits it by name across the last few fights.
    private readonly List<(int Id, PendingKill Pk)> _recentFights = new();
    private const int RecentFightsCap = 12;
    private const double RecentFightTtlMs = 8000.0;

    // name -> wcid for monsters seen by the scanner this session. AOE/area casts kill mobs we
    // never directly fought (ACE streams health only for the selected target, so tiers 1-4 never
    // saw them) — tier 5 credits those kills by matching the death message against these names.
    // Same pump thread as the scanner and OnKillNotification, so no lock needed.
    private readonly Dictionary<string, uint> _seenMonsterNameToWcid =
        new(StringComparer.OrdinalIgnoreCase);

    // Persistent snapshot of the most-recently-cast-at fight — survives DropTarget
    // (which zeroes _fightTarget*). When a kill arrives after the bot has already
    // dropped/advanced (common when one-shotting fast: the corpse's KillerNotification
    // lands a beat after we moved on), this lets the kill still be credited by name.
    private uint   _lastFightWcid;
    private string _lastFightName = "";
    private uint   _lastFightWeaponId;
    private string _lastFightElement = "";
    private int    _lastFightTier;
    private int    _lastFightCount;
    private int    _killDbgCount;
    private string _lastCastElement = "Fire"; // element of the most recent offensive cast
    private int    _lastCastTier;             // tier (spell level) of the most recent offensive cast
    private uint   _lastCastWeaponId;         // weapon (wand) guid the most recent cast used — damage varies by weapon
    private DateTime _fightStartedAt;         // when BeginFight engaged the current target (seconds-to-kill)
    private string?  _fightSummon;            // summon element out during this fight (null = none, "" = unknown element)

    /// <summary>The summon out right now: null = none, "" = one of unknown element, else its element. Set by the plugin (PetManager).</summary>
    internal Func<string?>? SummonOut;

    private int    _equippedWeaponId;         // weapon EquipWeaponAndSetStance last wielded (any mode) — drives melee/missile damage learning
    private bool   _predictKillSwap;          // set by AttackWithMagic, consumed in Think after the cast
    // Predicted kill shot is ARMED here but only executed once the cast is
    // CONFIRMED accepted (ACE: no "You're too busy!" within the refusal window).
    // The old code dropped the target 1ms after issuing the cast — so a REFUSED
    // cast still abandoned a live mob and advanced to the next, churning the
    // swarm and pinning AC's m_cBusy (the "can't enter combat mode" wedge).
    private bool   _predictKillSwapArmed;
    private bool   _predictSwapGestureSeen;       // observed the cast windup since arming (gate the swap on it)
    private int      _predictArmTargetId;         // target of the cast that armed the swap
    private DateTime _predictArmCastAt = DateTime.MinValue;   // when that cast was issued
    private const double PredictKillSwapMaxWaitMs = 3000; // hard cap: a tier-8 cast fully resolves well within this
    private const double KILL_CONFIDENCE = 0.80; // predict kill if remaining HP <= avg cast damage * this
    private const int    KILL_MIN_SAMPLES = 3;    // need this many learned hits before trusting the avg

    internal void SetDamageStores(CreatureProfileStore? creatures, MonsterDamageStore? damage)
    {
        _creatureStore = creatures;
        _damageStore = damage;
    }

    internal void SetAwakenedTier(AwakenedTier? tier) => _awakenedTier = tier;

    /// <summary>Aelrynth's difficulty tier where we stand; always 0 on other servers.</summary>
    private int Difficulty => _awakenedTier?.Current ?? 0;

    public int RaycastBlockCount { get; private set; }
    public int RaycastCheckCount { get; private set; }

    // D2 three-tier target telemetry, refreshed each ScanNearbyTargets() so a
    // "scanned=0 vs live mobs" wedge shows WHICH stage zeroed the candidate set:
    //   Total>0,Ring=0      → mobs exist but all out of engage range (nav/spawn issue)
    //   Ring>0,Possible=0   → over-filtering (blacklist / not-attackable / LOS)
    //   Total=0             → genuinely blind (cache empty / respawn-blind)
    // -1 = no scan has run yet this session.
    public int LastScanTotalMonsters { get; private set; } = -1;  // real monsters the client renders
    public int LastScanInRing { get; private set; } = -1;         // of those, within MonsterRange
    public int LastScanPossible { get; private set; } = -1;       // survive all combat filters (= candidates)
    public int LastScanLosBlocked { get; private set; } = -1;     // in range + attackable but wall-blocked

    // D6 offensive attack-cast counters. CastsSinceLastKill climbing while kills stay
    // flat is the early signal of the "animates but 0 damage / too busy forever" orphan.
    public int SessionAttackCasts { get; private set; }
    public int CastsSinceLastKill { get; private set; }

    // ── Monster scanner ──────────────────────────────────────────────────
    // Pre-scans nearby creatures for distance, IsAttackable, and LOS.
    // Combat picks from this list — no SelectItem needed until attack time.
    private readonly List<ScannedTarget> _scannedTargets = new();
    private DateTime _lastScanTime = DateTime.MinValue;
    private const int SCAN_INTERVAL_MS = 50;

    // Utility-AI target switching: every candidate is scored each tick.
    // The currently-locked target gets this bonus so we don't flap on near-ties —
    // an alternative must beat (locked_score + STICKINESS) to take over.
    private const double TARGET_SWITCH_STICKINESS = 25.0;

    // Extra hold applied ON TOP of stickiness once we have actually landed damage
    // on the locked target — "finish what you started". Stickiness alone only
    // breaks near-ties; this is what stops combat walking away from a half-dead
    // mob because something undamaged wandered closer. Cleared with the rest of
    // the fight state on switch/DropTarget. See HandleCombatTrigger.
    private const double DAMAGE_COMMITMENT_BONUS = 20.0;
    // A monster this much lower in health than the wounded target takes over (a predicted kill
    // that survived). See HandleCombatTrigger.
    private const float MoreWoundedMargin = 0.15f;

    // Monster priority is a strict tier, not a score bonus: when a monster of a higher
    // Priority is in range (MonsterRange), only monsters of that Priority are candidates,
    // and a lower-priority target is left for it at once. As a +5-per-level bonus it lost
    // to stickiness (+25) and commitment (+20) and did "little to nothing" (2026-10-03).
    // Priority comes from GetRuleForTarget (name, match expression, else the DEFAULT row),
    // cached per monster for PriorityCacheMs because match expressions aren't free and
    // HandleCombatTrigger runs every tick over every scanned monster.
    private readonly Dictionary<int, (int Priority, DateTime At)> _priorityCache = new();
    private List<MonsterRule>? _priorityCacheRules;
    private const double PriorityCacheMs = 1000;
    private int _lockedPriority;

    /// <summary>How long a cast-issue commitment arm survives without damage
    /// landing. Covers a projectile's flight (Force Arc confirms at a ~650ms
    /// median, damage follows) without granting open-ended immunity to a target
    /// that never takes damage. See the arming block in HandleCombatTrigger.</summary>
    private const double CastCommitmentWindowMs = 4000.0;

    /// <summary>Ceiling on how long a target that has never taken damage may keep
    /// the commitment bonus after being locked. Past this it competes on its own
    /// merits again, so an unkillable or unreachable mob cannot hold the lock
    /// forever — the 2026-09-05 stall, where one Olthoi absorbed 5+ hours and
    /// ~3,100 casts because commitment never expired and
    /// TargetNoProgressTimeoutSec defaults to 0 (disabled).</summary>
    private const double UndamagedCommitmentMaxMs = 15000.0;

    /// <summary>When the last offensive action was issued at
    /// <see cref="_lastAttackedTargetId"/>. Bounds the cast-issue commitment arm.</summary>
    private DateTime _lastAttackedAt = DateTime.MinValue;

    // Distance (m) at which the melee-threat bonus reaches full value. The bonus
    // ramps continuously to this range instead of switching on at a hard edge —
    // as a step it was worth more than TARGET_SWITCH_STICKINESS, so a mob
    // drifting across the line flipped the winner every tick. See ScoreCandidate.
    private const double THREAT_RANGE_M = 5.0;

    // Incumbent continuity across scan gaps. ScanNearbyTargets drops a target on
    // a single bad LOS result or a transient miss — the candidate list has been
    // observed oscillating 14<->17 entries tick to tick. HandleCombatTrigger
    // scores only what is in the list, so an incumbent that blinks out scores
    // NOTHING and loses to any challenger. That is the "score=gone" switch, and
    // it defeats TARGET_SCAN_GRACE_MS, whose stated job is that "one bad LOS
    // result or scan gap shouldn't hand control to navigation" — it protected
    // the target from being DROPPED but not from being OUTSCORED while absent.
    // Carry the last computed score for the grace window instead.
    private double   _lockedLastScore     = double.MinValue;
    private DateTime _lockedLastSeenInScan = DateTime.MinValue;

    public struct ScannedTarget
    {
        public int Id;
        public double Distance;
        public double Angle;   // absolute facing error to target, degrees (0 = directly ahead)
        public string Name;
    }

    /// <summary>List of nearby, attackable, LOS-clear creatures. Re-scanned every tick (≤50 ms).
    /// Sorted by distance — selection itself is score-based via HandleCombatTrigger.</summary>
    public IReadOnlyList<ScannedTarget> ScannedTargets => _scannedTargets;
    internal MonsterDamageStore?   DamageStore   => _damageStore;    // per-monster pet choice (PetManager)
    internal CreatureProfileStore? CreatureStore => _creatureStore;  // learned resists (PetManager)

    /// <summary>True when combat has an active target or viable targets nearby. Used to block navigation.</summary>
    public bool HasTargets => activeTargetId != 0 || _scannedTargets.Count > 0;

    // Monsters that passed range/attackable checks but were LOS-blocked by walls.
    // Non-zero means a monster is nearby but not yet visible — nav should stop.
    private int _nearbyNoLos;

    /// <summary>True when any monster is in range, regardless of LOS. Use this for nav-blocking so
    /// the bot stops before walking through a portal into a room with monsters.</summary>
    public bool HasNearbyMonsters => HasTargets || _nearbyNoLos > 0;

    /// <summary>True if any scanned target is within <paramref name="yd"/> yards. Used by loot/salvage gates to decide when to yield to combat.</summary>
    public bool HasCloseThreat(double yd)
    {
        for (int i = 0; i < _scannedTargets.Count; i++)
            if (_scannedTargets[i].Distance <= yd) return true;
        return false;
    }

    /// <summary>True only when actively attacking a specific target. Unlike HasTargets, this is false between kills even when more monsters are scanned nearby.</summary>
    public bool IsActivelyEngaged => activeTargetId != 0;

    /// <summary>Yards inside which a scanned mob counts as "on top of me".</summary>
    private const double CloseAttackYards = 5.0;

    /// <summary>
    /// "A fight is actually happening to me" — the arbiter's escape hatch that
    /// lets Combat preempt BoostLootPriority (2026-06-03 audit P1#1).
    ///
    /// Deliberately NOT HasEngageableTarget: that is true for any scanned mob
    /// inside MonsterRange, which would make BoostLootPriority meaningless.
    /// And deliberately not IsActivelyEngaged alone: while BoostLoot holds the
    /// Looting decision, canRun blocks Think(), so activeTargetId never gets
    /// set — keying the escape on it would be circular (blocked → no lock →
    /// still blocked → mob keeps hitting a bot that never fights back). The
    /// scan always runs and has no side effects, so a close-range scanned mob
    /// is the non-circular signal.
    /// </summary>
    public bool IsUnderCloseAttack => IsActivelyEngaged || HasCloseThreat(CloseAttackYards);

    /// <summary>
    /// Pure predicate for the ActivityArbiter: should Combat claim this tick?
    /// True iff we're already locked on a target, OR a scanned target (the
    /// scan is already filtered to attackable + LOS-clear) is within actual
    /// engage range (MonsterRange — the same distance Think() uses for its
    /// attack gate). Deliberately NOT HasTargets: HasTargets is true for any
    /// scanned creature including unreachable/out-of-range ones, which made
    /// Combat squat on the action lock without ever attacking while nav was
    /// blocked — the "stands there surrounded by far-off mobs" freeze. With
    /// this predicate, far mobs → Combat doesn't claim → nav runs and closes
    /// distance → once within MonsterRange this flips true → Combat takes over.
    /// No side effects; safe to call from the arbiter's pure decision path.
    /// </summary>
    public bool HasEngageableTarget =>
        IsActivelyEngaged || HasCloseThreat(System.Math.Max(1, _settings.MonsterRange));

    /// <summary>
    /// Distance at which an already-locked target is dropped / stops being
    /// attacked. Larger than MonsterRange (the acquire distance) to form a
    /// hysteresis deadband around the combat-mode step-back. Configurable via
    /// MonsterDisengageRange; 0 or any value not exceeding MonsterRange falls
    /// back to MonsterRange + 3.
    /// </summary>
    private double DisengageDistance =>
        _settings.MonsterDisengageRange > _settings.MonsterRange
            ? _settings.MonsterDisengageRange
            : _settings.MonsterRange + 3;

    /// <summary>Diagnostic snapshot for /ra combat — exposes the internal state machine fields.</summary>
    public CombatStateSnapshot GetStateSnapshot()
    {
        // Pick the same weapon EquipWeaponAndSetStance would pick for the active target,
        // so the dump shows which weapon combat is trying to swing/cast with.
        WorldObject? targetObj = activeTargetId != 0 ? _worldFilter[activeTargetId] : null;
        int    pickedWeaponId    = 0;
        string pickedWeaponName  = "";
        string pickedWeaponWhy   = "";
        int    pickedWeaponMode  = 0;
        int    weaponWieldLoc    = -1;
        if (targetObj != null)
        {
            // The same choice EquipWeaponAndSetStance makes, else any wand.
            var dumpRule = GetRuleForTarget(targetObj);
            string dumpDesired = "Auto";
            pickedWeaponId = ChooseWeapon(targetObj, dumpRule, ref dumpDesired, out pickedWeaponWhy);
            if (pickedWeaponId == 0) { pickedWeaponId = FindWandInItems(); pickedWeaponWhy = "FindWandInItems"; }

            if (pickedWeaponId != 0)
            {
                var w = _worldFilter[pickedWeaponId];
                if (w != null)
                {
                    pickedWeaponName = w.Name ?? "";
                    weaponWieldLoc   = w.Values(LongValueKey.CurrentWieldedLocation, 0);
                    pickedWeaponMode = IsWandObject(w)                                  ? CombatMode.Magic
                                    : w.ObjectClass == AcObjectClass.MissileWeapon ? CombatMode.Missile
                                    : CombatMode.Melee;
                }
            }
        }

        int liveMode = -1;
        if (_host.HasGetCurrentCombatMode)
        {
            try { liveMode = _host.GetCurrentCombatMode(); } catch { liveMode = -1; }
        }

        bool hasAmmo = false;
        try { hasAmmo = HasWieldedAmmo(); } catch { hasAmmo = false; }

        return new()
        {
            ActiveTargetId       = activeTargetId,
            LockedTargetId       = _lockedTargetId,
            ScannedCount         = _scannedTargets.Count,
            ClosestScannedId     = _scannedTargets.Count > 0 ? _scannedTargets[0].Id        : 0,
            ClosestScannedName   = _scannedTargets.Count > 0 ? _scannedTargets[0].Name ?? "" : "",
            ClosestScannedDist   = _scannedTargets.Count > 0 ? _scannedTargets[0].Distance  : 0.0,
            BusyCount            = BusyCount,
            FacingTarget         = _facingTarget,
            TargetLostScanTime   = _targetLostScanTime,
            LastAttackCmd        = lastAttackCmd,
            LastStanceAttempt    = lastStanceAttempt,
            LastEquipTime        = _lastEquipTime,
            CurrentCombatMode    = CurrentCombatMode,
            LiveCombatMode       = liveMode,
            BotAction            = _settings.BotAction ?? "",
            EnableCombat         = _settings.EnableCombat,
            IsMacroRunning       = _settings.IsMacroRunning,
            PickedWeaponId       = pickedWeaponId,
            PickedWeaponName     = pickedWeaponName,
            PickedWeaponWhy      = pickedWeaponWhy ?? "",
            PickedWeaponMode     = pickedWeaponMode,
            PickedWeaponWieldLoc = weaponWieldLoc,
            HasWieldedAmmoFlag   = hasAmmo,
            LastCombatSkipReason = LastCombatSkipReason,
        };
    }

    public struct CombatStateSnapshot
    {
        public int      ActiveTargetId;
        public int      LockedTargetId;
        public int      ScannedCount;
        public int      ClosestScannedId;
        public string   ClosestScannedName;
        public double   ClosestScannedDist;
        public int      BusyCount;
        public bool     FacingTarget;
        public DateTime TargetLostScanTime;
        public DateTime LastAttackCmd;
        public DateTime LastStanceAttempt;
        public DateTime LastEquipTime;
        public int      CurrentCombatMode;
        public int      LiveCombatMode;
        public string   BotAction;
        public bool     EnableCombat;
        public bool     IsMacroRunning;
        public int      PickedWeaponId;
        public string   PickedWeaponName;
        /// <summary>Why that weapon (the plan's source, e.g. "slayer (Olthoi x2)").</summary>
        public string   PickedWeaponWhy;
        public int      PickedWeaponMode;
        public int      PickedWeaponWieldLoc;
        public bool     HasWieldedAmmoFlag;
        public string   LastCombatSkipReason;
    }

    /// <summary>
    /// Periodic scan of all creatures. Filters: distance, self, blacklist, IsAttackable, raycast LOS.
    /// Call from OnHeartbeat or Think.
    /// </summary>
    public void ScanNearbyTargets()
    {
        if ((DateTime.Now - _lastScanTime).TotalMilliseconds < SCAN_INTERVAL_MS)
            return;
        _lastScanTime = DateTime.Now;

        // Raycast just became ready (warmup→ready edge, flagged on the bg
        // thread): one-time clean slate. Whatever got blacklisted or had its
        // miss/no-progress timers run up while targeting was degraded is
        // forgiven, so mobs that were present at login are re-evaluated with
        // real LOS/attack-type instead of being sidelined for 5 minutes.
        if (_raycastReadyResetPending)
        {
            _raycastReadyResetPending = false;
            _blacklistManager.ClearAll();
            _consecutiveCastMisses = 0;
            _pendingJudgeCastAt = DateTime.MinValue;
            _lastAttackedTargetId = 0;
            _targetLockedAt = DateTime.MinValue;
            _lastDamageDealtAt = DateTime.MinValue;
            _host.Log("[RynthAi] Raycast ready — combat clean slate (blacklist cleared, target timers reset).");
        }

        // Drop expired kill-suppression entries so the set can't grow unbounded
        // over a long session (a suppressed id may never re-enter scan to be
        // cleared lazily). Allocation-free: scratch list is reused.
        if (_recentlyKilled.Count > 0)
        {
            DateTime now = DateTime.Now;
            _killPruneScratch.Clear();
            foreach (var kv in _recentlyKilled)
                if (now >= kv.Value)            // Value = expiry time
                    _killPruneScratch.Add(kv.Key);
            for (int i = 0; i < _killPruneScratch.Count; i++)
                _recentlyKilled.Remove(_killPruneScratch[i]);
        }

        _scannedTargets.Clear();
        _nearbyNoLos = 0;
        int scanTotal = 0, scanRing = 0;   // D2 telemetry, published after the loop
        int playerId = (int)_playerId;
        if (playerId == 0) return;

        // Cache player pose once for angle tiebreaker — same math as GetFacingError.
        bool hasPose = _host.TryGetPlayerPose(out _, out float ppx, out float ppy, out _,
            out float pqw, out _, out _, out float pqz);
        double playerHeadingDeg = 0;
        if (hasPose)
        {
            double physYaw = 2.0 * Math.Atan2(pqz, pqw) * (180.0 / Math.PI);
            playerHeadingDeg = ((-physYaw) % 360.0 + 720.0) % 360.0;
        }

        double maxDist = _settings.MonsterRange;
        TargetingFSM.AttackType attackType = TargetingFSM.AttackType.Linear;
        if (RaycastInitialized && _raycastSystem?.TargetingFSM != null)
            attackType = DetermineAttackTypeForLOS();
        bool losDebug = _settings.LosDebugLog;
        if (losDebug) LogLosDebugWeapon();

        // The player's position once per scan: Distance(player, mob) re-read it for every
        // creature, two host reads per monster 20 times a second (2026-10-02 flood work).
        bool hasPlayerPos = _host.TryGetObjectPosition((uint)playerId, out uint meCell,
            out float meX, out float meY, out float meZ);

        foreach (var wo in _worldFilter.GetLandscape())
        {
            if (wo.Id == playerId) continue;
            if ((int)wo.ObjectClass != (int)AcObjectClass.Monster) continue;

            // Never acquire our own spell projectiles (mis-classified as
            // Monster, no health record). A real monster is never named
            // "Flame Bolt"/"Frost Streak"/etc.
            if (IsSpellProjectileName(wo.Name)) continue;

            // D2 telemetry: every real monster the client renders counts toward Total, and (by
            // pure proximity) toward Ring. dist is hoisted up here from below the blacklist gate
            // so Ring reflects how many mobs are physically near us regardless of the gameplay
            // filters below — that's what distinguishes "out of range" from "all filtered out".
            scanTotal++;
            bool hasMobPos = false;
            float tx = 0f, ty = 0f;
            double dist = hasPlayerPos
                ? _worldFilter.DistanceFrom(meCell, meX, meY, meZ, wo.Id, out hasMobPos, out tx, out ty)
                : double.MaxValue;
            if (dist <= maxDist) scanRing++;

            // Cache name->wcid (once per type) for AOE kill attribution (tier 5): a mob wiped
            // by an area cast we never directly fought is credited by matching its death message.
            // Attackable only — this runs before the attackable filter below, so an NPC the
            // cache misclassifies as a monster could otherwise be credited kills (Damage tab).
            if (!string.IsNullOrEmpty(wo.Name) && _seenMonsterNameToWcid.Count < 256
                && !_seenMonsterNameToWcid.ContainsKey(wo.Name)
                && (!_host.HasObjectIsAttackable || _host.ObjectIsAttackable((uint)wo.Id))
                && _host.HasGetObjectWcid && _host.TryGetObjectWcid((uint)wo.Id, out uint scanWcid) && scanWcid != 0)
                _seenMonsterNameToWcid[wo.Name] = scanWcid;

            // Allow the currently-engaged target out to DisengageDistance: AC's
            // combat-mode step-back routinely pushes the active target just past
            // MonsterRange, and dropping it from the scan defeated the disengage
            // hysteresis — it vanished from _scannedTargets and was dropped after
            // the 1.5s scan grace despite the distance gate intending to keep
            // attacking out to DisengageDistance.
            // The range gate comes before the other filters (all of them only drop a monster,
            // so the order doesn't change the result): in a crowded dungeon most monsters are
            // out of range and now cost only their distance, not the name list, blacklist,
            // attackable and health reads as well.
            bool isEngaged = wo.Id != 0 && (wo.Id == activeTargetId || wo.Id == _lockedTargetId);
            if (dist > (isEngaged ? Math.Max(maxDist, DisengageDistance) : maxDist)) continue;

            // User-configured "never attack" list — excluded from acquisition
            // entirely (an already-engaged match drops out of _scannedTargets
            // here and is released by the scan-grace timer in Think).
            if (IsUserBlacklistedName(wo.Name)) continue;

            if (_blacklistManager.IsBlacklisted(wo.Id)) continue;

            // Just killed (KillerNotification) but not yet a corpse — skip so we
            // don't re-lock and cast at it during its death animation.
            if (_recentlyKilled.ContainsKey(wo.Id)) continue;

            if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)wo.Id)) continue;

            // Dead but not yet reclassified as corpse — skip
            if (_worldFilter.GetHealthRatio(wo.Id) == 0f) continue;

            bool losBlocked = false;
            if (_settings.EnableRaycasting && RaycastInitialized && _raycastSystem?.TargetingFSM != null)
            {
                RaycastCheckCount++;
                losBlocked = _raycastSystem.TargetingFSM.IsTargetBlocked(_host, (uint)wo.Id, attackType, out var losDetail);
                if (losBlocked) RaycastBlockCount++;
                if (losDebug) LogLosDebug(wo, losBlocked, losDetail);
            }

            // Don't blacklist from scan — just exclude from this result.
            // Blacklisting only happens when an active target fails LOS during attack.
            if (losBlocked)
            {
                _nearbyNoLos++; // in range + attackable but wall-blocked — still stops nav
                continue;
            }

            double angle = 180.0;
            if (hasPose && hasMobPos) // the mob's position was read for the distance above
            {
                double desired = Math.Atan2(tx - ppx, ty - ppy) * (180.0 / Math.PI);
                if (desired < 0) desired += 360.0;
                double err = desired - playerHeadingDeg;
                while (err > 180.0) err -= 360.0;
                while (err < -180.0) err += 360.0;
                angle = Math.Abs(err);
            }
            _scannedTargets.Add(new ScannedTarget { Id = wo.Id, Distance = dist, Angle = angle, Name = wo.Name });
        }

        // Primary: closest first. Tiebreaker within 0.5yd: smallest facing angle first.
        _scannedTargets.Sort((a, b) =>
        {
            double dd = a.Distance - b.Distance;
            if (Math.Abs(dd) > 0.5) return dd < 0 ? -1 : 1;
            return a.Angle.CompareTo(b.Angle);
        });

        // Publish D2 telemetry (whole-int writes are atomic; the plugin tick reads these on the
        // same pump thread before pushing them to the status feed).
        LastScanTotalMonsters = scanTotal;
        LastScanInRing = scanRing;
        LastScanPossible = _scannedTargets.Count;
        LastScanLosBlocked = _nearbyNoLos;
    }

    public CombatManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache worldFilter, SpellManager? spellManager = null)
    {
        _host = host;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _worldFilter = worldFilter ?? throw new ArgumentNullException(nameof(worldFilter));
        _spellManager = spellManager;
        // Diagnostic: surface every blacklist (any path) with id + reason so a
        // single login test is conclusive instead of another guess.
        _blacklistManager.Log = m => _host.Log($"[Blacklist] {m}");
        ElementTracker = new WeaponElementTracker(host);
    }

    /// <summary>Reads listed weapons' elements and identifies them (pumped by the plugin).</summary>
    internal WeaponElementTracker ElementTracker { get; }
    /// <summary>Combat's weapon swaps: one in flight, confirmed, capped, no flip-flop.</summary>
    internal WeaponSwapTracker SwapTracker { get; } = new();
    /// <summary>Tests: which elements the character can cast (default: the spellbook).</summary>
    internal Func<string, bool>? CanCastOverride { get; set; }
    /// <summary>Tests: the weapon in hand (default: a live read of the inventory).</summary>
    internal Func<int>? InHandOverride { get; set; }

    public void SetSpellManager(SpellManager spellManager) => _spellManager = spellManager;
    public void SetCharacterSkills(CharacterSkills skills) => _charSkills = skills;

    /// <summary>Plugin forwards every object deletion here (D7/D8). For a genuinely-despawned
    /// object (a reliable engine event, unlike a transient scan gap) we: (D8) drop its stale
    /// per-id state — kill-suppression + blacklist/failure entries — so a respawn reusing the GUID
    /// starts clean instead of inheriting the old 300s timeout; and (D7) if it was the target we
    /// were attacking or mid-cast on, clear the cast-resolution/debuff wait so we don't sit out the
    /// 2.5s windup timeout casting at nothing. Writes are atomic flag/int sets — safe on any thread.</summary>
    public void OnObjectDeleted(uint objectId)
    {
        int id = (int)objectId;
        _recentlyKilled.Remove(id);
        _blacklistManager.ForgetTarget(id);

        if (id == activeTargetId || id == _lockedTargetId || id == _lastAttackedTargetId)
            _awaitingCastResolution = false;
        if (id == _pendingDebuffTargetId)
        {
            _waitingForDebuffResult = false;
            _pendingDebuffTargetId = 0;
        }
    }
    public void SetPlayerId(uint playerId)
    {
        if (playerId != _playerId)
        {
            // A new character (or a relog): its weapons are new objects.
            SwapTracker.Reset();
            ResetAmmoState();
            ResetOffhandState();
            ElementTracker.Reset();
            _unlistedNoted.Clear();
            _inHandId = 0;
            _inHandReadAt = DateTime.MinValue;
        }
        _playerId = playerId;
        _monsterMatchEval = new MonsterMatchEvaluator(_worldFilter, playerId);
    }
    public void SetRaycastSystem(MainLogic raycast)
    {
        bool wasReady = RaycastInitialized;
        _raycastSystem = raycast;
        RaycastInitialized = raycast?.IsInitialized ?? false;

        // Warmup→ready edge. This runs on the raycast-init bg thread, so do
        // NOT mutate combat state here — defer the clean slate to the next
        // main-thread scan. Anything sidelined while targeting was degraded
        // (no LOS, Linear attack-type) gets re-evaluated now it's real.
        if (!wasReady && RaycastInitialized)
            _raycastReadyResetPending = true;
    }

    private uint _playerId;
    private MonsterMatchEvaluator? _monsterMatchEval;

    private readonly HashSet<string> _confirmedDebuffs = new();
    // Failed tries (fizzle/resist/refusal) per "{target}_{debuff}", cleared with _confirmedDebuffs.
    private readonly Dictionary<string, int> _debuffFailures = new();
    private const int DebuffMaxFailures = 3;
    private int _lastDebuffTargetId = 0;

    private string? _pendingDebuffKey = null;
    private int _pendingDebuffTargetId = 0;
    private int _pendingDebuffTier = 0;
    private bool _waitingForDebuffResult = false;
    private DateTime _pendingDebuffCastTime = DateTime.MinValue;
    private const double DEBUFF_RESULT_TIMEOUT_MS = 3000.0;

    // Last offensive (war/void) spell we fired and when. If AC never answers it
    // (char doesn't know it, or no components) the no-chat valve in
    // AttackWithMagic marks it unresolvable so FindBestOffensiveSpellId tiers
    // down to an alternative the char actually has — same empirical signal the
    // buff path uses (the engine IsSpellKnown oracle lies "true" for unknowns).
    private int _pendingOffensiveSpellId = 0;
    private DateTime _pendingOffensiveCastAt = DateTime.MinValue;
    // Target id captured when the offensive cast was *issued*, so the
    // chat-confirmation handler can record the cast against the right target
    // for blacklist judgement — even if activeTargetId has since changed.
    private int _pendingOffensiveTargetId = 0;
    private const double OFFENSIVE_NOCHAT_TIMEOUT_MS = 5000.0;
    // ACE doesn't emit "You cast X" chat for offensive war magic, so we infer
    // a successful server cast by NEGATION: if no "You're too busy!" arrives
    // within this many ms of issuance, the gesture proceeded — queue the cast
    // for blacklist judgement. Server "too busy" responses arrive within
    // ~100–200ms; 500ms is a comfortable margin without delaying the next
    // attempt noticeably.
    private const double OffensiveRefusalWindowMs = 500.0;

    // Throttled lowercased inventory-name set for the predictive component
    // gate (TrySpellByName is called many times per resolution; rebuilding the
    // set every call would be wasteful). _compSkipLogged dedupes the skip log
    // within a cache window so a tiered-down spell doesn't spam the log.
    private readonly HashSet<string> _invNamesLower = new();
    private DateTime _invNamesBuiltAt = DateTime.MinValue;
    private const double InvNameCacheMs = 1000.0;
    private readonly HashSet<int> _compSkipLogged = new();

    // Predictive component gate is OFF: the dat formula is the full historical
    // recipe, not ACE's actual (reduced) requirement, so it false-rejected
    // every spell. Empirical no-components learning + persistence handles it
    // reliably instead. See the long note in TrySpellByName.
    private const bool EnablePredictiveComponentGate = false;

    private static readonly Dictionary<string, string[]> DebuffSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fester",    new[] { "Fester Other",                    "Decrepitude's Grasp" } },
        { "Broadside", new[] { "Missile Weapon Ineptitude Other", "Broadside of a Barn" } },
        { "Gravity",   new[] { "Vulnerability Other",             "Gravity Well" } },
        { "Imperil",   new[] { "Imperil Other",                   "Gossamer Flesh" } },
        { "Yield",     new[] { "Magic Yield Other",               "Yield" } },
    };

    // Shape index into the SpellShapes / VoidSpellShapes rows.
    internal const int ShapeArc = 0, ShapeRing = 1, ShapeStreak = 2, ShapeBolt = 3, ShapeBlast = 4;

    // Base names per element, by shape: Arc, Ring, Streak, Bolt, Blast. Every non-ring base has
    // "{base} I".."VI" (blasts: III-VI only, retail has no blast I/II) and "Incantation of {base}"
    // in SpellData.txt (checked by the host test "attack spells: every element and shape").
    // Fixed 2026-10-03: Lightning's bolt was "Shock Wave" (the bludgeon bolt), Bludgeon's arc
    // "Bludgeoning Arc" (is "Shock Arc"), the Blade/Slash streak "Blade Streak" (is "Whirling
    // Blade Streak") and the Bludgeon streak "Bludgeoning Streak" (is "Shock Wave Streak"): none
    // of those names exist, so those shapes cast the wrong element or only their tier-7 lore.
    private static readonly Dictionary<string, string[]> SpellShapes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fire",      new[] { "Flame Arc", "Ring of Fire", "Flame Streak", "Flame Bolt", "Flame Blast" } },
        { "Cold",      new[] { "Frost Arc", "Frost Ring", "Frost Streak", "Frost Bolt", "Frost Blast" } },
        { "Lightning", new[] { "Lightning Arc", "Shock Ring", "Lightning Streak", "Lightning Bolt", "Lightning Blast" } },
        { "Acid",      new[] { "Acid Arc", "Acid Ring", "Acid Streak", "Acid Stream", "Acid Blast" } },
        { "Blade",     new[] { "Blade Arc", "Blade Ring", "Whirling Blade Streak", "Whirling Blade", "Blade Blast" } },
        { "Pierce",    new[] { "Force Arc", "Force Ring", "Force Streak", "Force Bolt", "Force Blast" } },
        { "Bludgeon",  new[] { "Shock Arc", "Bludgeoning Ring", "Shock Wave Streak", "Shock Wave", "Shock Blast" } },
        { "Slash",     new[] { "Blade Arc", "Blade Ring", "Whirling Blade Streak", "Whirling Blade", "Blade Blast" } },
    };

    // War Streak/Bolt/Blast lines have NO "{base} VII" — their tier-7 is a
    // lore-named spell (Arc uses Roman "VII"; Ring uses RingLoreNames).
    // [0] = Streak VII lore, [1] = Bolt VII lore, [2] = Blast VII lore. Names
    // verified against SpellData.txt skill-300 entries 2026-05-17 (e.g. Force
    // Streak VII = "Outlander's Insolence" id 2133); blasts 2026-10-03 (power
    // 325, the blast category: e.g. Flame Blast VII = "Silencia's Scorn" id 2127).
    // Slash shares the Blade family.
    private static readonly Dictionary<string, string[]> WarTier7Lore = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fire",      new[] { "Sizzling Fury",          "Ilservian's Flame", "Silencia's Scorn" } },
        { "Cold",      new[] { "Sudden Frost",           "Icy Torment",       "Winter's Embrace" } },
        { "Lightning", new[] { "Lhen's Flare",           "Alset's Coil",      "Luminous Wrath" } },
        { "Acid",      new[] { "Corrosive Flash",        "Disintegration",    "Dissolving Vortex" } },
        { "Blade",     new[] { "Rending Wind",           "Evisceration",      "Sau Kolin's Sword" } },
        { "Slash",     new[] { "Rending Wind",           "Evisceration",      "Sau Kolin's Sword" } },
        { "Pierce",    new[] { "Outlander's Insolence",  "The Spike",         "Stinging Needles" } },
        { "Bludgeon",  new[] { "Cameron's Curse",        "Crushing Shame",    "Pummeling Storm" } },
    };

    private static readonly Dictionary<string, string[]> RingLoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fire",      new[] { "Cassius' Ring of Fire", "Cassius' Ring of Fire II" } },
        { "Cold",      new[] { "Halo of Frost", "Halo of Frost II" } },
        { "Lightning", new[] { "Eye of the Storm", "Eye of the Storm II" } },
        { "Acid",      new[] { "Searing Disc", "Searing Disc II" } },
        { "Blade",     new[] { "Horizon's Blades", "Horizon's Blades II" } },
        { "Slash",     new[] { "Horizon's Blades", "Horizon's Blades II" } },
        { "Pierce",    new[] { "Nuhmudira's Spines", "Nuhmudira's Spines II" } },
        { "Bludgeon",  new[] { "Tectonic Rifts", "Tectonic Rifts II" } },
    };

    private static readonly Dictionary<string, string[]> VoidSpellShapes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Nether", new[] { "Nether Arc", "Nether Ring", "Nether Streak", "Nether Bolt", "Nether Blast" } },
        { "Fire",   new[] { "Corrosion Arc", "Corrosion Ring", "Corrosion Streak", "Nether Bolt", "Nether Blast" } },
    };

    // In-world war/void projectiles are named exactly the spell's shape base
    // (e.g. "Flame Bolt", "Frost Streak"). The object cache sometimes mis-
    // classifies these transient objects as Monster and they carry no health
    // record, so the combat scanner would lock onto the bot's OWN projectile,
    // burn casts on it (no damage possible) and blacklist it. No real
    // attackable monster is ever named one of these — they are excluded from
    // target selection. Built once from the authoritative shape tables.
    private static readonly HashSet<string> ProjectileNames = BuildProjectileNames();

    private static HashSet<string> BuildProjectileNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arr in SpellShapes.Values)
            foreach (var n in arr) set.Add(n);
        foreach (var arr in VoidSpellShapes.Values)
            foreach (var n in arr) set.Add(n);
        return set;
    }

    private static bool IsSpellProjectileName(string? name)
        => !string.IsNullOrEmpty(name) && ProjectileNames.Contains(name);

    public void InitializeRaycasting(string? acFolderPath = null)
    {
        try
        {
            _raycastSystem = new MainLogic();
            if (string.IsNullOrEmpty(acFolderPath)) acFolderPath = @"C:\Turbine\Asheron's Call";
            RaycastInitialized = _raycastSystem.Initialize(acFolderPath);
        }
        catch { RaycastInitialized = false; }
    }

    // Crit detection: AC may send the crit indicator inline ("...critically...")
    // or as a separate line just before the damage line — track the last crit
    // line so a damage line arriving within the window is credited as a crit.
    private DateTime _lastCritChatAt = DateTime.MinValue;
    private const double CRIT_WINDOW_MS = 600;
    private int _dmgDbgCount;

    /// <summary>
    /// Parse OUR outgoing damage out of the combat-log chat and feed it to the
    /// per-monster crit/non-crit learning. War magic sends NO AttackerNotification
    /// (0x01B1), so the chat line "You blast X for N points of fire damage!" is the
    /// only damage source for casters — this is what fills the Crit/NonCrit columns.
    /// MAGIC-ONLY: melee/missile get the structured 0x01B1 event (OnCombatDamage),
    /// so parsing chat for them too would double-count.
    /// </summary>
    // "Olthoi Swarm Harvester blisters your lower leg for 12 points of acid damage!"
    internal static readonly System.Text.RegularExpressions.Regex IncomingDamage = new(
        @"^(?:Critical hit!\s*)?(?:The )?(?<name>.+?) \S+ (?:you|your [a-z ]+?) for (?<n>\d+) points? of (?<type>[a-z]+) damage",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string ElementFromWord(string w) => w.ToLowerInvariant() switch
    {
        "slashing" or "slash" => "Slash",
        "piercing" or "pierce" => "Pierce",
        "bludgeoning" or "bludgeon" => "Bludgeon",
        "fire" => "Fire",
        "cold" => "Cold",
        "acid" => "Acid",
        "electric" or "lightning" => "Lightning",
        "nether" => "Nether",
        _ => "Physical",
    };

    /// <summary>The wcid of a monster we've seen by <paramref name="name"/>, else the current fight's when the name matches it.</summary>
    private uint WcidForName(string name)
    {
        if (_seenMonsterNameToWcid.TryGetValue(name, out uint w)) return w;
        if (_fightTargetWcid != 0 && name.Equals(_fightTargetName, StringComparison.OrdinalIgnoreCase)) return _fightTargetWcid;
        return 0;
    }

    /// <summary>
    /// Misses and damage taken, for the Damage tab's detail panel. A miss is an attack the
    /// monster evaded ("X evaded your attack.") or a spell it resisted ("X resists your
    /// spell"), counted against the weapon in hand. Damage taken comes from the monster's
    /// hit lines, which name it (the engine's defender event doesn't).
    /// </summary>
    private void LearnFromCombatChat(string text)
    {
        int i = text.IndexOf(" evaded your attack", StringComparison.OrdinalIgnoreCase);
        bool resisted = false;
        if (i < 0) { i = text.IndexOf(" resists your spell", StringComparison.OrdinalIgnoreCase); resisted = i >= 0; }
        if (i > 0)
        {
            string who = text.Substring(0, i).Trim();
            if (who.StartsWith("The ", StringComparison.Ordinal)) who = who.Substring(4);
            uint wcid = WcidForName(who);
            uint weapon = resisted || CurrentCombatMode == CombatMode.Magic
                ? _lastCastWeaponId : unchecked((uint)_equippedWeaponId);
            if (wcid != 0) _damageStore!.RecordMiss(weapon, wcid, who);
            return;
        }

        if (text.StartsWith("You ", StringComparison.Ordinal)) return;   // our own hits and evades
        var m = IncomingDamage.Match(text);
        if (!m.Success) return;
        string name = m.Groups["name"].Value.Trim();
        uint src = WcidForName(name);
        if (src == 0) return;
        if (int.TryParse(m.Groups["n"].Value, out int amount))
            _damageStore!.RecordTaken(src, name, ElementFromWord(m.Groups["type"].Value), amount);
    }

    public void HandleChatForDamage(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // A miss is a swing too (stall watchdog). "You evaded X!" is the monster's miss, not ours.
        if (text.IndexOf(" evaded your attack", StringComparison.OrdinalIgnoreCase) > 0)
            NoteSwingEvidence("miss");
        NoteAttackReachedFromChat(text);
        if (_damageStore == null) return;
        try { LearnFromCombatChat(text); } catch { }

        DateTime now = DateTime.Now;
        bool critLine = text.IndexOf("critical", StringComparison.OrdinalIgnoreCase) >= 0;
        if (critLine) _lastCritChatAt = now;

        if (!TryParseOutgoingDamage(text, out int amount)) return;
        bool crit = critLine || (now - _lastCritChatAt).TotalMilliseconds <= CRIT_WINDOW_MS;

        // Attribute to the current fight; if it's already cleared (we one-shot and
        // moved on before the damage line landed), fall back to the last-fight
        // snapshot — same wcid as the bolt, so the crit/non-crit average is right.
        bool cur  = _fightTargetWcid != 0;
        uint wcid = cur ? _fightTargetWcid : _lastFightWcid;

        if (_dmgDbgCount < 15)
        {
            _dmgDbgCount++;
            _host.Log($"[DmgDbg] amt={amount} crit={crit} wcid={wcid} mode={CurrentCombatMode} text='{text}'");
        }

        // MAGIC-ONLY: melee/missile get the structured 0x01B1 event (OnCombatDamage),
        // so parsing chat for them too would double-count.
        if (CurrentCombatMode != CombatMode.Magic || wcid == 0) return;

        // Feed the kill-shot predictor. War magic gets no 0x01B1, so this chat
        // line is the ONLY damage signal for casters — without accumulating it,
        // _fightDamage stayed 0: EvaluateKillShot path B always saw a full-HP
        // target and RecordKill(totalDamage:0) never learned HP pools.
        if (cur)
            _fightDamage += amount;

        uint   weapon = cur ? _lastCastWeaponId : _lastFightWeaponId;
        string nm     = cur ? _fightTargetName  : _lastFightName;
        string elem   = cur ? _lastCastElement  : _lastFightElement;
        int    tier   = cur ? _lastCastTier     : _lastFightTier;
        _damageStore.RecordHit(weapon, wcid, nm, elem, tier, amount, crit);
    }

    /// <summary>
    /// Extract our outgoing damage amount from a combat-log line. Two wordings reach us:
    ///   • Retail combat log:  "You blast X for 153 points of fire damage!"
    ///   • ACE war/void magic: "You blast X for 153 points with Whirling Blade VII."
    ///       (ACEmulator's SpellProjectile.DamageTarget emits "for {amount} points with
    ///        {Spell.Name}" — NOT "points of <element> damage" — so the old retail-only
    ///        parser matched nothing on ACE and the Crit/NonCrit columns stayed empty.)
    /// Returns false for incoming ("... you for N points ...") and non-damage
    /// "points of &lt;vital&gt;" lines (heals / mana / stamina drains). Allocation-free (hot path).
    /// </summary>
    internal static bool TryParseOutgoingDamage(string text, out int amount)
    {
        amount = 0;

        // Anchor on the word "points" (shared by both wordings). The number is the run
        // of digits immediately before it; the word right AFTER it disambiguates:
        //   "points with ..."             → ACE damage cast (only the projectile msg uses this)
        //   "points of ... damage"        → retail damage
        //   "points of mana/stamina/..."  → vital drain/heal (no "damage") → reject
        int pts = text.IndexOf("points", StringComparison.OrdinalIgnoreCase);
        if (pts < 0) return false;

        int w = pts + 6;                                   // first char after "points"
        while (w < text.Length && text[w] == ' ') w++;
        bool nextWith = WordAt(text, w, "with");
        bool nextOf   = WordAt(text, w, "of");
        bool isDamage = nextWith
                     || (nextOf && text.IndexOf("damage", w, StringComparison.OrdinalIgnoreCase) >= 0);
        if (!isDamage) return false;

        // Incoming (we got hit) lines read "... you for N points ..." — not our damage.
        if (text.IndexOf("you for", StringComparison.OrdinalIgnoreCase) >= 0) return false;

        int i = pts - 1;
        while (i >= 0 && text[i] == ' ') i--;
        long val = 0, mult = 1; bool any = false;
        while (i >= 0 && (char.IsDigit(text[i]) || text[i] == ','))
        {
            if (text[i] != ',') { val += (text[i] - '0') * mult; mult *= 10; any = true; }
            i--;
        }
        if (!any || val <= 0 || val > 1_000_000) return false;
        amount = (int)val;
        return true;
    }

    /// <summary>Case-insensitive whole-word match of <paramref name="word"/> (passed lowercase)
    /// at <paramref name="pos"/> in <paramref name="text"/>, bounded by end-of-string or a
    /// non-letter. Allocation-free.</summary>
    private static bool WordAt(string text, int pos, string word)
    {
        if (pos < 0 || pos + word.Length > text.Length) return false;
        for (int k = 0; k < word.Length; k++)
            if (char.ToLowerInvariant(text[pos + k]) != word[k]) return false;
        int end = pos + word.Length;
        return end >= text.Length || !char.IsLetter(text[end]);
    }

    public void HandleChatForDebuffs(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Offensive (war/void) cast resolution — runs independent of the debuff
        // gate (this handler is called for every chat line). Mirrors
        // BuffManager's categorisation so a war spell the char can't actually
        // cast gets marked unresolvable and FindBestOffensiveSpellId drops to
        // an alternative, instead of silently re-firing the dead spell forever.
        if (_pendingOffensiveSpellId != 0)
        {
            string lo = text.ToLowerInvariant();

            // "You're too busy!" — server refused the cast mid-gesture (a
            // previous cast is still animating). NOT a real cast: must NOT
            // count toward the blacklist miss streak. Clear pending silently
            // so JudgePendingOffensiveCast's 500ms no-too-busy window stays
            // honest, and the next attempt can be issued under the throttle.
            if (lo.Contains("you're too busy") || lo.Contains("you are too busy"))
            {
                _pendingOffensiveSpellId   = 0;
                _pendingOffensiveTargetId  = 0;
                // The cast was REFUSED (a prior gesture is still animating) — it
                // never fired, so undo the casts-to-kill increment that issuance
                // optimistically added. The blacklist counter already excludes
                // refusals; without this, casts-to-kill reads >1 on pure one-shots.
                if (_fightCastCount > 0) _fightCastCount--;
                // Cancel any armed predicted kill-shot swap — the cast that armed
                // it was refused, so the mob is still alive: keep + retry it
                // instead of advancing. This is the core of the anti-wedge fix.
                _predictKillSwapArmed = false;
            }
            // Hard reject: no components / unknown / no target. Same phrases the
            // buff path treats as a hard rejection. Mark unresolvable so the
            // tier loop drops down (faster than waiting out the no-chat valve).
            else if (lo.Contains("missing some required") ||
                lo.Contains("you do not have the") ||
                lo.Contains("have all the components for this spell"))
            {
                _spellManager?.MarkSpellUnresolvable(_pendingOffensiveSpellId);
                _host.Log($"[CombatCast] NO-COMPONENTS id={_pendingOffensiveSpellId} " +
                          $"'{SpellTableStub.GetById(_pendingOffensiveSpellId)?.Name}' — '{text.Trim()}'. " +
                          $"Marked unresolvable → tiering down to an alternative.");
                _pendingOffensiveSpellId = 0;
                _predictKillSwapArmed = false; // the cast never went out — don't swap off a live mob
            }
            else
            {
                // Success / fizzle / resisted ⇒ the char DOES know it and has
                // components — clear pending, never mark. Match the cast id to
                // ours so a debuff's "ou cast" can't falsely clear it; on a
                // name-lookup miss, leave pending (the no-chat valve backstops).
                int ci = text.IndexOf("ou cast ", StringComparison.OrdinalIgnoreCase);
                if (ci >= 0)
                {
                    string after = text.Substring(ci + 8);
                    int onI = after.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                    int cI  = after.IndexOf(',');
                    int e   = after.Length;
                    if (onI > 0) e = onI;
                    if (cI > 0 && cI < e) e = cI;
                    int castId = SpellDatabase.GetIdByName(after.Substring(0, e).Trim());
                    if (castId == _pendingOffensiveSpellId)
                    {
                        // ACE doesn't emit "You cast X" for offensive war
                        // magic, so on ACE this branch is effectively dead —
                        // the successful-cast detection happens via
                        // JudgePendingOffensiveCast (no "too busy" within
                        // OffensiveRefusalWindowMs of the issue). This branch
                        // is kept so forks/retail that DO emit chat still
                        // clear pending; it must NOT itself call
                        // RecordOffensiveCast or the cast would double-count.
                        _pendingOffensiveSpellId   = 0;
                        _pendingOffensiveTargetId  = 0;
                    }
                }
                else if (lo.Contains("fizzle") || lo.Contains("resists your spell"))
                {
                    _pendingOffensiveSpellId = 0;
                    _predictKillSwapArmed = false; // cast didn't land — don't swap off a live mob
                }
            }
        }

        if (!_waitingForDebuffResult) return;

        // AC strips the leading 'Y' from cast-confirmation chat — text arrives as
        // "ou cast …". IndexOf matches both "You cast" and "ou cast".
        if (text.IndexOf("ou cast ", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string key = $"{_pendingDebuffTargetId}_{_pendingDebuffKey}";
            _confirmedDebuffs.Add(key);
            _waitingForDebuffResult = false;
            _lastSpellCast = DateTime.Now;
            return;
        }

        if (text.IndexOf("fizzle", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            NoteDebuffFailed("Fizzled");
            return;
        }

        if (text.IndexOf("resists your spell", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            NoteDebuffFailed("Resisted");
            return;
        }

        // Refused mid-gesture: the debuff never went out, so don't let the result timeout
        // mark it as landed. Retry it (counted, so a refusal loop can't run forever).
        if (text.IndexOf("you're too busy", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("you are too busy", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            NoteDebuffFailed("Refused");
            return;
        }
    }

    /// <summary>
    /// A debuff didn't land (fizzle, resist, refusal). Recast it, but only up to
    /// DebuffMaxFailures times per target: a mob the character's Creature Magic can't land
    /// on used to get the same debuff forever, and the bot never reached its attack spells
    /// (debuffs don't feed the no-damage blacklist either).
    /// </summary>
    private void NoteDebuffFailed(string what)
    {
        string key = $"{_pendingDebuffTargetId}_{_pendingDebuffKey}";
        _debuffFailures.TryGetValue(key, out int n);
        _debuffFailures[key] = ++n;
        if (n >= DebuffMaxFailures)
        {
            _confirmedDebuffs.Add(key);   // skip it for this target
            _host.WriteToChat($"[RynthAi] {what}: {_pendingDebuffKey} — {n} tries on this target, skipping it", 2);
        }
        else
        {
            _host.WriteToChat($"[RynthAi] {what}: {_pendingDebuffKey} — recasting", 2);
        }
        _waitingForDebuffResult = false;
        _lastSpellCast = DateTime.Now;
    }

    public void ReportDamageOnTarget(int targetId)
    {
        // Damage / health change on the cast we're judging → that cast HIT.
        if (targetId == _pendingJudgeTargetId)
            _damageSincePendingCast = true;
        // Immediate positive feedback on the engaged target clears the streak.
        if (targetId == _lastAttackedTargetId)
            _consecutiveCastMisses = 0;
        _blacklistManager.ClearFailure(targetId);
        if (targetId == activeTargetId || targetId == _lockedTargetId)
            _lastDamageDealtAt = DateTime.Now;
    }

    /// <summary>
    /// "X evaded your attack." and "X resists your spell" mean the attack reached X, so they
    /// count as feedback for the no-damage blacklist (not as damage: no commitment bonus, no
    /// no-progress reset). Before this an evasive or resistant mob that was fighting the bot
    /// got blacklisted and dropped after three windows of evades (~6 s of melee), and with
    /// nothing else in range the bot went idle and walked on while it kept hitting her.
    /// </summary>
    private void NoteAttackReachedFromChat(string text)
    {
        int i = text.IndexOf(" evaded your attack", StringComparison.OrdinalIgnoreCase);
        if (i < 0) i = text.IndexOf(" resists your spell", StringComparison.OrdinalIgnoreCase);
        if (i <= 0) return;
        string who = text.Substring(0, i).Trim();
        if (who.StartsWith("The ", StringComparison.Ordinal)) who = who.Substring(4);
        if (who.Length == 0) return;

        if (_pendingJudgeCastAt != DateTime.MinValue && NameIs(_pendingJudgeTargetId, who))
            _damageSincePendingCast = true;
        if (NameIs(_lastAttackedTargetId, who))
            _consecutiveCastMisses = 0;
    }

    private bool NameIs(int id, string name)
        => id != 0 && string.Equals(_worldFilter[id]?.Name, name, StringComparison.OrdinalIgnoreCase);

    private void NoteSwingEvidence(string kind)
    {
        DateTime now = DateTime.Now;
        _lastSwingEvidenceAt = now;
        _lastSwingEvidenceKind = kind;
        switch (kind)
        {
            case "hit":  _lastHitAt  = now; break;
            case "miss": _lastMissAt = now; break;
            case "kill": _lastKillAt = now; break;
        }
    }

    /// <summary>True only when the mob is known to be dead (kill notice, 0 health, corpse).
    /// A mob that is merely out of view may still hold the server's attack, so it's not.</summary>
    private bool IsKnownDead(int id)
    {
        if (id == 0 || _recentlyKilled.ContainsKey(id)) return true;
        if (_worldFilter.GetHealthRatio(id) == 0f) return true;
        var wo = _worldFilter[id];
        return wo != null && (int)wo.ObjectClass != (int)AcObjectClass.Monster;
    }

    /// <summary>
    /// End the server's melee/missile attack (CancelAttack → ACE HandleActionCancelAttack →
    /// OnAttackDone clears MeleeTarget; mid-swing it ends after the swing). The next attack
    /// request then starts a fresh loop on the target the bot actually wants.
    /// </summary>
    private void ReleaseServerAttack(string why, bool log)
    {
        int old = _serverAttackTargetId;
        _serverAttackTargetId = 0;
        if (!_host.HasCancelAttack) return;
        _host.CancelAttack();
        if (!log) return;

        DateTime now = DateTime.Now;
        if ((now - _lastRebindLogAt).TotalMilliseconds < 2000) { _rebindLogSuppressed++; return; }
        string more = _rebindLogSuppressed > 0 ? $" (+{_rebindLogSuppressed} more since last line)" : "";
        _rebindLogSuppressed = 0;
        _lastRebindLogAt = now;
        string oldDesc = old == 0 ? "none" : $"0x{old:X8} '{_worldFilter[old]?.Name}' {DescribeTargetPos(old)}";
        _host.Log($"[AttackRebind] CancelAttack — server attack was on {oldDesc}; {why}; attacking again next cycle{more}");
    }

    /// <summary>
    /// True if a monster's name matches the user-configured never-attack list
    /// (case-insensitive substring, so "Drudge" excludes every drudge). Blank
    /// entries are ignored. This is the manual do-not-attack list and is distinct
    /// from the automatic no-damage blacklist.
    /// </summary>
    private bool IsUserBlacklistedName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var list = _settings.MonsterNameBlacklist;
        if (list == null || list.Count == 0) return false;
        for (int i = 0; i < list.Count; i++)
        {
            string entry = list[i];
            if (!string.IsNullOrWhiteSpace(entry) &&
                name.IndexOf(entry.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// True during the post-login window where raycasting is enabled but the
    /// DAT parse hasn't finished, so combat targeting runs degraded (no LOS,
    /// Linear attack-type fallback). Attacks miss for reasons unrelated to the
    /// target — blacklist accrual must be suppressed here, otherwise the first
    /// mobs after login get sidelined for BlacklistTimeoutSec (default 300s)
    /// and nav runs the bot straight past them.
    /// </summary>
    private bool RaycastWarmingUp => _settings.EnableRaycasting && !RaycastInitialized;

    /// <summary>
    /// Judge the cast awaiting a verdict. A cast becomes a "miss" only once
    /// BlacklistCastSettleMs has elapsed with no damage on the target — so the
    /// damage/health packet has time to land and blacklisting is driven by
    /// confirmed no-damage casts, never wall-clock time. BlacklistAttempts
    /// consecutive misses → blacklist. Called every combat cycle and before
    /// recording a new cast so the last cast is still judged.
    /// </summary>
    private void JudgePendingCast()
    {
        if (_pendingJudgeCastAt == DateTime.MinValue) return;
        if (RaycastWarmingUp) { _pendingJudgeCastAt = DateTime.MinValue; return; }
        if ((DateTime.Now - _pendingJudgeCastAt).TotalMilliseconds < _settings.BlacklistCastSettleMs)
            return; // not settled yet — give the damage/health packet time

        int tid  = _pendingJudgeTargetId;
        bool hit = _damageSincePendingCast;
        _pendingJudgeCastAt = DateTime.MinValue;

        if (hit) { _consecutiveCastMisses = 0; return; }

        _consecutiveCastMisses++;
        if (_consecutiveCastMisses >= _settings.BlacklistAttempts)
        {
            _blacklistManager.ReportFailure(tid);
            if (_blacklistManager.IsBlacklisted(tid))
            {
                string what = CurrentCombatMode == CombatMode.Magic ? "casts" : "attacks";
                _host.WriteToChat($"[RynthAi] {_consecutiveCastMisses} {what} with no damage — blacklisting 0x{(uint)tid:X8}", 2);
                _consecutiveCastMisses = 0;
                if (tid == activeTargetId)
                    DropTarget($"blacklisted after {_settings.BlacklistAttempts} no-damage {what}");
            }
        }
    }

    /// <summary>
    /// ACE doesn't emit "You cast X" chat for offensive war magic, so the
    /// offensive cast-confirmation works by NEGATION: cast issuance sets
    /// _pendingOffensiveSpellId/CastAt/TargetId; if a "You're too busy!" chat
    /// hits within OffensiveRefusalWindowMs the chat handler clears pending
    /// silently (server refused, don't count); otherwise this method runs
    /// once that window has elapsed and queues the cast for blacklist
    /// judgement (the BlacklistCastSettleMs damage-wait then runs in
    /// JudgePendingCast as usual). Effect: refused spam attempts never reach
    /// _consecutiveCastMisses.
    /// </summary>
    private void JudgePendingOffensiveCast()
    {
        if (_pendingOffensiveSpellId == 0) return;
        if (_pendingOffensiveTargetId == 0) return;
        if (_pendingOffensiveCastAt == DateTime.MinValue) return;
        if ((DateTime.Now - _pendingOffensiveCastAt).TotalMilliseconds < OffensiveRefusalWindowMs)
            return;

        // Refusal window passed without a "too busy" — the server accepted
        // the gesture. Queue this cast for blacklist judgement.
        int targetId = _pendingOffensiveTargetId;
        _pendingOffensiveSpellId   = 0;
        _pendingOffensiveTargetId  = 0;
        _pendingOffensiveCastAt    = DateTime.MinValue;
        RecordOffensiveCast(targetId);
    }

    /// <summary>
    /// Record that an attack (offensive spell cast / weapon swing) was just
    /// issued at <paramref name="targetId"/>. The prior attack is judged first;
    /// a new judgement window is then armed ONLY if none is still in flight.
    /// Not clobbering an unsettled window is what makes melee/missile count at
    /// all: those modes re-attack every 1000ms (attackCmdIntervalMs) while the
    /// settle window is BlacklistCastSettleMs (default 1500ms), so resetting the
    /// timer every cycle meant the pending attack never settled and the miss
    /// streak never advanced — only magic (≥1500ms cadence) ever blacklisted.
    /// Now each attack window runs to settle, so the blacklist is driven by
    /// "N real attacks with no damage" for casts, missile shots, and melee
    /// swings alike — never raw interval ticks.
    /// </summary>
    private void RecordOffensiveCast(int targetId)
    {
        if (_lastAttackedTargetId != targetId)
        {
            // New target — reset streak + drop any stale pending judgement.
            _lastAttackedTargetId = targetId;
            _consecutiveCastMisses = 0;
            _pendingJudgeCastAt = DateTime.MinValue;
        }

        // Stamped on EVERY offensive action, not just a target change, so the
        // cast-issue commitment window tracks the most recent cast. Stamping only
        // on change would expire commitment mid-fight while still casting at the
        // same mob — reintroducing the in-flight abandonment this exists to stop.
        _lastAttackedAt = DateTime.Now;

        JudgePendingCast(); // verdict on the prior attack if its settle window elapsed

        // An attack is still awaiting its verdict — let it settle instead of
        // restarting the clock (the fast-cadence starvation fix). JudgePendingCast
        // clears _pendingJudgeCastAt on settle, so the next attack re-arms here.
        if (_pendingJudgeCastAt != DateTime.MinValue) return;

        _pendingJudgeCastAt   = DateTime.Now;
        _pendingJudgeTargetId = targetId;
        _damageSincePendingCast = false;
    }

    /// <summary>
    /// Take a just-killed (or predicted-killed) monster out of the current scan list. The scan
    /// skips <see cref="_recentlyKilled"/> ids, but it runs at most every SCAN_INTERVAL_MS and the
    /// kill notice arrives between ticks, so the next tick's target pick still saw the dead mob
    /// in the old list and locked it again (score ~140: closest and wounded) — a "lock" of the
    /// corpse-to-be right after "DropTarget ... killed", dropped one tick later as "hp=0". On
    /// every kill in the 2026-10-01/10-04 Olthoi logs; a cast or swing could go at it.
    /// </summary>
    private void ForgetScanned(int id)
    {
        for (int i = _scannedTargets.Count - 1; i >= 0; i--)
            if (_scannedTargets[i].Id == id) _scannedTargets.RemoveAt(i);
    }

    private void DropTarget(string reason)
    {
        if (activeTargetId != 0)
            _host.Log($"[RynthAi] DropTarget 0x{activeTargetId:X8}: {reason} [{DescribeTargetPos(activeTargetId)}]");
        activeTargetId = 0;
        _lockedTargetId = 0;
        // A turn held toward the dropped target must be let go, or the character keeps
        // spinning until something else happens to release it.
        if (_facingTarget) ClearCombatTurnMotions();
        _facingTarget = false;
        _returnToPhysicalCombat = false;
        _targetLockedAt    = DateTime.MinValue;
        _lastDamageDealtAt = DateTime.MinValue;
        _lockedLastScore      = double.MinValue;
        _lockedLastSeenInScan = DateTime.MinValue;
        _targetLostScanTime = DateTime.MinValue;
        _pendingJudgeCastAt = DateTime.MinValue;
        _consecutiveCastMisses = 0;
        // Reset kill-shot fight tracking — the next acquired target starts fresh.
        _fightTargetId = 0;
        _fightTargetWcid = 0;
        _fightTargetName = "";
        _fightTargetMaxHp = 0;
        _fightHpEstimated = false;
        _fightDamage = 0;
        _fightCastCount = 0;
        _fightAppraiseRequested = false;
        _predictKillSwap = false;
        _predictKillSwapArmed = false;
        // Clear the stance-stuck timer — it arms during the normal NonCombat→Magic
        // window at fight start, and if the target drops in that window a surviving
        // timer makes the NEXT engagement's first equip tick see minutes of "stuck"
        // and fire a spurious recovery (busy force-clear + UseObject on a wielded wand).
        ResetStanceRecovery();
        // Defer the turn-stop while a targeted cast is still resolving: SetMotion(turn,false)
        // here truncates the in-flight cast gesture → ACE never finishes the cast → server
        // Player.IsBusy stranded (relog-only wedge). The target drop above still happens; only
        // the stop-thunk waits, bounded by IsCastInFlight (UseDone-seq or 2.5s); re-issued next tick.
        if (!IsCastInFlight()) ClearCombatTurnMotions();
    }

    /// <summary>
    /// Execute a predicted kill-shot swap that was ARMED at cast time and is now
    /// CONFIRMED accepted (ACE: refusal window elapsed with no "You're too
    /// busy!"). Deferring the drop until confirmation is what stops a refused
    /// cast from abandoning a live mob and churning the swarm — the behaviour
    /// that pinned AC's m_cBusy and wedged combat-mode / cast input. Body is the
    /// old immediate-swap path, unchanged.
    /// </summary>
    private void ExecuteDeferredKillSwap()
    {
        _predictKillSwapArmed = false;
        int swapId = activeTargetId;
        if (swapId == 0) return;

        _recentlyKilled[swapId] = DateTime.Now.AddMilliseconds(PREDICTED_SWAP_SUPPRESS_MS);
        ForgetScanned(swapId);
        // Queue this predicted one-shot so it's CREDITED when its death confirms —
        // we may swap away before its KillerNotification. Capture the name LIVE
        // from the world filter (_fightTargetName is often empty this early).
        string pkName = _worldFilter[swapId]?.Name ?? _fightTargetName;
        if (_fightTargetWcid != 0 && _fightCastCount > 0 && !string.IsNullOrEmpty(pkName))
        {
            DateTime nowSwap = DateTime.Now;
            _predictedKillPending.RemoveAll(p => p.Expiry < nowSwap);
            _predictedKillPending.Add(new PendingKill(
                pkName, _fightTargetWcid, _fightCastCount,
                _lastCastWeaponId, _lastCastElement, _lastCastTier, _fightDamage,
                DateTime.Now.AddSeconds(5), _fightStartedAt, _fightSummon, swapId));
        }
        _host.Log($"[RynthAi] Predicted kill shot 0x{swapId:X8} '{_worldFilter[swapId]?.Name}' (cast#{_fightCastCount} hp~{_fightTargetMaxHp:0} dealt~{_fightDamage:0}) — confirmed accepted, swapping to next target.");
        // The pending-offensive judge state belongs to the mob being dropped —
        // clearing it stops JudgePendingOffensiveCast from recording this cast
        // against the dead target on the next attack block.
        _pendingOffensiveSpellId = 0;
        _pendingOffensiveTargetId = 0;
        DropTarget("predicted kill — swap (confirmed)");
    }

    /// <summary>
    /// Handle ACE's KillerNotification (GameEvent 0x01AD), forwarded by the
    /// engine with the formatted death message. The server sends it to the
    /// killer at the lethal hit — earlier than the health=0 update and the
    /// Monster→Corpse flip combat otherwise waits on (ACE delays CreateCorpse
    /// by the death-animation length, so those land 1–3s later). If the death
    /// message names our active target — or we can't read its name — drop it
    /// now and briefly suppress re-acquiring that id, so combat advances to the
    /// next target instead of firing a second full cast at a corpse-to-be.
    /// The notification is sent ONLY to the killer, so receiving it means WE
    /// killed something; combat engages one target at a time, so the active
    /// target is the victim. A non-matching name means a different creature
    /// died (AoE/DoT) — we leave the live target alone.
    /// Runs on the same pump thread as Think()/ScanNearbyTargets (no locking).
    /// </summary>
    public void OnKillNotification(string deathMessage)
    {
        NoteSwingEvidence("kill");
        CastsSinceLastKill = 0;   // D6: a kill landed (KillerNotification → the killer) — reset the orphan signal.
        // Record EXACTLY ONE kill per death message, attributed by name (→ correct
        // wcid) from the best available source, so rapid one-shots aren't lost when
        // the fight state has already been cleared/advanced by the time the corpse's
        // KillerNotification lands. Three tiers, most-specific first.
        if (_damageStore == null) return;
        DateTime nowKill = DateTime.Now;
        _predictedKillPending.RemoveAll(p => p.Expiry < nowKill);

        bool recorded = false;
        string how = "miss";
        int pendingKillId = 0;   // id of the predicted one-shot this notice was credited to (tier 1)

        // (1) A predicted one-shot we swapped away from — captured wcid + cast count.
        for (int i = 0; i < _predictedKillPending.Count; i++)
        {
            if (!string.IsNullOrEmpty(_predictedKillPending[i].Name)
                && deathMessage.IndexOf(_predictedKillPending[i].Name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                PendingKill pk = _predictedKillPending[i];
                _predictedKillPending.RemoveAt(i);
                pendingKillId = pk.Id;
                // A summon's damage isn't in pk.Damage, so that total would learn the HP too low.
                _damageStore.RecordKill(pk.WeaponId, pk.Wcid, pk.Name, pk.Element, pk.Tier, pk.CastCount,
                                        pk.Summon != null ? 0 : pk.Damage);
                if (pk.Started != default)
                    _damageStore.RecordFightTime(pk.WeaponId, pk.Wcid, pk.Name, (nowKill - pk.Started).TotalSeconds, pk.Summon ?? "none");
                recorded = true; how = "pending";
                break;
            }
        }

        int tid = activeTargetId;
        string curName = tid != 0 ? (_worldFilter[tid]?.Name ?? "") : "";
        bool curMatches = curName.Length > 0
                          && deathMessage.IndexOf(curName, StringComparison.OrdinalIgnoreCase) >= 0;

        // (2) The current target died (its name is in the message).
        if (!recorded && curMatches && _fightTargetWcid != 0 && _fightCastCount > 0)
        {
            string? summon = _fightSummon ?? SummonOut?.Invoke();
            _damageStore.RecordKill(_lastCastWeaponId, _fightTargetWcid, curName, _lastCastElement, _lastCastTier,
                                    _fightCastCount, summon != null ? 0 : _fightDamage);
            if (_fightStartedAt != default)
                _damageStore.RecordFightTime(_lastCastWeaponId, _fightTargetWcid, curName,
                                             (nowKill - _fightStartedAt).TotalSeconds, summon ?? "none");
            recorded = true; how = "current";
        }

        // (3) A mob we cast at then moved on from (state already cleared) — credit
        //     it from the persistent snapshot when the name matches. This is the
        //     fix for the rapid-kill under-count.
        if (!recorded && _lastFightWcid != 0 && _lastFightName.Length > 0
            && deathMessage.IndexOf(_lastFightName, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _damageStore.RecordKill(_lastFightWeaponId, _lastFightWcid, _lastFightName, _lastFightElement, _lastFightTier,
                                    _lastFightCount > 0 ? _lastFightCount : 1, 0);
            recorded = true; how = "snapshot";
        }

        // (4) A mob fought further back than one-ago (advanced past several targets) — the
        //     recent-fights ring credits it by name. Closes the post-advance under-count that
        //     the single _lastFight slot drops (KillDbg how=miss). Consumes the matched entry.
        if (!recorded)
        {
            _recentFights.RemoveAll(e => e.Pk.Expiry < nowKill);
            for (int i = _recentFights.Count - 1; i >= 0; i--)
            {
                PendingKill pk = _recentFights[i].Pk;
                if (!string.IsNullOrEmpty(pk.Name)
                    && deathMessage.IndexOf(pk.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _recentFights.RemoveAt(i);
                    _damageStore.RecordKill(pk.WeaponId, pk.Wcid, pk.Name, pk.Element, pk.Tier,
                                            pk.CastCount > 0 ? pk.CastCount : 1, 0);
                    recorded = true; how = "recent";
                    break;
                }
            }
        }

        // (5) AOE/area kill: a mob wiped by a splash cast we never directly fought (tiers 1-4
        //     have no record of it). Credit by matching its death message against the names of
        //     monsters the scanner has seen this session. Longest name wins (most specific), so
        //     "Olthoi Swarm Soldier" isn't mis-credited to a shorter overlapping name.
        if (!recorded && _seenMonsterNameToWcid.Count > 0)
        {
            uint bestWcid = 0; string bestName = ""; int bestLen = 0;
            foreach (var kv in _seenMonsterNameToWcid)
            {
                if (kv.Key.Length > bestLen
                    && deathMessage.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                { bestWcid = kv.Value; bestName = kv.Key; bestLen = kv.Key.Length; }
            }
            if (bestWcid != 0)
            {
                // Attribute to the last cast's shape (the AOE/ring that wiped the pack) so these
                // kills group under that tier (e.g. "R2") instead of an unattributed row. Per-cast
                // damage/casts-to-kill stays meaningless for AOE — we only count the kill.
                _damageStore.RecordKill(_lastCastWeaponId, bestWcid, bestName, _lastCastElement, _lastCastTier, 1, 0);
                recorded = true; how = "aoe";
            }
        }

        if (_killDbgCount < 1000)
        {
            _killDbgCount++;
            _host.Log($"[KillDbg] how={how} curMatch={curMatches} tid=0x{(uint)tid:X8} fightWcid={_fightTargetWcid} cnt={_fightCastCount} lastWcid={_lastFightWcid} pend={_predictedKillPending.Count} msg='{deathMessage}'");
        }

        // Drop the current target only if IT is the one that died. The name alone can't
        // tell same-named mobs apart (a swarm is all "Olthoi Swarm Soldier"), so keep the
        // live target when the notice belongs to the predicted one-shot we swapped away
        // from, or when we haven't attacked the current target at all yet — then a ring or
        // a late death notice killed a neighbour. Dropping it also hid it from the scan for
        // 4 s, and with no other mob in range the bot went to peace mid-fight.
        bool noticeForSwappedMob = pendingKillId != 0 && pendingKillId != tid;
        bool curAttacked = _fightTargetId != tid || _fightCastCount > 0 || _pendingOffensiveTargetId == tid;
        if (tid != 0 && curMatches && (noticeForSwappedMob || !curAttacked))
        {
            _host.Log($"[RynthAi] Kill notice names 0x{tid:X8} '{curName}' but " +
                      (noticeForSwappedMob ? $"belongs to predicted kill 0x{pendingKillId:X8}" : "it hasn't been attacked yet") +
                      " — keeping the target");
        }
        else if (tid != 0 && curMatches)
        {
            _recentlyKilled[tid] = nowKill.AddMilliseconds(RECENTLY_KILLED_SUPPRESS_MS);
            ForgetScanned(tid);
            DropTarget("killed (KillerNotification)");
        }
    }

    /// <summary>Record the just-stamped _lastFight* context into the recent-fights ring,
    /// keyed by the live object id (refreshed per hit, capped, TTL-pruned). Lets a kill that
    /// lands after we've advanced past the dying mob still be credited by name (tier 4).</summary>
    private void StampRecentFight()
    {
        if (_lastFightWcid == 0) return;
        int id = activeTargetId;
        if (id == 0) return;
        var pk = new PendingKill(_lastFightName, _lastFightWcid, _lastFightCount > 0 ? _lastFightCount : 1,
                                 _lastFightWeaponId, _lastFightElement, _lastFightTier, 0,
                                 DateTime.Now.AddMilliseconds(RecentFightTtlMs));
        for (int i = _recentFights.Count - 1; i >= 0; i--)
            if (_recentFights[i].Id == id) _recentFights.RemoveAt(i);
        _recentFights.Add((id, pk));
        if (_recentFights.Count > RecentFightsCap) _recentFights.RemoveAt(0);
    }

    /// <summary>
    /// Exact per-hit damage from the engine (AttackerNotification 0x01B1),
    /// attributed to the active target (combat engages one mob at a time). This
    /// is the only selection-free source of real damage numbers: ACE pushes
    /// creature health only for the SELECTED target, so combat — which never
    /// selects — otherwise can't see how hurt a mob is. Feeds kill-shot
    /// prediction and doubles as reliable blacklist damage feedback.
    /// </summary>
    public void OnCombatDamage(double damage, uint damageType, bool crit, bool isAttacker)
    {
        if (!isAttacker || damage <= 0) return;
        NoteSwingEvidence("hit");   // any target: a swing landing anywhere means the attack loop is alive
        int tid = activeTargetId;
        if (tid == 0 || tid != _fightTargetId) return;

        _fightDamage += damage;

        // War magic sends NO 0x01B1 (its damage comes from the chat parse), so a structured
        // hit event in a NON-magic mode is a MELEE or MISSILE hit. Magic sets its cast context
        // in AttackWithMagic; for melee/missile derive the per-hit context here so the Damage
        // tab keys it correctly: weapon = the equipped weapon, element = the packet damage type
        // (accurate per hit), tier = 0 (no spell tier). The packet crit flag is reliable too.
        if (CurrentCombatMode != CombatMode.Magic)
        {
            _lastCastWeaponId = unchecked((uint)_equippedWeaponId);
            _lastCastElement  = ElementFromDamageType(damageType);
            _lastCastTier     = 0;
            _lastCastWasRing  = false;   // melee/missile are single-target
            _fightCastCount++;           // landed hits → "casts/attacks to kill" learning
            // Snapshot the fight so a kill landing after we've advanced is still credited
            // (OnKillNotification path 3) with the right melee/missile weapon/element.
            if (_fightTargetWcid != 0)
            {
                _lastFightWcid     = _fightTargetWcid;
                _lastFightName     = _fightTargetName;
                _lastFightWeaponId = _lastCastWeaponId;
                _lastFightElement  = _lastCastElement;
                _lastFightTier     = _lastCastTier;
                _lastFightCount    = _fightCastCount;
                StampRecentFight();
            }
        }

        // Don't learn per-cast damage from ring/AoE casts — they hit multiple
        // mobs, so the damage can't be attributed to this single target.
        if (_damageStore != null && _fightTargetWcid != 0 && !_lastCastWasRing)
            _damageStore.RecordHit(_lastCastWeaponId, _fightTargetWcid, _fightTargetName, _lastCastElement, _lastCastTier, damage, crit);

        ReportDamageOnTarget(tid); // clears the blacklist miss streak + stamps last-damage time
    }

    // Map an AC DAMAGE_TYPE flag (from the 0x01B1 packet) to the Damage-tab element string.
    // A weapon hit carries one primary type; if multiple bits are set, prefer the physical base.
    internal static string ElementFromDamageType(uint dt)
    {
        if ((dt & 0x1)   != 0) return "Slash";
        if ((dt & 0x2)   != 0) return "Pierce";
        if ((dt & 0x4)   != 0) return "Bludgeon";
        if ((dt & 0x10)  != 0) return "Fire";
        if ((dt & 0x8)   != 0) return "Cold";
        if ((dt & 0x40)  != 0) return "Lightning";   // DamageType.Electric
        if ((dt & 0x20)  != 0) return "Acid";
        if ((dt & 0x400) != 0) return "Nether";
        return "Physical";                            // Undef / vital drains
    }

    /// <summary>Start tracking a fresh fight against <paramref name="targetId"/>: zero the damage tally, capture its wcid, resolve its HP.</summary>
    private void BeginFight(int targetId)
    {
        _fightTargetId = targetId;
        _fightDamage = 0;
        _fightCastCount = 0;
        _fightTargetWcid = 0;
        _fightTargetMaxHp = 0;
        _fightHpEstimated = false;
        _fightAppraiseRequested = false;
        _predictKillSwap = false;
        _fightTargetName = "";
        _fightStartedAt = default;
        _fightSummon = null;
        if (targetId == 0) return;
        _fightStartedAt = DateTime.Now;
        _fightSummon = SummonOut?.Invoke();
        _fightTargetName = _worldFilter[targetId]?.Name ?? "";
        if (_host.HasGetObjectWcid && _host.TryGetObjectWcid((uint)targetId, out uint wcid))
            _fightTargetWcid = wcid;
        ResolveFightHp();
    }

    /// <summary>
    /// Resolve the active target's HP pool for prediction. Priority:
    ///   1) appraised MaxHealth from the shared creature store (instant once any
    ///      character has assessed this monster type; persists across sessions),
    ///   2) learned damage-to-kill for this wcid (per character),
    ///   3) one appraisal request to populate (1) for next time (best-effort).
    /// Re-called each tick until HP is known (the appraisal lands async).
    /// </summary>
    private void ResolveFightHp()
    {
        if (_fightTargetId == 0) return;

        // Once per fight, query the live target's health. The 0x01C0 response is
        // handled on AC's main thread, where the engine reads the creature's REAL
        // MaxHealth from its qualities (SEH-guarded) and corrects the bogus
        // appraisal stub in creatures.json. Fire this EVEN when we already have a
        // cached (stub) value — otherwise the stub suppresses the query and the
        // real number never gets read.
        if (!_fightAppraiseRequested)
        {
            _fightAppraiseRequested = true;
            try { if (_host.HasQueryHealth) _host.QueryHealth((uint)_fightTargetId); } catch { }
        }

        if (_fightTargetMaxHp > 0 && !_fightHpEstimated) return;

        // Aelrynth's awakened worlds and scaled copies: the same wcid, +5% health a tier.
        // 0 everywhere else, and then this is the old resolution exactly.
        int difficulty = Difficulty;
        double scale = AwakenedTier.HealthScale(difficulty);

        // User-entered HP override (set in the Damage panel) is authoritative —
        // it's the manual fix for ACE's skill-gated appraisal reading low.
        // It is the real-Dereth value; a scaled tier scales it like the server does.
        if (_damageStore != null && _fightTargetWcid != 0)
        {
            double manual = _damageStore.GetManualHp(_fightTargetWcid);
            if (manual > 0) { _fightTargetMaxHp = manual * scale; _fightHpEstimated = false; return; }
        }

        var obj = _worldFilter[_fightTargetId];
        string name = obj?.Name ?? "";

        CreatureProfile? prof = null;
        if (_creatureStore != null && !string.IsNullOrEmpty(name))
        {
            bool found = _fightTargetWcid != 0
                ? _creatureStore.TryGet(name, _fightTargetWcid, out prof)
                : _creatureStore.TryGetByName(name, out prof);
            if (!found) prof = null;
            uint appraised = _creatureStore.MaxHealthAt(prof, difficulty);
            if (appraised > 0)
            {
                _fightTargetMaxHp = appraised;
                _fightHpEstimated = false;
                return;
            }
        }

        if (_damageStore != null && _fightTargetWcid != 0)
        {
            double hp = _damageStore.GetLearnedHp(_fightTargetWcid, difficulty);
            if (hp > 0) { _fightTargetMaxHp = hp; _fightHpEstimated = false; return; }
        }

        // A scaled tier never seen yet: tier 0's HP x (1 + 5% x tier) is what the server does
        // (Scaling.cs raises the written-in health and the attributes it derives from by that
        // factor), so it stands in until this tier's own appraisal lands (QueryHealth above,
        // usually within the first second) and replaces it. Casts-to-kill is not estimated:
        // only this tier's own kills count for that (MonsterDamageStore).
        if (difficulty > 0)
        {
            double baseHp = prof != null && prof.MaxHealth > 0 ? prof.MaxHealth
                          : _damageStore != null && _fightTargetWcid != 0 ? _damageStore.GetLearnedHp(_fightTargetWcid, 0) : 0;
            if (baseHp > 0)
            {
                _fightTargetMaxHp = baseHp * scale;
                _fightHpEstimated = true;
                return;
            }
        }
        if (_fightHpEstimated) return;   // keep the estimate while the appraisal is pending

        // Unknown — appraise once so the shared store fills for next time. Costs
        // one busy-count tick; only the first encounter of a wcid pays it.
        if (!_fightAppraiseRequested)
        {
            _fightAppraiseRequested = true;
            try { if (_host.HasRequestId)  _host.RequestId((uint)_fightTargetId); }  catch { }
            try { if (_host.HasQueryHealth) _host.QueryHealth((uint)_fightTargetId); } catch { }
        }
    }

    /// <summary>
    /// After firing an offensive cast at the active target, decide whether that
    /// cast is expected to finish the mob. If so — and another target is in
    /// range — flag a swap so Think drops this mob instead of wasting the next
    /// cast during the projectile's flight. Ring/AoE casts are excluded (their
    /// damage hits multiple mobs, so single-target accounting doesn't apply).
    /// </summary>
    private void EvaluateKillShot(uint weaponId, string element, int tier, bool isRing)
    {
        _predictKillSwap = false;
        if (isRing || _damageStore == null || _fightTargetWcid == 0) return;
        if (activeTargetId == 0 || activeTargetId != _fightTargetId) return;
        if (!HasAlternateTarget(activeTargetId)) return; // nothing to swap to — keep casting

        // (A) Casts-to-kill: we've fired the number of casts this wcid typically
        //     takes to die from this weapon+spell. Catches ONE-SHOTS (avg≈1 → swap
        //     right after cast 1), which the damage math below can't — nothing has
        //     landed when we predict, so it has no remaining-HP signal yet.
        double avgCasts = _damageStore.GetAvgCastsToKill(weaponId, _fightTargetWcid, element, tier, out int killSamples);
        if (killSamples >= KILL_MIN_SAMPLES && avgCasts > 0
            && _fightCastCount >= (int)Math.Round(avgCasts))
        {
            _predictKillSwap = true;
            return;
        }

        // (B) Damage-based: remaining HP is within one comfortable cast. Precise
        //     for multi-cast fights once some damage has landed.
        if (_fightTargetMaxHp > 0)
        {
            double expected = _damageStore.GetAvgDamage(weaponId, _fightTargetWcid, element, tier, out int dmgSamples);
            double remaining = _fightTargetMaxHp - _fightDamage;
            if (dmgSamples >= KILL_MIN_SAMPLES && expected > 0 && remaining <= expected * KILL_CONFIDENCE)
                _predictKillSwap = true;
        }
    }

    /// <summary>True if a scanned, non-suppressed target other than <paramref name="excludeId"/> is available to swap to.</summary>
    private bool HasAlternateTarget(int excludeId)
    {
        for (int i = 0; i < _scannedTargets.Count; i++)
        {
            int id = _scannedTargets[i].Id;
            if (id == excludeId) continue;
            if (_recentlyKilled.ContainsKey(id)) continue;
            return true;
        }
        return false;
    }

    /// <summary>Floor between attack commands for the first cast at a new target when the server
    /// gates are available (see <see cref="AttackIntervalMs"/>). Only bounds how fast a refused
    /// cast could be retried; a refusal sets the target as the last attacked, so the retry waits
    /// the full interval anyway.</summary>
    internal const double NewTargetCastFloorMs = 300;

    /// <summary>True when <see cref="BusyCount"/> is positive only because AC lowered it inline at a
    /// server UseDone the busy hook can't see (see <see cref="CombatBusyGate"/>).</summary>
    internal bool BusyIsLeftover()
    {
        bool gesture = _host.HasGetCastBusyState && !_host.CanCastNow;
        return _busyGate.IsStale(BusyCount, _host.HasUseDoneSeq,
            _host.HasUseDoneSeq ? _host.GetUseDoneSeq() : 0, gesture);
    }

    /// <summary>Times combat found the busy count was a leftover and went ahead (status/tests).</summary>
    internal int StaleBusyResets => _staleBusyResets;
    private int _staleBusyResets;

    /// <summary>
    /// The interval the combat tick waits after an attack command before the next one.
    /// Melee/missile: 1000 ms (AC paces swings and shots itself). Magic: the Attack Spell Delay
    /// setting (≤0 = 1500), except for the first cast at a new target when the engine has the
    /// server gates (UseDone and the cast gesture): those already hold the cast until the
    /// previous one is finished, so the new target only waits <see cref="NewTargetCastFloorMs"/>.
    /// Casting at the same target keeps the full setting.
    /// </summary>
    internal static double AttackIntervalMs(int combatMode, int attackSpellIntervalMs, bool hasServerGates, bool newTarget)
    {
        if (combatMode != CombatMode.Magic) return 1000.0;
        double interval = attackSpellIntervalMs > 0 ? attackSpellIntervalMs : 1500;
        if (newTarget && hasServerGates) return Math.Min(interval, NewTargetCastFloorMs);
        return interval;
    }

    public bool Think()
    {
        _tickWait = CombatLatencyTracker.Wait.Other;
        _attackedThisTick = false;
        bool result = ThinkCore();

        // [CombatLatency]: charge this tick to what it waited on, or log the engagement once
        // its first attack/cast went out (see CombatLatencyTracker).
        if (_latency.Pending)
        {
            double nowMs = Environment.TickCount64;
            if (activeTargetId == 0)
                _latency.OnDrop();
            else if (_attackedThisTick)
            {
                string? line = _latency.OnAttack(activeTargetId, nowMs, CombatModeName(CurrentCombatMode),
                    _worldFilter[activeTargetId]?.Name);
                if (line != null) _host.Log(line);
            }
            else
                _latency.OnTick(activeTargetId, nowMs, _tickWait);
        }
        return result;
    }

    private bool ThinkCore()
    {
        if (!_settings.EnableCombat) return false;

        // A stall-watchdog re-wield must finish (weapon back in hand) before anything
        // attacks again, target or not — see ContinueStallRewield.
        if (_stallRewieldId != 0 && ContinueStallRewield())
        {
            _stallWatch.Hold(DateTime.Now);
            return true;
        }

        double acDistanceLimit = _settings.MonsterRange;
        // Hysteresis: a locked target is retained (and kept under attack) out to
        // the larger disengage distance, while new targets are still only
        // acquired within MonsterRange (ScanNearbyTargets). The gap absorbs the
        // step-back AC applies on combat-mode entry so a mob at the edge doesn't
        // oscillate engage<->peace.
        double disengageLimit = DisengageDistance;
        // Note: _blacklistManager self-expires entries on read (IsBlacklisted), so no
        // explicit cleanup pass is needed. The old vestigial `blacklistedTargets` dict
        // and its CleanupExpiredBlacklist helper were removed — they were never populated.
        _blacklistManager.AttemptThreshold = 1; // one report = blacklist; the N-cast count is controlled by JudgePendingCast
        _blacklistManager.TimeoutSeconds   = _settings.BlacklistTimeoutSec;

        if (_raycastSystem?.TargetingFSM != null)
        {
            var fsm = _raycastSystem.TargetingFSM;
            fsm.MaxScanDistanceMeters = _settings.MonsterRange + 40.0f;
            fsm.UseArcs               = _settings.UseArcs;
            fsm.BowArcVelocity        = _settings.BowArcVelocity;
            fsm.CrossbowArcVelocity   = _settings.CrossbowArcVelocity;
            fsm.AtlatlArcVelocity     = _settings.AtlatlArcVelocity;
            fsm.MagicArcVelocity      = _settings.MagicArcVelocity;
            fsm.MissileArcClearance   = _settings.MissileArcClearance;
        }

        // Deferred predicted kill-shot swap: a cast that armed it is confirmed
        // accepted once OffensiveRefusalWindowMs elapses with no "You're too
        // busy!" (ACE has no positive "You cast X" for war magic). Run BEFORE the
        // scan so the dropped mob is suppressed this same tick and acquisition
        // picks the next target — preserving the old swap cadence on real kills,
        // while a REFUSED cast (which cleared the arm) now keeps + retries its
        // target instead of churning the swarm and pinning AC's busy count.
        if (_predictKillSwapArmed)
        {
            // Observe the cast WINDUP: CanCastNow goes false while the cast
            // gesture animates ([CMI+0x80] non-empty), true once it releases.
            bool gestureInFlight = _host.HasGetCastBusyState && !_host.CanCastNow;
            if (gestureInFlight)
                _predictSwapGestureSeen = true;

            // Uses the arm's own copy of the cast target/time: the pending-offensive fields are
            // consumed by JudgePendingOffensiveCast at the next attack cycle (1.5 s), which
            // used to discard the arm as "stale" before a ~2 s windup ever released, so the
            // swap never fired for high-tier casts and the 3 s timeout was unreachable. A
            // refused/fizzled/resisted cast still cancels the arm in HandleChatForDebuffs.
            if (activeTargetId == 0 || _predictArmTargetId != activeTargetId)
                _predictKillSwapArmed = false; // target already advanced/dropped — stale arm
            else if ((DateTime.Now - _predictArmCastAt).TotalMilliseconds >= OffensiveRefusalWindowMs)
            {
                // ⚠ Do NOT drop the target while the cast gesture is still
                // animating. A high-tier cast (Force Arc VII) winds up ~2s,
                // FAR longer than the 500ms refusal window — swapping at 500ms
                // dropped the target mid-windup and ABORTED the cast before it
                // reached the server (full gesture + bolt played, but no
                // "You cast", zero damage; two mobs ping-ponged, nothing died —
                // 2026-06-13). Wait until the windup has been SEEN and then
                // completed (released): then the cast has actually landed its
                // damage and the predicted swap is correct. Bounded by a
                // timeout so a missing/forever gesture can't arm-lock combat.
                bool released = _predictSwapGestureSeen && (!_host.HasGetCastBusyState || _host.CanCastNow);
                bool timedOut = (DateTime.Now - _predictArmCastAt).TotalMilliseconds >= PredictKillSwapMaxWaitMs;
                if (released || timedOut)
                    ExecuteDeferredKillSwap();
            }
        }

        // Update the pre-scanned target list (distance, attackable, LOS — no selection)
        try { ScanNearbyTargets(); } catch (Exception ex) { _host.Log($"[RynthAi] ScanNearbyTargets crashed: {ex.Message}"); }

        // Validate current target — restore from lock first so transient world-filter nulls
        // don't cause HandleCombatTrigger to pick a different mob on the same tick.
        if (_lockedTargetId != 0 && activeTargetId == 0)
            activeTargetId = _lockedTargetId;

        if (activeTargetId != 0)
        {
            var target = _worldFilter[activeTargetId];
            bool blacklisted = _blacklistManager.IsBlacklisted(activeTargetId);

            if (blacklisted)
                DropTarget("blacklisted");
            else if (target != null && IsSpellProjectileName(target.Name))
                DropTarget("spell projectile (not a monster)");
            else if (target != null && (int)target.ObjectClass != (int)AcObjectClass.Monster)
            {
                ForgetScanned(activeTargetId);
                DropTarget("became corpse");
            }
            else if (_worldFilter.GetHealthRatio(activeTargetId) == 0f)
            {
                ForgetScanned(activeTargetId);
                DropTarget("hp=0");
            }
            else if (target != null &&
                     _worldFilter.Distance(_host.GetPlayerId() == 0 ? 0 : (int)_host.GetPlayerId(), activeTargetId) > disengageLimit)
                DropTarget("out of range");
            // If target == null here it's a transient world-filter miss — keep the lock,
            // the stillScanned grace period below will handle a truly dead/vanished mob.
        }

        // Score every candidate every tick — handles initial pick AND switching when
        // a meaningfully better target appears (stickiness bonus prevents flapping).
        HandleCombatTrigger();

        if (activeTargetId == 0)
        {
            // No valid target — go to Peace immediately to stop the client
            // from auto-running toward a distant monster.  The 500ms cooldown
            // prevents ChangeCombatMode spam while idling between spawns.
            if (CurrentCombatMode == CombatMode.NonCombat)
                _idlePeaceAttempts = 0;   // reached peace — the next idle request starts fresh
            if (_settings.PeaceModeWhenIdle && CurrentCombatMode != CombatMode.NonCombat
                && _scannedTargets.Count == 0 && BusyCount == 0
                && DateTime.Now >= _navMagicWantedUntil)   // a nav recall wants Magic mode
            {
                // Defer idle peace-swap while a cast at the just-dropped target is still
                // resolving: ChangeCombatMode truncates the in-flight gesture → strands
                // Player.IsBusy. Bounded by IsCastInFlight (UseDone-seq or 2.5s).
                // Backoff like the stance flips: 500 ms for the first 4 sends, doubling every
                // 4 after that up to 15 s. An unbounded ChangeCombatMode stream is the
                // 2026-05-25 stance-spam crash class; this used to retry every 500 ms forever.
                double peaceDelayMs = Math.Min(15_000, 500 * Math.Pow(2, Math.Min(5, _idlePeaceAttempts / 4)));
                if ((DateTime.Now - _lastPeaceAttempt).TotalMilliseconds > peaceDelayMs && !IsCastInFlight())
                {
                    _host.ChangeCombatMode(CombatMode.NonCombat);
                    _lastPeaceAttempt = DateTime.Now;
                    _idlePeaceAttempts++;
                    if (_idlePeaceAttempts == 16)
                        _host.Log($"[EquipDiag] WARNING: {_idlePeaceAttempts} idle peace-mode requests without reaching peace (mode={CurrentCombatMode}) — backing off to {Math.Min(15_000, 500 * Math.Pow(2, Math.Min(5, _idlePeaceAttempts / 4))) / 1000:0.#}s retries.");
                }
            }
            return true;
        }

        // Verify active target is still in the scanned list (still visible + LOS clear)
        bool stillScanned = false;
        for (int i = 0; i < _scannedTargets.Count; i++)
        {
            if (_scannedTargets[i].Id == activeTargetId) { stillScanned = true; break; }
        }
        if (!stillScanned)
        {
            // Give it a grace period before dropping — one bad LOS result or scan gap shouldn't
            // hand control to navigation mid-fight.
            if (_targetLostScanTime == DateTime.MinValue)
                _targetLostScanTime = DateTime.Now;

            if ((DateTime.Now - _targetLostScanTime).TotalMilliseconds > TARGET_SCAN_GRACE_MS)
                DropTarget("scan grace expired");
            return true;
        }
        _targetLostScanTime = DateTime.MinValue; // back in scan — reset grace timer

        var targetObj = _worldFilter[activeTargetId];
        if (targetObj == null) return true; // transient miss — skip tick, keep lock

        // Pin kill-shot fight tracking to the active target (covers initial lock,
        // target switches, and lock-restore after a transient miss), and keep
        // trying to resolve its HP until known (the appraisal lands async).
        if (activeTargetId != _fightTargetId)
            BeginFight(activeTargetId);
        else if (_fightTargetMaxHp <= 0 || _fightHpEstimated)
            ResolveFightHp();
        // Keep trying to capture the mob's name (the world filter fills it in
        // asynchronously, so it's often empty at lock time) — by kill time the
        // learning file then has a real name, not a blank.
        if (_fightTargetName.Length == 0)
        {
            string? nm = _worldFilter[activeTargetId]?.Name;
            if (!string.IsNullOrEmpty(nm)) _fightTargetName = nm;
        }

        // Time-based blacklist: if we've been engaged with this target for longer than
        // TargetNoProgressTimeoutSec without dealing any damage, give up and blacklist it.
        // Uses last-damage time when available; falls back to lock time.
        // Skip the no-progress blacklist while raycast is warming up — the bot
        // can't deal damage with degraded targeting, so this timer would
        // otherwise sideline a perfectly good target through no fault of its
        // own. Warmup is sub-second to ~2s, far under TargetNoProgressTimeoutSec.
        int noProgressTimeoutSec = _settings.TargetNoProgressTimeoutSec;
        if (noProgressTimeoutSec > 0 && _targetLockedAt != DateTime.MinValue && !RaycastWarmingUp)
        {
            DateTime refTime = _lastDamageDealtAt != DateTime.MinValue ? _lastDamageDealtAt : _targetLockedAt;
            if ((DateTime.Now - refTime).TotalSeconds > noProgressTimeoutSec)
            {
                _host.WriteToChat($"[RynthAi] No progress on {targetObj.Name} after {noProgressTimeoutSec}s — blacklisting", 2);
                _blacklistManager.TimeoutSeconds = _settings.BlacklistTimeoutSec;
                _blacklistManager.ReportFailure(activeTargetId);
                DropTarget("no-progress timeout");
                return true;
            }
        }

        // Distance gate: don't issue any attack commands (SelectItem, attack, cast)
        // when the target is beyond the disengage distance. Within the deadband
        // (MonsterRange..disengage) we KEEP issuing commands so AC's auto-run
        // pulls the character back into engage range after the combat-mode
        // step-back — instead of dropping to peace and restarting the approach.
        double currentDist = _worldFilter.Distance(
            _host.GetPlayerId() == 0 ? 0 : (int)_host.GetPlayerId(), activeTargetId);
        if (currentDist > disengageLimit)
            return true;

        // Melee/missile stall safety net (see CombatStallWatchdog). Holds the attack while
        // one of its steps is settling.
        if (RunStallWatchdog(targetObj, currentDist))
            return true;

        // While a targeted combat cast is still resolving on the server, do NOT re-assert
        // stance/weapon here: EquipWeaponAndSetStance can emit ChangeCombatMode/UseObject,
        // motion-replacing actions that truncate the in-flight cast gesture → orphan the cast
        // → strand Player.IsBusy (relog-only wedge). Magic-only (melee re-equips every tick and
        // never arms the flag); bounded by IsCastInFlight (UseDone-seq or 2.5s); re-runs next tick.
        if (!(CurrentCombatMode == CombatMode.Magic && IsCastInFlight()))
        {
            if (!EquipWeaponAndSetStance(targetObj, "Auto"))
            {
                _tickWait = CombatLatencyTracker.Wait.Stance;
                return true;
            }
        }

        bool useNative = _settings.UseNativeAttack && _host.HasNativeAttack;

        // Melee turn motions are managed by the facing gate below (direct-attack
        // path) — it clears them the moment heading is within tolerance, which
        // also mops up anything navigation left running. The native path never
        // sets them and lets AC turn the avatar itself.

        // Attack magic uses its OWN interval (AttackSpellIntervalMs, default
        // 1500ms) so war/void combat casts can be spaced ~1-2s without slowing
        // buff chains — buffs keep the faster SpellCastIntervalMs. Spacing the
        // offensive casts stops back-to-back "You're too busy!" refusals that
        // silently drop casts and cost kills. ≤0 (e.g. a pre-existing settings
        // file saved before this field existed) falls back to 1500ms.
        // The first cast at a NEW target (after a kill or a predicted-kill swap) doesn't wait out
        // the interval left over from the last target when the engine reports UseDone: the
        // cast-resolution gate (UseDone) and the gesture gate (CanCastNow) below already hold it
        // until the previous cast is finished, which is what the interval stood in for before
        // those gates existed (it predates them, 2026-06-06 vs 06-20). See AttackIntervalMs.
        double attackCmdIntervalMs = AttackIntervalMs(CurrentCombatMode, _settings.AttackSpellIntervalMs,
            hasServerGates: _host.HasUseDoneSeq && _host.HasGetCastBusyState,
            newTarget: activeTargetId != _lastAttackCmdTargetId);
        if ((DateTime.Now - lastAttackCmd).TotalMilliseconds < attackCmdIntervalMs)
            _tickWait = CombatLatencyTracker.Wait.Interval;
        else
        {
            _offensiveCastThisCycle = false;
            // Convert any pending offensive cast whose refusal window has
            // elapsed into a queued judgement (ACE has no "You cast X" for
            // war magic; this is our successful-cast detector).
            JudgePendingOffensiveCast();
            JudgePendingCast(); // verdict on the last cast once its window elapses

            // Client is busy processing a previous action — don't queue more. Unless the count
            // is a leftover: the server finished every action behind it (a UseDone arrived after
            // it last rose) and no gesture is animating — AC lowered it inline, where the busy
            // hook can't see it, and it would otherwise hold combat until the 5 s force-clear.
            // The shared mirror is reset (not AC's real field — the engine reconciler owns that),
            // the same as BuffManager does when a buff resolves. See CombatBusyGate.
            if (BusyCount > 0)
            {
                if (!BusyIsLeftover())
                {
                    LastCombatSkipReason = "busy-count"; // D4 record-only
                    _tickWait = CombatLatencyTracker.Wait.Busy;
                    return true;
                }
                int was = BusyCount;
                _staleBusyResets++;
                _onStaleBusy?.Invoke($"combat: the server finished the action (UseDone) — was {was}");
                if (BusyCount > 0) BusyCount = 0;   // no callback, or the shared mirror was already 0
                _busyGate.Reset();
            }

            // Magic cadence guard: while a previous combat cast is still
            // resolving on the ACE server, do NOT turn or issue another cast.
            // A turn/stop MoveToState (or the next cast's free-hands stop-thunk)
            // landing during the server's deferred targeted-cast windup orphans
            // the prior cast → 0 damage. Resolves on a server UseDone (poll) or
            // a hard timeout. Magic only; melee/missile are AC-paced.
            if (CurrentCombatMode == CombatMode.Magic && IsAwaitingCastResolution())
            {
                LastCombatSkipReason = "cast-cadence"; // D4 record-only (IsCastInFlight)
                _tickWait = CombatLatencyTracker.Wait.Cast;
                return true;
            }

            // MAGIC and MISSILE: the server turns the player to the target itself,
            // as retail did — ACE Rotate(target) before a targeted cast (and again
            // after the windup if needed) and before the first missile shot and
            // between repeat shots. So the bot doesn't turn at all. A client-side
            // turn servo on top only fought that rotation: missile characters spun
            // in circles between shots (2026-09-27, Lucy, Olthoi swarm at close
            // range), and for magic the server waits while the client holds a turn
            // (PendingTurnRelease), which is where the "animates but 0 damage / too
            // busy" orphaned windups came from. Applies with or without
            // UseNativeAttack. Only a turn another mode left held is released — with one
            // settle tick before a cast, so the stop doesn't ride with the packet.
            // MELEE the same: ACE's HandleActionTargetedMeleeAttack_Inner rotates to the
            // target (past melee_max_angle) before the swing, so the servo made the same
            // swing-spin-swing with a knife in an Olthoi swarm (2026-09-29, Lucy).
            if (CurrentCombatMode == CombatMode.Magic || CurrentCombatMode == CombatMode.Missile
                || CurrentCombatMode == CombatMode.Melee)
            {
                if (_facingTarget)
                {
                    ClearCombatTurnMotions();
                    _facingTarget = false;
                    _faceSettledAt = DateTime.Now;
                    LastCombatSkipReason = "face-settle-release"; // D4 record-only
                    _tickWait = CombatLatencyTracker.Wait.Face;
                    return true; // settle tick
                }
                if (CurrentCombatMode == CombatMode.Magic
                    && (DateTime.Now - _faceSettledAt).TotalMilliseconds < FACE_SETTLE_MS)
                {
                    LastCombatSkipReason = "face-settle-wait"; // D4 record-only
                    _tickWait = CombatLatencyTracker.Wait.Face;
                    return true; // let the turn-stop settle on the server first
                }
            }
            // Native attack handles facing internally — skip manual facing.
            // Otherwise turn to face before swinging or firing: the direct
            // MeleeAttack/MissileAttack game actions bypass the client's
            // turn-to-face, so without this a non-native melee box swings at
            // whatever heading nav left it on. AC paces the swing/shot itself,
            // so the cast-windup orphaning that Magic guards against above
            // doesn't apply — a plain servo is enough here.
            else if (!useNative)
            {
                double facingError = GetFacingError(activeTargetId);
                if (facingError > FACE_TOLERANCE_DEG)
                {
                    FaceTarget(activeTargetId);
                    if (!_facingTarget)
                    {
                        _facingTarget = true;
                        _faceStartTime = DateTime.Now;
                    }
                    if ((DateTime.Now - _faceStartTime).TotalMilliseconds < FACE_TIMEOUT_MS)
                    {
                        _tickWait = CombatLatencyTracker.Wait.Face;
                        return true; // not facing yet, keep waiting
                    }
                }
                _facingTarget = false;
                ClearCombatTurnMotions();
            }

            // Don't attack in missile mode without ammo
            if (CurrentCombatMode == CombatMode.Missile && !HasWieldedAmmo())
            {
                _lastMissileNoAmmoAt = DateTime.Now;   // waiting on ammo is not a stall
                LastCombatSkipReason = "no-ammo";      // D4 record-only
                return true;
            }

            // SelectItem removed 2026-06-03: targeted casts now use the explicit-target
            // FreeHandsAndCastSpell path and direct attacks pass targetId explicitly, so
            // AC's global selection no longer needs setting here. Setting it stole the
            // user's inventory selection every combat tick (AC has a single selection) and
            // was an off-thread SetSelectedObject mutation (not marshalled by P1). Matches
            // RC2's RynthBot, which never selects. (Native physical attack in AttackTarget
            // still selects — it genuinely requires AC's selection.)

            bool physicalSent = true;   // false when AttackTarget sent nothing this cycle
            if (CurrentCombatMode == CombatMode.Magic && _spellManager != null)
            {
                // Don't issue a combat spell while the previous cast GESTURE is
                // still animating. AC refuses a mid-gesture cast with the
                // server-driven "You're too busy!" notice; sustained refusals
                // re-enter AC's AddTextToScroll → 0x00460D1D AV. CanCastNow is
                // the engine's CMotionInterp gesture gate (the SAME gate
                // BuffManager uses). It degrades to true on an engine without
                // the gate, where the SpellCastIntervalMs attack throttle still
                // bounds retries. Melee/missile (AttackTarget, below) is
                // deliberately NOT gated on this — a weapon swing isn't a spell
                // cast and AC paces the swing animation itself.
                if (!CastGateWatchdog.CanCastNow(_host.CanCastNow, s => _host.Log(s)))
                {
                    if ((DateTime.Now - lastAttackCmd).TotalMilliseconds > 5000)
                        _host.Log($"[CombatCast] CanCastNow=false — gesture gate blocking cast (last attack {(DateTime.Now - lastAttackCmd).TotalMilliseconds:0}ms ago, target=0x{activeTargetId:X8})");
                    LastCombatSkipReason = "cast-gate"; // D4 record-only (CanCastNow=false)
                    _tickWait = CombatLatencyTracker.Wait.Cast;
                    return true;
                }

                AttackWithMagic(targetObj);

                // Don't restore the physical weapon on top of a cast just issued this tick
                // (AttackWithMagic → MarkCombatCastIssued): the UseObject + re-equip below would
                // truncate the in-flight gesture and strand Player.IsBusy. Defer one windup
                // (bounded); _returnToPhysicalCombat stays true so the restore still runs after.
                if (_returnToPhysicalCombat && !IsCastInFlight())
                {
                    var rule2 = GetRuleForTarget(targetObj);
                    string elem2 = GetPreferredElement(targetObj, rule2);
                    if (rule2 != null && !HasPendingDebuffs(rule2, elem2))
                    {
                        _returnToPhysicalCombat = false;
                        if (_savedWeaponId != 0
                            && (_weaponSwapGate == null || _weaponSwapGate.TryBeginSwap("combat-restore")))
                        {
                            _host.UseFor((uint)_savedWeaponId, "Combat", "wield the weapon again after casting");
                            _savedWeaponId = 0;
                        }
                        EquipWeaponAndSetStance(targetObj, "Auto");
                    }
                }
            }
            else
            {
                var rule = GetRuleForTarget(targetObj);

                if (rule != null && _spellManager != null && !_returnToPhysicalCombat)
                {
                    string elem = GetPreferredElement(targetObj, rule);
                    if (HasPendingDebuffs(rule, elem))
                    {
                        int wandId = FindWandInItems();
                        // This quick swap (UseObject wand + Magic in one go) can't work with a
                        // melee/missile weapon in hand: ACE won't wield the wand over it and
                        // denies Magic while it's wielded (see the dequip-first notes at the
                        // top), and EquipWeaponAndSetStance would switch straight back anyway.
                        // It only cost each new target a refused swap, a lost attack cycle and
                        // the swap gate. Skip it then; it still runs when the hands are empty.
                        // A shield or an off-hand weapon blocks the wand the same way (ACE refuses a caster beside it).
                        if (wandId != 0 && (FindWieldedNonWandWeapon(wandId) != 0 || OffhandInSlot() != 0))
                        {
                            if (!_physicalDebuffSkipWarned)
                            {
                                _physicalDebuffSkipWarned = true;
                                _host.Log($"[CombatCast] debuffs for '{rule.Name}' skipped in {CombatModeName(CurrentCombatMode)} mode — can't swap to the wand with a weapon in hand");
                                _host.WriteToChat($"[RynthAi] Debuffs aren't cast in melee/missile mode with a weapon in hand (the wand swap doesn't work there). Fighting without them.", 2);
                            }
                        }
                        else if (wandId != 0
                            && (_weaponSwapGate == null || _weaponSwapGate.TryBeginSwap("combat-debuff-wand")))
                        {
                            // TODO: Save current equipped weapon when inventory API is available.
                            _savedWeaponId = 0;
                            _returnToPhysicalCombat = true;
                            _host.UseFor((uint)wandId, "Combat", "wield the wand to cast");
                            _lastEquipTime = DateTime.Now; // gate AttackWithMagic until wand is wielded
                            _host.ChangeCombatMode(CombatMode.Magic);
                            lastAttackCmd = DateTime.Now;
                            _lastAttackCmdTargetId = activeTargetId;
                            return true;
                        }
                    }
                }

                // Physical combat always attacks — spell shape flags (UseArc/Bolt/Ring/Streak)
                // are only relevant in magic mode and must not gate melee/missile attacks.
                physicalSent = AttackTarget();
                if (physicalSent) _attackedThisTick = true;
            }

            // Ring spells hit an area — no per-target damage feedback is generated,
            // so they must not count toward the blacklist miss counter.
            // Magic mode no longer counts from here at all: RecordOffensiveCast
            // is invoked from the chat-confirmation path ("ou cast X" matching
            // _pendingOffensiveSpellId) so a server-refused attempt ("too busy",
            // equip waits, "no spell found" etc.) never queues a judgement.
            // Melee/missile attack every cycle and get prompt damage feedback,
            // so they keep per-cycle counting (the magicMode branch is false).
            bool magicMode = CurrentCombatMode == CombatMode.Magic && _spellManager != null;
            if (!_lastCastWasRing && (!magicMode || _offensiveCastThisCycle) && physicalSent)
                RecordOffensiveCast(activeTargetId);
            lastAttackCmd = DateTime.Now;
            _lastAttackCmdTargetId = activeTargetId;

            // Predicted kill shot (set in AttackWithMagic): the cast we just fired
            // is expected to finish this mob. DON'T swap yet — on ACE a war cast is
            // only confirmed accepted ~OffensiveRefusalWindowMs later (the absence
            // of "You're too busy!"). Dropping the target 1ms after issuing meant a
            // REFUSED cast still abandoned a live mob and advanced to the next,
            // churning the whole swarm and pinning AC's busy count (the wedge).
            // Arm it; the deferred-swap check at the top of Think executes it once
            // the cast is confirmed, and the "too busy"/fizzle handlers cancel it.
            if (_predictKillSwap)
            {
                _predictKillSwap = false;
                if (activeTargetId != 0)
                {
                    _predictKillSwapArmed = true;
                    _predictSwapGestureSeen = false; // must observe the cast windup before swapping
                    // AttackWithMagic just set these for the cast that predicted the kill.
                    _predictArmTargetId = _pendingOffensiveTargetId != 0 ? _pendingOffensiveTargetId : activeTargetId;
                    _predictArmCastAt   = _pendingOffensiveCastAt != DateTime.MinValue ? _pendingOffensiveCastAt : DateTime.Now;
                }
            }
        }
        return true;
    }

    // Diagnostic: log the combat manager state when meaningful inputs change.
    // We bucket msSinceAttack into Recent (<3s = "in active engagement") vs
    // Stale (>=3s) so this doesn't fire on every tick — the raw ms ticks up
    // continuously and would flood the log at ~30 lines/sec otherwise (which
    // it did before this fix — bot lived ~64s under that load and the file-
    // write contention may have helped trigger AC's idle-exit timeout).
    private string _lastCombatStateKey = "";
    private void LogCombatStateIfChanged()
    {
        long msSinceAttack = lastAttackCmd == DateTime.MinValue
            ? -1
            : (long)(DateTime.Now - lastAttackCmd).TotalMilliseconds;
        string attackBucket = msSinceAttack < 0 ? "never"
                            : msSinceAttack < 3000 ? "recent"
                            : msSinceAttack < 10_000 ? "stale"
                            : "cold";
        string key = $"enableCombat={_settings.EnableCombat} scanned={_scannedTargets.Count} active=0x{activeTargetId:X8} busy={BusyCount} mode={CurrentCombatMode} attack={attackBucket} action='{_settings.BotAction}'";
        if (key == _lastCombatStateKey) return;
        _lastCombatStateKey = key;
        _host.Log($"Combat: state {key} (msSinceAttack={msSinceAttack})");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  STALL WATCHDOG (melee/missile) — see CombatStallWatchdog
    // ══════════════════════════════════════════════════════════════════════════

    private const double StallReselectSuppressMs = 3000;   // step 2: skip the stuck mob this long if another is in reach
    private const double StallToggleHoldMs       = 5000;   // step 3: peace→back window that still counts as eligible

    /// <summary>
    /// Called from Think once a live target is validated and within disengage distance.
    /// Returns true when this tick's attack must be held (a recovery step was just sent).
    /// Runs only while the arbiter has given combat the tick and BotAction is Combat, so it
    /// never competes with buffing, looting or navigation.
    /// </summary>
    private bool RunStallWatchdog(WorldObject targetObj, double currentDist)
    {
        DateTime now = DateTime.Now;
        int mode = CurrentCombatMode;
        bool physical = mode == CombatMode.Melee || mode == CombatMode.Missile;
        bool toggling = _stallToggleAt != DateTime.MinValue
                        && (now - _stallToggleAt).TotalMilliseconds < StallToggleHoldMs;
        bool rewielding = _stallRewieldId != 0;
        double range = Math.Max(2.0, _settings.MonsterRange);

        bool eligible = _settings.BotAction == "Combat"
                        && activeTargetId != 0
                        && (physical || toggling)
                        && currentDist <= range
                        && _worldFilter.GetHealthRatio(activeTargetId) != 0f
                        && !(mode == CombatMode.Missile && (now - _lastMissileNoAmmoAt).TotalMilliseconds < 3000);

        // While the client is busy combat sends nothing (the attack block's busy gate), so no
        // swing evidence can arrive — and the steps (mode change, moving the weapon to a pack)
        // are exactly the actions that must not go out then. Don't fire a step; keep the
        // episode (Hold, so this isn't read as a tick gap) so brief busy pulses between
        // attacks change nothing and the ladder resumes once busy clears.
        if (eligible && BusyCount > 0)
        {
            _stallWatch.Hold(now);
            return false;
        }

        var act = _stallWatch.Tick(now, eligible, _lastSwingEvidenceAt, out double stalledMs);
        switch (act)
        {
            case CombatStallWatchdog.StallAction.None:
                return false;

            case CombatStallWatchdog.StallAction.Recovered:
                _host.Log($"[StallWatch] recovered — {_lastSwingEvidenceKind} after step {LastStallStepName} " +
                          $"({stalledMs / 1000:0.0}s without swings; episodes={_stallWatch.Episodes} recovered={_stallWatch.Recoveries})");
                return false;

            case CombatStallWatchdog.StallAction.GaveUp:
                _host.Log($"[StallWatch] all {CombatStallWatchdog.StepCount} steps done, still no swings on 0x{activeTargetId:X8} '{targetObj.Name}' — " +
                          $"resting {CombatStallWatchdog.CooldownMs / 1000:0}s (targeting and no-progress rules carry on)");
                return false;
        }

        if (rewielding)
            return true; // a step-4 re-wield is still settling; the ladder waits for it

        _lastStallStep = act;
        if (act == CombatStallWatchdog.StallAction.Reissue)
            LogStallSnapshot(targetObj, currentDist, stalledMs);

        string head = $"[StallWatch] step {_stallWatch.CurrentStep}/{CombatStallWatchdog.StepCount}";
        switch (act)
        {
            case CombatStallWatchdog.StallAction.Reissue:
                ReleaseServerAttack("stall watchdog", log: false);
                lastAttackCmd = now; // the attack goes out again one cycle later, after the cancel lands
                _host.Log($"{head} re-issue: CancelAttack, attack 0x{activeTargetId:X8} again next cycle");
                return true;

            case CombatStallWatchdog.StallAction.Reselect:
            {
                ReleaseServerAttack("stall watchdog", log: false);
                int old = activeTargetId;
                bool alt = HasAlternateTarget(old);
                if (alt) _recentlyKilled[old] = now.AddMilliseconds(StallReselectSuppressMs);
                _host.Log($"{head} re-select: dropping 0x{old:X8} '{targetObj.Name}'" +
                          (alt ? $" and skipping it {StallReselectSuppressMs / 1000:0}s (another mob is in reach)" : " (no other mob in reach, it may be picked again)"));
                DropTarget("stall watchdog: re-select");
                lastAttackCmd = now;
                return true;
            }

            case CombatStallWatchdog.StallAction.ToggleMode:
                // Same effect on the server as the wand heal: a mode change runs
                // HandleActionCancelAttack. EquipWeaponAndSetStance flips back after its
                // stance retry delay (~1 s with the flip backoff reset).
                _host.ChangeCombatMode(CombatMode.NonCombat);
                _stallToggleAt = now;
                ResetStanceFlipBackoff();
                lastStanceAttempt = now;
                _serverAttackTargetId = 0;
                lastAttackCmd = now;
                _host.Log($"{head} toggle mode: peace, then back to {CombatModeName(mode)} on the next stance pass");
                return true;

            case CombatStallWatchdog.StallAction.Rewield:
                StartStallRewield(head);
                lastAttackCmd = now;
                return true;
        }
        return false;
    }

    private CombatStallWatchdog.StallAction _lastStallStep = CombatStallWatchdog.StallAction.None;

    /// <summary>Stall watchdog / attack-rebind counters for /ra why.</summary>
    public int StallEpisodes   => _stallWatch.Episodes;
    public int StallRecoveries => _stallWatch.Recoveries;
    public int AttackRebinds   => _attackRebinds;
    public string LastSwingEvidenceDescription => _lastSwingEvidenceAt == DateTime.MinValue
        ? "never"
        : $"{_lastSwingEvidenceKind} {(DateTime.Now - _lastSwingEvidenceAt).TotalSeconds:0.0}s ago";
    private string LastStallStepName => _lastStallStep switch
    {
        CombatStallWatchdog.StallAction.Reissue    => "1 (re-issue)",
        CombatStallWatchdog.StallAction.Reselect   => "2 (re-select)",
        CombatStallWatchdog.StallAction.ToggleMode => "3 (toggle mode)",
        CombatStallWatchdog.StallAction.Rewield    => "4 (re-wield)",
        _ => "?",
    };

    private static string CombatModeName(int mode) => mode switch
    {
        CombatMode.NonCombat => "Peace",
        CombatMode.Melee     => "Melee",
        CombatMode.Missile   => "Missile",
        CombatMode.Magic     => "Magic",
        _ => mode.ToString(),
    };

    /// <summary>Step 4: put the weapon away and wield it again (what the heal's wand swap
    /// does to the knife). Finished by ContinueStallRewield; bounded and swap-gated.</summary>
    private void StartStallRewield(string head)
    {
        int wid = _equippedWeaponId;
        var wo = wid != 0 ? _worldFilter[wid] : null;
        if (wid == 0 || wo == null)
        {
            _host.Log($"{head} re-wield: skipped — no known weapon in hand");
            return;
        }
        if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("stall-rewield"))
        {
            _host.Log($"{head} re-wield: skipped — another weapon swap ran {_weaponSwapGate.MsSinceLastSwap}ms ago ({_weaponSwapGate.LastSwapBy})");
            return;
        }

        if (!IsWieldedLive(wid))
        {
            // Not actually in hand: just wield it.
            _host.UseFor((uint)wid, "Combat", "wield the weapon (not in hand)");
            _stallRewieldWieldSent = true;
            _stallRewieldWieldTries = 1;
            _host.Log($"{head} re-wield: 0x{wid:X8} '{wo.Name}' is not in hand — UseObject to wield it");
        }
        else
        {
            int pack = WorldObjectCache.FindPackFor(_host, _worldFilter, includeMainPack: true, requireFree: 1);
            if (pack == 0 || !_host.HasMoveItemInternal)
            {
                _host.Log($"{head} re-wield: skipped — no pack with room to put 0x{wid:X8} '{wo.Name}' in");
                return;
            }
            if (_host.HasCancelAttack) _host.CancelAttack();
            _serverAttackTargetId = 0;
            _host.MoveItemInternal((uint)wid, (uint)pack, 0, 1);
            _stallRewieldWieldSent = false;
            _stallRewieldWieldTries = 0;
            _host.Log($"{head} re-wield: 0x{wid:X8} '{wo.Name}' -> pack 0x{pack:X8}, then wield it again");
        }
        _stallRewieldId = wid;
        _stallRewieldAt = DateTime.Now;
        _stallRewieldCheckAt = DateTime.MinValue;
    }

    /// <summary>
    /// Finish a step-4 re-wield: once the weapon is out of hand, wield it (through the swap
    /// gate); done when it reads wielded. Runs at the top of Think whether or not a target
    /// is locked, because EquipWeaponAndSetStance trusts a matching combat mode and would
    /// not put a weapon left in the pack back in hand. Returns true while attacks must wait.
    /// </summary>
    private bool ContinueStallRewield()
    {
        DateTime now = DateTime.Now;
        int mode = CurrentCombatMode;
        if (mode == CombatMode.Magic)
        {
            // Buffing/healing took the hands; its wand swap and combat's own equip path
            // (mode ≠ Melee → ChangeCombatMode + UseObject) bring the weapon back.
            _host.Log($"[StallWatch] re-wield of 0x{_stallRewieldId:X8} handed over — mode is Magic");
            _stallRewieldId = 0;
            return false;
        }
        if ((now - _stallRewieldCheckAt).TotalMilliseconds < 250) return true;
        _stallRewieldCheckAt = now;

        bool wielded = IsWieldedLive(_stallRewieldId);
        double ms = (now - _stallRewieldAt).TotalMilliseconds;

        if (_stallRewieldWieldSent && wielded)
        {
            _host.Log($"[StallWatch] re-wield done: 0x{_stallRewieldId:X8} back in hand ({ms:0}ms after the last step)");
            _stallRewieldId = 0;
            lastStanceAttempt = DateTime.MinValue; // let the stance pass re-assert the mode now
            return false;
        }

        if (!wielded && (!_stallRewieldWieldSent || ms > StallRewieldStepTimeoutMs))
        {
            if (_stallRewieldWieldTries >= StallRewieldMaxWieldTries)
            {
                _host.Log($"[StallWatch] re-wield FAILED: 0x{_stallRewieldId:X8} not back in hand after {_stallRewieldWieldTries} tries");
                _host.WriteToChat($"[RynthAi] Couldn't put the weapon back in hand after the stall recovery. Please wield it by hand.", 2);
                _stallRewieldId = 0;
                return false;
            }
            if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("stall-rewield"))
                return true; // the dequip holds the slot ~3 s; wield when it frees
            _host.UseFor((uint)_stallRewieldId, "Combat", "stalled: wield the weapon again");
            _stallRewieldWieldSent = true;
            _stallRewieldWieldTries++;
            _stallRewieldAt = now;
            return true;
        }

        if (!_stallRewieldWieldSent && wielded && ms > StallRewieldStepTimeoutMs)
        {
            _host.Log($"[StallWatch] re-wield: 0x{_stallRewieldId:X8} never left the hand in {StallRewieldStepTimeoutMs:0}ms — leaving it wielded");
            _stallRewieldId = 0;
            return false;
        }
        return true;
    }

    /// <summary>One detailed look at every gate on the attack path, logged when a stall is
    /// first declared (once per episode).</summary>
    private void LogStallSnapshot(WorldObject targetObj, double dist, double stalledMs)
    {
        DateTime now = DateTime.Now;
        string Ago(DateTime t) => t == DateTime.MinValue ? "never" : $"{(now - t).TotalMilliseconds:0}ms ago";
        int tid = activeTargetId;

        _host.Log($"[StallWatch] STALL {stalledMs / 1000:0.0}s: action Combat, mode {CombatModeName(CurrentCombatMode)}, " +
                  $"no hit/miss/kill while attacking 0x{tid:X8} '{targetObj.Name}' — snapshot (episode {_stallWatch.Episodes}):");

        float hp = _worldFilter.GetHealthRatio(tid);
        bool scanned = false;
        for (int i = 0; i < _scannedTargets.Count; i++)
            if (_scannedTargets[i].Id == tid) { scanned = true; break; }
        _host.Log($"[StallWatch]   target: d={dist:0.0}m range={_settings.MonsterRange:0.#} hp={(hp < 0 ? "?" : hp.ToString("0.00"))} class={targetObj.ObjectClass} " +
                  $"scanned={scanned} locked={(_lockedTargetId == tid)} lockedFor={Ago(_targetLockedAt)} lastDamage={Ago(_lastDamageDealtAt)} " +
                  $"facingErr={GetFacingError(tid):0}deg scan={_scannedTargets.Count} alt={HasAlternateTarget(tid)} {DescribeTargetPos(tid)}");

        string serverTarget = _serverAttackTargetId == 0 ? "none"
            : _serverAttackTargetId == tid ? "same"
            : $"0x{_serverAttackTargetId:X8} '{_worldFilter[_serverAttackTargetId]?.Name}' dead={IsKnownDead(_serverAttackTargetId)} {DescribeTargetPos(_serverAttackTargetId)}";
        _host.Log($"[StallWatch]   attack: lastCmd={Ago(lastAttackCmd)} interval=1000ms lastSkip='{LastCombatSkipReason}' serverTarget={serverTarget} " +
                  $"rebinds={_attackRebinds} native={(_settings.UseNativeAttack && _host.HasNativeAttack)} " +
                  $"evidence: hit={Ago(_lastHitAt)} miss={Ago(_lastMissAt)} kill={Ago(_lastKillAt)} (last={_lastSwingEvidenceKind})");

        string castGate = _host.HasGetCastBusyState ? (_host.CanCastNow ? "open" : "closed") : "n/a";
        _host.Log($"[StallWatch]   gates: busy={BusyCount} castGate={castGate} castInFlight={IsCastInFlight()} faceHeld={_facingTarget} " +
                  $"returnToPhysical={_returnToPhysicalCombat} stanceFlips={_stanceFlipAttempts} stanceStuck={Ago(_stanceStuckSince)} " +
                  $"wandWield(pending=0x{_wandPendingWieldId:X8} fails={_wandWieldFailCount}) " +
                  $"swapGate={(_weaponSwapGate == null ? "n/a" : $"{_weaponSwapGate.MsSinceLastSwap}ms ago by '{_weaponSwapGate.LastSwapBy}'")}");

        int wid = _equippedWeaponId;
        var wo = wid != 0 ? _worldFilter[wid] : null;
        string weapon = wo == null ? $"0x{wid:X8} (unknown)"
            : $"0x{wid:X8} '{wo.Name}' class={wo.ObjectClass} wieldedLive={IsWieldedLive(wid)} loc={wo.Values(LongValueKey.CurrentWieldedLocation, 0)}";
        string useDone = "n/a", weenie = "n/a";
        if (_host.HasGetLastUseDone && _host.TryGetLastUseDone(out int uSeq, out uint uErr))
            useDone = $"seq={uSeq} err=0x{uErr:X}";
        if (_host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int wSeq, out uint wErr, out uint wEvt, out uint wObj))
            weenie = $"seq={wSeq} err=0x{wErr:X4} evt=0x{wEvt:X3} obj=0x{wObj:X8}";
        _host.Log($"[StallWatch]   weapon: {weapon} | server: lastUseDone {useDone} | lastWeenieError {weenie}");
    }

    public void OnHeartbeat()
    {
        if (!_settings.IsMacroRunning)
        {
            // Clear turn motions once on the transition from running → stopped,
            // so the character doesn't spin indefinitely after a mid-turn stop.
            // Do NOT clear every frame — that blocks manual keyboard turning.
            if (_wasMacroRunning)
            {
                _wasMacroRunning = false;
                ClearCombatTurnMotions();
            }
            return;
        }
        _wasMacroRunning = true;

        LogCombatStateIfChanged();

        // A combat mode change ends the server's attack (HandleActionChangeCombatMode →
        // HandleActionCancelAttack), so out of Melee/Missile there is nothing bound to release.
        if (_serverAttackTargetId != 0)
        {
            int m = CurrentCombatMode;
            if (m != CombatMode.Melee && m != CombatMode.Missile) _serverAttackTargetId = 0;
        }

        // Always run the scan and BotAction state update, even when combat can't
        // take actions. ScanNearbyTargets has no side-effects (no game commands)
        // and must stay fresh so HasTargets is accurate for nav-blocking decisions.
        // Without this, stale scan data keeps combatBlocking = true after a kill,
        // preventing navigation from resuming while the bot is buffing, etc.
        if (_settings.EnableCombat)
        {
            try { ScanNearbyTargets(); }
            catch (Exception ex) { _host.Log($"[RynthAi] ScanNearbyTargets CRASH: {ex.Message}"); }

            // Only hold the "Combat" BotAction lock while actively engaging.
            // "Actively engaging" = an attack command was issued recently.
            // Without this, having anything visible in scan range latched
            // BotAction = "Combat" and blocked NavigationEngine.Tick from
            // running, even when CombatManager couldn't actually attack
            // (out of range, BusyCount stuck, etc.) — symptom was bot
            // standing still surrounded by far-off mobs while nav refused
            // to move. By tying the lock to recent attack activity, nav
            // gets to run between engagement windows and bot can chase /
            // reposition.
            // BotAction is no longer written here. STEP 2 (ACTIVITY_ARBITER_PLAN.md):
            // the ActivityArbiter in RynthAiPlugin.OnTick is the sole writer of
            // the "Combat"/"Navigating" strings, driven by the pure
            // HasEngageableTarget predicate. CombatManager only reads BotAction
            // (via canRun below) and acts. Removing these writes is what kills
            // the squat-without-fighting freeze: Combat can no longer latch the
            // lock based on broad/stale scan state.
        }
        else if (activeTargetId != 0 || _scannedTargets.Count > 0 || _nearbyNoLos > 0)
        {
            // Combat switched off (the UI, /vt opt or a meta's SetOpt): the scan stops, so
            // drop what it last saw. Kept, HasEngageableTarget stayed true for good: salvage
            // never ticked (it yields to a threat), the arbiter kept picking Salvaging, and
            // the bot stood still; BuffManager also stayed on its in-combat thresholds.
            _scannedTargets.Clear();
            _nearbyNoLos = 0;
            DropTarget("combat disabled");
        }

        // Combat can run in Default/Combat, can interrupt navigation unless nav boost is on,
        // and can interrupt looting unless loot boost is on.
        // Buffing always blocks combat — if buffs drop, the character dies.
        bool canRun = _settings.BotAction == "Default"
                   || _settings.BotAction == "Combat"
                   || (_settings.BotAction == "Navigating" && !_settings.BoostNavPriority)
                   || (_settings.BotAction == "Looting" && !_settings.BoostLootPriority);
        if (!canRun) return;

        if (_settings.EnableCombat)
        {
            try { Think(); }
            catch (Exception ex) { _host.Log($"[RynthAi] Think CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }

            // BotAction no longer written post-Think either — the arbiter
            // recomputes from HasEngageableTarget every tick (33ms lag, picks
            // up activeTargetId set by the Think() above on the next pass).
            // See STEP 2 note in the pre-Think branch.
        }
    }

    public void HandleCombatTrigger()
    {
        // Utility-AI selection: score every visible candidate, pick the highest.
        // The locked target carries a stickiness bonus so we only switch when a
        // genuinely better option appears — no flapping on near-ties.
        if (_scannedTargets.Count == 0) return;

        int bestId = 0;
        double bestScore = double.MinValue;
        double heldScore = double.MinValue;   // scored total for the incumbent, for the switch log
        bool incumbentSeen = false;
        double acquireRange = Math.Max(1.0, _settings.MonsterRange);

        // Strict priority: the highest Priority among monsters inside MonsterRange (or among
        // all scanned ones when none is inside it) is the only tier that may be picked.
        int topPriority = int.MinValue, topAnyRange = int.MinValue;
        foreach (var c in _scannedTargets)
        {
            int p = PriorityOf(c.Id);
            if (p > topAnyRange) topAnyRange = p;
            if (c.Distance <= acquireRange && p > topPriority) topPriority = p;
        }
        if (topPriority == int.MinValue) topPriority = topAnyRange;

        // Finish what you started: a target we have damaged (or whose health is known to be below
        // full) is kept while it's within MonsterRange and in the top priority tier, whatever a
        // closer fresh monster scores. As a score bonus (stickiness 25 + commitment 20) it lost to
        // a fresh monster ~2 m closer (20 points a metre at Monster Range 5): Mob A hit twice,
        // Mob B killed, back to A (2026-10-04). Only a clearly more wounded monster in range takes
        // over: a predicted kill that survived. Stuck targets are still dropped by the stall
        // watchdog (it suppresses them out of the scan).
        int forcedId = 0;
        if (_lockedTargetId != 0)
        {
            ScannedTarget? inc = null;
            foreach (var c in _scannedTargets) if (c.Id == _lockedTargetId) { inc = c; break; }
            if (inc is { } held && held.Distance <= acquireRange && PriorityOf(held.Id) >= topPriority)
            {
                float heldHp = _worldFilter.GetHealthRatio(held.Id);
                bool wounded = _lastDamageDealtAt != DateTime.MinValue || (heldHp >= 0f && heldHp < 0.99f);
                if (wounded)
                {
                    float best = heldHp >= 0f ? heldHp : 1f;
                    foreach (var c in _scannedTargets)
                    {
                        if (c.Id == held.Id || c.Distance > acquireRange || PriorityOf(c.Id) < topPriority) continue;
                        float r = _worldFilter.GetHealthRatio(c.Id);
                        if (r > 0f && r < best - MoreWoundedMargin) { best = r; forcedId = c.Id; }
                    }
                    if (forcedId == 0)
                    {
                        _lockedLastSeenInScan = DateTime.Now;
                        return;   // keep the wounded target
                    }
                }
            }
        }

        foreach (var c in _scannedTargets)
        {
            if (PriorityOf(c.Id) < topPriority) continue;   // a higher-priority monster is in range
            double s = ScoreCandidate(c);
            // The incumbent keeps stickiness and commitment only inside MonsterRange. The
            // scan holds a locked target out to DisengageDistance (MonsterRange+3 by default)
            // so a lone mob at the edge doesn't flip engage/peace, not so it can out-score
            // mobs inside the range the user set. Beyond MonsterRange it competes on its
            // merits, and any reasonable in-range mob takes over. Before this a target at
            // 5-8 m kept +45 and could hold off mobs at 3-5 m (2026-09-27, Olthoi swarm:
            // Monster Range 5, one target kept for over a minute, never out of range).
            if (c.Id == _lockedTargetId)
            {
                double meritScore = s;
                s += TARGET_SWITCH_STICKINESS;
                // Commitment: once we have COMMITTED to the locked target,
                // finishing it outranks any small positional edge a fresh mob
                // holds. Stickiness alone only settles near-ties — it loses to a
                // full-health mob that simply walked closer, which is how the bot
                // left a trail of half-dead mobs.
                //
                // Armed at cast ISSUE as well as on confirmed damage, because
                // Force Arc has ~2s of travel time and keying only on damage
                // landing left that window looking "untouched" — a 2.4-point
                // margin could walk away from a mob with a bolt already in the
                // air (observed 2026-09-03 02:22:45).
                //
                // The cast-issue arm MUST expire. The first version keyed on
                // _lastAttackedTargetId alone, which is set at issue and never
                // cleared while the lock holds — granting a permanent +20 on top
                // of the +25 stickiness to a target that had never taken damage
                // and might never take any. With TargetNoProgressTimeoutSec
                // defaulting to 0 (disabled) nothing else breaks that loop: on
                // 2026-09-05 the bot held one Olthoi for 5+ hours, ~3,100 casts,
                // zero damage, zero kills, switches down from 126 to 3 per 4h.
                //
                // Bounded to the projectile flight window instead: long enough to
                // cover a bolt in the air, short enough that a target which never
                // takes damage returns to competing on its merits.
                // Two bounds, and BOTH are needed:
                //   * since the last cast — covers the bolt currently in flight;
                //   * since the target was locked — caps how long we extend that
                //     courtesy to a target that has never actually taken damage.
                // The second bound is the important one. Without it the window
                // refreshes on every cast, so a target under continuous fire keeps
                // the bonus indefinitely, which is the same permanent-immunity bug
                // in a different guise.
                //
                // Once damage HAS landed, _lastDamageDealtAt grants commitment with
                // no time bound — finishing a wounded mob is exactly the intent.
                bool undamaged = _lastDamageDealtAt == DateTime.MinValue;
                bool withinFlightWindow =
                    _lastAttackedTargetId == c.Id
                    && _lastAttackedAt != DateTime.MinValue
                    && (DateTime.Now - _lastAttackedAt).TotalMilliseconds <= CastCommitmentWindowMs;
                bool withinUndamagedGrace =
                    _targetLockedAt != DateTime.MinValue
                    && (DateTime.Now - _targetLockedAt).TotalMilliseconds <= UndamagedCommitmentMaxMs;

                if (!undamaged || (withinFlightWindow && withinUndamagedGrace))
                    s += DAMAGE_COMMITMENT_BONUS;
                if (c.Distance > acquireRange)
                    s = meritScore;   // beyond MonsterRange: no stickiness, no commitment (see above)
                heldScore = s;
                incumbentSeen = true;
                _lockedLastScore = s;
                _lockedLastSeenInScan = DateTime.Now;
            }
            if (s > bestScore) { bestScore = s; bestId = c.Id; }
        }

        // Incumbent briefly absent from the scan: keep it in the running at its
        // last known score for the grace window rather than letting it score
        // nothing. Without this a transient scan gap hands the fight to whoever
        // happens to be visible this tick, then the next tick hands it back —
        // the A->B->A flapping seen repeatedly on 2026-09-03.
        if (!incumbentSeen
            && _lockedTargetId != 0
            && _lockedPriority >= topPriority
            && _lockedLastScore > double.MinValue
            && _lockedLastSeenInScan != DateTime.MinValue
            && (DateTime.Now - _lockedLastSeenInScan).TotalMilliseconds <= TARGET_SCAN_GRACE_MS)
        {
            heldScore = _lockedLastScore;
            if (heldScore >= bestScore)
                return; // incumbent still wins on its last known score — no switch
        }

        if (forcedId != 0) { bestId = forcedId; bestScore = double.NaN; heldScore = double.MinValue; }

        if (bestId == 0 || bestId == _lockedTargetId) return;

        // One line per real switch. After the scoring fixes a switch should be a
        // genuine event (target died, left, or something decisively better showed
        // up) — if this floods the log, selection is thrashing again and the two
        // scores say by how much.
        int bestPriority = PriorityOf(bestId);
        if (_lockedTargetId != 0)
        {
            string held = forcedId != 0 ? "wounded, but another is more wounded"
                        : _lockedPriority < bestPriority ? $"priority {_lockedPriority} < {bestPriority}"
                        : heldScore == double.MinValue ? "gone" : heldScore.ToString("0.0");
            _host.Log($"[CombatTarget] switch 0x{_lockedTargetId:X8} '{_worldFilter[_lockedTargetId]?.Name}' (score={held}, {DescribeTargetPos(_lockedTargetId)}) " +
                      $"-> 0x{bestId:X8} '{_worldFilter[bestId]?.Name}' (score={bestScore:0.0}, priority {bestPriority}, {DescribeTargetPos(bestId)})");
        }
        else
            _host.Log($"[CombatTarget] lock 0x{bestId:X8} '{_worldFilter[bestId]?.Name}' (score={bestScore:0.0}, priority {bestPriority}, {DescribeTargetPos(bestId)})");

        // [CombatLatency]: time this engagement from the lock to its first attack.
        double lockMs = Environment.TickCount64;
        double sinceKillMs = _lastKillAt == DateTime.MinValue ? double.MaxValue : (DateTime.Now - _lastKillAt).TotalMilliseconds;
        _latency.OnLock(bestId, lockMs, sinceKillMs <= 2000 ? lockMs - sinceKillMs : double.NaN);

        activeTargetId      = bestId;
        _lockedTargetId     = bestId;
        _lockedPriority     = bestPriority;
        _targetLockedAt     = DateTime.Now;
        _lastDamageDealtAt  = DateTime.MinValue;
        _lockedLastScore      = double.MinValue;   // new incumbent — no carried score yet
        _lockedLastSeenInScan = DateTime.MinValue;
        _consecutiveCastMisses = 0;
        _pendingJudgeCastAt = DateTime.MinValue;
        _facingTarget       = false;
        _returnToPhysicalCombat = false;
        // Target-switch turn-stop: defer while a cast at the PREVIOUS target is still resolving
        // (nothing clears the in-flight flag on target acquire), else this SetMotion(turn,false)
        // truncates that gesture → strands Player.IsBusy. The lock switch above still happens;
        // the stop re-issues next tick. Bounded by IsCastInFlight (UseDone-seq or 2.5s).
        if (!IsCastInFlight()) ClearCombatTurnMotions();

        // Internal lock is set unconditionally so combat is ready to swing the
        // moment busy clears. SelectItem during a combat-mode transition can
        // wedge the client action queue, so only fire it when not busy.
        // SelectItem removed 2026-06-03 (see the combat-tick note): the target lock is
        // purely internal (activeTargetId / _lockedTargetId set above). Casts use the
        // explicit-target FreeHandsAndCastSpell path and attacks pass targetId, so AC's
        // selection needn't track the combat target. Removing it frees the user's
        // inventory selection and drops an off-thread SetSelectedObject mutation.
    }

    private double ScoreCandidate(in ScannedTarget c)
    {
        double maxDist = Math.Max(1.0, _settings.MonsterRange);
        double distScore   = Math.Clamp((maxDist - c.Distance) / maxDist, 0.0, 1.0) * 100.0;

        // GetHealthRatio returns -1 for "no vitals received for this mob yet".
        // Combat is selection-free (SelectItem removed 2026-06-03), so ACE only
        // pushes health for the mob we are actually fighting — every other
        // candidate reads -1. Math.Clamp(-1, 0, 1) folded that to 0.0, i.e. "at
        // death's door", so every untouched mob collected the FULL 50-point
        // wounded bonus while the mob we were burning down scored its real,
        // smaller one.
        //
        // The nastier half: a target's score COLLAPSED by up to 45 points the
        // instant we hit it and its true health finally arrived. Engaging a mob
        // made it a worse choice than the untouched mob standing next to it, so
        // combat was pushed off whatever it had just committed to and onto the
        // next one — pick, turn, hit once, re-pick, turn again. That is the spin.
        //
        // Unknown health means "not engaged yet", and an un-engaged mob is almost
        // certainly at full health. Score it that way.
        float  hpRatio     = _worldFilter.GetHealthRatio(c.Id);
        double hpScore     = hpRatio < 0f ? 0.0 : (1.0 - Math.Clamp(hpRatio, 0f, 1f)) * 50.0;

        // Continuous ramp rather than a hard step at 3.0m. As a step this was
        // worth 30 points the moment a mob crossed the line — more than
        // TARGET_SWITCH_STICKINESS (25) — so a mob jittering either side of 3.0m
        // could flip the winner every tick regardless of our commitment.
        double threatScore = Math.Clamp((THREAT_RANGE_M - c.Distance) / THREAT_RANGE_M, 0.0, 1.0) * 30.0;

        double facingScore = (1.0 - Math.Min(1.0, c.Angle / 180.0)) * 10.0;

        // Monster priority is not scored here: HandleCombatTrigger only scores monsters of
        // the top Priority in range (see PriorityOf), so within the field it's a constant.
        return distScore + hpScore + threatScore + facingScore;
    }

    /// <summary>The Priority of the monster rule that applies to <paramref name="id"/> (its own
    /// row by name or match expression, else the DEFAULT row; 1 when neither exists). Cached per
    /// monster for <see cref="PriorityCacheMs"/>; the cache empties when the rule list changes.</summary>
    private int PriorityOf(int id)
    {
        var rules = _settings.MonsterRules;
        if (!ReferenceEquals(rules, _priorityCacheRules)) { _priorityCache.Clear(); _priorityCacheRules = rules; }
        DateTime now = DateTime.Now;
        if (_priorityCache.TryGetValue(id, out var hit) && (now - hit.At).TotalMilliseconds < PriorityCacheMs)
            return hit.Priority;
        if (_priorityCache.Count > 512) _priorityCache.Clear();
        int p = GetRuleForTarget(_worldFilter[id])?.Priority ?? 1;
        _priorityCache[id] = (p, now);
        return p;
    }

    private DateTime _lastEquipTime = DateTime.MinValue;
    private DateTime _lastStanceTime = DateTime.MinValue;
    private DateTime _lastEquipDiagAt = DateTime.MinValue;

    /// <summary>
    /// The weapon to fight <paramref name="target"/> with (0 = none), and the element in
    /// <paramref name="desired"/>. See WeaponPlanner for the order: this monster's Damage tab
    /// weapon (listed only), a slayer of the monster, the Damage tab DEFAULT row's weapon, the
    /// Monsters rule's damage type, the weakness (weakest element a listed
    /// weapon of the main kind has), else the weapon in hand, the learned best, the first.
    /// Casters are skipped when the character has no War/Void Magic (IsUsableCombatWeapon).
    /// </summary>
    internal int ChooseWeapon(WorldObject target, MonsterRule? rule, ref string desired, out string source)
    {
        var plan = PlanFor(target, rule);
        source = plan.Source;
        if (plan.Element.Length > 0) desired = plan.Element;
        return plan.WeaponId;
    }

    /// <summary>
    /// The weapon and element for <paramref name="target"/> (WeaponPlanner's rule). Only weapons
    /// in the Items list are candidates: a Damage-tab pick or a learned "best" weapon that is no
    /// longer listed is ignored (logged once), which is how an unlisted fire wand used to be
    /// wielded after the user removed it from the list (2026-09-30).
    /// </summary>
    internal WeaponPlan PlanFor(WorldObject target, MonsterRule? rule)
    {
        // The equip path and the spell path both ask every tick; the answer only moves when
        // the target, the rule, the list or the hand does, so reuse it for a quarter second.
        DateTime now = DateTime.Now;
        int hand = InHandWeaponId();
        var list = _settings.ItemRules;
        if (_planCache is { } pc && pc.TargetId == target.Id && ReferenceEquals(pc.Rule, rule) && ReferenceEquals(pc.List, list)
            && pc.Count == list.Count && pc.Hand == hand && (now - pc.At).TotalMilliseconds < PlanCacheMs)
            return pc.Plan;
        var plan = ComputePlan(target, rule);
        _planCache = (target.Id, rule, list, list.Count, hand, now, plan);
        return plan;
    }

    private (int TargetId, MonsterRule? Rule, List<ItemRule> List, int Count, int Hand, DateTime At, WeaponPlan Plan)? _planCache;
    private const double PlanCacheMs = 250;

    private WeaponPlan ComputePlan(WorldObject target, MonsterRule? rule)
    {
        var listed = ListedCandidates();
        string ruleElement = rule != null && !string.IsNullOrEmpty(rule.DamageType)
                             && !rule.DamageType.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? rule.DamageType : "";

        uint wcid = 0;
        if (_host.HasGetObjectWcid && _host.TryGetObjectWcid((uint)target.Id, out uint tw)) wcid = tw;

        int fixedId = 0, learnedId = 0;
        string fixedSource = "Damage tab";
        bool fixedIsDefault = false;
        if (wcid != 0 && _damageStore != null)
        {
            uint pick = _damageStore.GetManualWeapon(wcid);
            if (pick == 0) { pick = _damageStore.GetDefaultWeapon(); fixedSource = "Damage tab default"; fixedIsDefault = true; }
            if (pick != 0 && !listed.Any(c => c.Id == (int)pick)) { NoteUnlisted((int)pick, fixedSource); pick = 0; }
            fixedId = (int)pick;
            uint best = _damageStore.GetBestWeapon(wcid);
            if (best != 0 && !listed.Any(c => c.Id == (int)best)) { NoteUnlisted((int)best, "Damage tab learned"); best = 0; }
            learnedId = (int)best;
        }

        // Launchers: their element is their ammo's, and one with no ammo to shoot isn't wielded
        // (CombatManager.Ammo.cs). Lists without a launcher come back unchanged.
        var weak = WeaknessFor(target);
        listed = ApplyAmmo(listed, ruleElement, weak);
        // Slayers (PropertyInt 166 = the monster's PropertyInt 2) only when a listed weapon has one.
        int ctype = 0;
        if (listed.Any(c => c.SlayerType > 0))
        {
            ctype = CreatureTypeOf(wcid, target.Name, target.Id);
            if (ctype == 0 && _slayerTypeUnknownNoted.TryAdd(wcid != 0 ? wcid.ToString() : target.Name ?? "", 0))
                _host.Log($"[EquipDiag] '{target.Name}' (wcid {wcid}): creature type not known yet (not appraised, not in the creature data) - no slayer check until it is");
        }
        return WeaponPlanner.Choose(listed, ruleElement, weak, e => CanCastElement(e, rule),
            fixedId, fixedSource, learnedId, ctype, fixedIsDefault);
    }

    // PropertyInt 166 SlayerCreatureType, PropertyFloat 138 SlayerDamageBonus (ACE.Entity).
    private const uint PropSlayerCreatureType = 166, PropSlayerDamageBonus = 138;

    /// <summary>
    /// A weapon's slayer (creature type, damage multiplier); (0, 0) when it has none or isn't
    /// identified yet (the weapon tracker identifies listed weapons). The multiplier is 0 when
    /// not known: ACE sends SlayerCreatureType (int 166, an assessment property) on appraisal
    /// but never SlayerDamageBonus (float 138), so requiring both (as before 2026-10-05) meant no
    /// slayer was ever seen on an ACE server. A server that does send float 138 gets it used.
    /// </summary>
    internal (int Type, double Bonus) SlayerOf(int weaponId)
    {
        uint uid = unchecked((uint)weaponId);
        if (!_host.HasGetObjectIntProperty || !_host.TryGetObjectIntProperty(uid, PropSlayerCreatureType, out int t) || t <= 0)
            return (0, 0);
        double bonus = _host.HasGetObjectDoubleProperty && _host.TryGetObjectDoubleProperty(uid, PropSlayerDamageBonus, out double b) && b > 0 ? b : 0;
        if (_slayerNoted.TryAdd((weaponId, t), 0))
            _host.Log($"[EquipDiag] weapon 0x{uid:X8} '{WeaponName(weaponId)}' slays {CreatureTypeNames.Name(t)} ({(bonus > 0 ? "x" + bonus.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "bonus not sent")})");
        return (t, bonus);
    }

    // Logged once each: a listed weapon's slayer; a monster whose type isn't known while a slayer is listed.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), byte> _slayerNoted = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _slayerTypeUnknownNoted = new();

    // Creature type by wcid, once seen: a wcid's type never changes, so the next spawn is instant.
    // Concurrent: the Damage tab's JSON export asks for weaknesses off the combat tick.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, int> _creatureTypeByWcid = new();

    /// <summary>
    /// The monster's CreatureType (PropertyInt 2; 0 = unknown): this session's per-wcid cache,
    /// else the creature store (appraisals saved by name and wcid), else the live appraisal
    /// (the engine identifies every new object), else the shipped world-database table
    /// (CreatureWeakness: wcid and exact name). Never guessed from name keywords.
    /// </summary>
    internal int CreatureTypeOf(uint wcid, string? name, int objectId = 0)
    {
        if (wcid != 0 && _creatureTypeByWcid.TryGetValue(wcid, out int known)) return known;
        int ctype = 0;
        name ??= "";
        if (_creatureStore != null && name.Length > 0)
        {
            CreatureProfile? prof = null;
            bool got = wcid != 0 ? _creatureStore.TryGet(name, wcid, out prof) : _creatureStore.TryGetByName(name, out prof);
            if (got && prof != null) ctype = prof.CreatureType;
        }
        if (ctype == 0 && objectId != 0 && _host.HasGetObjectIntProperty
            && _host.TryGetObjectIntProperty(unchecked((uint)objectId), 2 /* CreatureType */, out int ct))
            ctype = ct;
        if (ctype > 0 && wcid != 0)
        {
            if (_creatureTypeByWcid.Count > 4096) _creatureTypeByWcid.Clear();
            _creatureTypeByWcid[wcid] = ctype;
        }
        // Not seen yet on this server: the shipped world-database table (wcid and name must both
        // match, as for its resists). Not cached, so this monster's own appraisal still wins once
        // it lands. 4,506 of the ~4,900 creature profiles are seeded without a type (2026-10-05),
        // so before this the first fight with a monster had no slayer step.
        if (ctype <= 0) ctype = CreatureWeakness.TableCreatureType(wcid, name);
        return ctype > 0 ? ctype : 0;
    }

    private readonly HashSet<int> _unlistedNoted = new();

    private void NoteUnlisted(int id, string from)
    {
        if (!_unlistedNoted.Add(id)) return;
        var wo = _worldFilter[id];
        if (wo != null && IsShieldItem(wo) && _settings.ItemRules.Any(r => r.Id == id))
        {
            _host.Log($"[EquipDiag] {from} weapon 0x{(uint)id:X8} '{wo.Name}' is a shield — it only goes in the off hand");
            return;
        }
        _host.Log($"[EquipDiag] {from} weapon 0x{(uint)id:X8} '{wo?.Name ?? "?"}' isn't in the Items list — not wielding it");
    }

    /// <summary>The Items list's usable weapons, in list order, with their kind, element, rending and whether each is in hand.</summary>
    internal List<WeaponCandidate> ListedCandidates()
    {
        int inHand = InHandWeaponId();
        var list = new List<WeaponCandidate>();
        foreach (var r in _settings.ItemRules)
        {
            if (r.Id == 0 || !IsUsableCombatWeapon(r.Id)) continue;
            if (list.Any(c => c.Id == r.Id)) continue;
            var wo = _worldFilter[r.Id];
            if (wo == null) continue;
            if (wo.ObjectClass == AcObjectClass.MissileWeapon && IsAmmoItem(wo)) continue;   // listed arrows are the ammo choice, never a weapon to wield
            if (IsShieldItem(wo)) continue;                                                  // listed shields are the off hand's (CombatManager.Offhand.cs)
            var info = ElementTracker.Read(r.Id, wo.Name);
            string elem = WeaponElements.Normalize(r.Element);
            if (elem.Length == 0) elem = info.Element;
            var (slayType, slayBonus) = SlayerOf(r.Id);
            list.Add(new WeaponCandidate(r.Id, wo.Name, WeaponKind(r.Id), elem, info.Rending, r.Id == inHand, slayType, slayBonus));
        }
        return list;
    }

    // The weapon in hand, read live at most every InHandReadMs (the read walks the inventory).
    private int _inHandId;
    private DateTime _inHandReadAt = DateTime.MinValue;
    private const double InHandReadMs = 1000;

    /// <summary>The melee/missile weapon or caster the character holds (0 = none readable).</summary>
    internal int InHandWeaponId(bool fresh = false)
    {
        if (InHandOverride != null) return InHandOverride();
        DateTime now = DateTime.Now;
        if (!fresh && (now - _inHandReadAt).TotalMilliseconds < InHandReadMs) return _inHandId;
        _inHandReadAt = now;
        int found = 0;
        try
        {
            foreach (var wo in _worldFilter.GetDirectInventory(forceRefresh: true))
            {
                if (WorldObjectCache.IsAmmo(wo)) continue;
                // The off hand (a dual-wielded weapon in the Shield slot) is not the weapon in hand.
                if ((wo.Values(LongValueKey.CurrentWieldedLocation, 0) & OffhandRules.ShieldSlot) != 0) continue;
                bool weapon = wo.ObjectClass == AcObjectClass.MeleeWeapon
                           || wo.ObjectClass == AcObjectClass.MissileWeapon
                           || IsWandObject(wo);
                if (!weapon || !WorldObjectCache.IsWieldedByPlayer(_host, wo)) continue;
                // Two in hand (a wand and a sword mid-swap): a listed one wins.
                if (found == 0 || _settings.ItemRules.Any(r => r.Id == wo.Id)) found = wo.Id;
            }
        }
        catch { }
        _inHandId = found;
        return found;
    }

    /// <summary>The combat mode a weapon puts the character in (0 = unknown).</summary>
    private int WeaponKind(int id)
    {
        var wo = _worldFilter[id];
        if (wo == null) return 0;
        return IsWandObject(wo) ? CombatMode.Magic
             : wo.ObjectClass == AcObjectClass.MissileWeapon ? CombatMode.Missile
             : CombatMode.Melee;
    }

    /// <summary>What hurts <paramref name="target"/> most (server data, learned, creature type); null = unknown.</summary>
    internal CreatureWeakness.Ranking? WeaknessFor(WorldObject? target)
    {
        if (target == null) return null;
        // The creature's own appraisal resists win when the server sent any (stock ACE doesn't).
        var appraised = AppraisedWeakness(target.Id);
        if (appraised != null) return appraised;
        uint wcid = 0;
        if (_fightTargetWcid != 0 && target.Id == _fightTargetId) wcid = _fightTargetWcid;
        else if (_host.HasGetObjectWcid && _host.TryGetObjectWcid((uint)target.Id, out uint w)) wcid = w;
        return WeaknessFor(wcid, target.Name, target.Id);
    }

    // ResistSlash, Pierce, Bludgeon, Fire, Cold, Acid, Electric (64-70), ResistNether (166),
    // in CreatureWeakness.Elements order.
    private static readonly uint[] ResistFloats = { 64, 65, 66, 67, 68, 69, 70, 166 };

    private CreatureWeakness.Ranking? AppraisedWeakness(int objectId)
    {
        if (objectId == 0 || !_host.HasGetObjectDoubleProperty) return null;
        var m = new double?[8];
        bool any = false;
        for (int i = 0; i < 8; i++)
            if (_host.TryGetObjectDoubleProperty(unchecked((uint)objectId), ResistFloats[i], out double v)) { m[i] = v; any = true; }
        return any ? CreatureWeakness.FromAppraisal(m) : null;
    }

    /// <summary>The same by wcid and name; <paramref name="objectId"/> (optional) reads a live creature type.</summary>
    internal CreatureWeakness.Ranking? WeaknessFor(uint wcid, string? name, int objectId = 0)
    {
        name ??= "";
        int ctype = CreatureTypeOf(wcid, name, objectId);
        string learned = wcid != 0 ? _damageStore?.GetBestElement(wcid) ?? "" : "";
        return CreatureWeakness.Rank(wcid, name, ctype, learned);
    }

    // Server replies seen when the last combat wield was sent (UseDone / WeenieError sequence).
    private int _swapUseDoneSeq, _swapWeenieSeq;

    /// <summary>
    /// Asks the swap tracker about <paramref name="chosenId"/> with <paramref name="inHandId"/>
    /// in hand, after folding in a server refusal of the wield in flight. Logs a give-up once.
    /// </summary>
    private WeaponSwapTracker.Step SwapStep(int chosenId, int inHandId)
    {
        DateTime now = DateTime.Now;
        if (SwapTracker.PendingId != 0 && ServerRefusedSinceSend()) SwapTracker.Refused(now, WeaponName);
        var step = SwapTracker.Next(chosenId, inHandId, now, WeaponName);
        if (SwapTracker.Notice != null)
        {
            _host.Log($"[EquipDiag] {SwapTracker.Notice}");
            SwapTracker.Notice = null;
        }
        if (step == WeaponSwapTracker.Step.Hold && (DateTime.Now - _lastSwapHoldLogAt).TotalSeconds > 10)
        {
            _lastSwapHoldLogAt = DateTime.Now;
            _host.Log($"[EquipDiag] keeping 0x{(uint)inHandId:X8} '{WeaponName(inHandId)}' instead of 0x{(uint)chosenId:X8} '{WeaponName(chosenId)}' ({SwapTracker.HoldReason})");
        }
        return step;
    }

    private DateTime _lastSwapHoldLogAt = DateTime.MinValue;

    private string WeaponName(int id) => id == 0 ? "" : (_worldFilter[id]?.Name ?? "");

    /// <summary>Send one combat wield of <paramref name="weaponId"/> and tell the swap tracker.</summary>
    private void SendWield(int weaponId, int inHandId, string why)
    {
        _host.UseFor((uint)weaponId, "Combat", $"wield: {why}");
        MarkWieldSent(weaponId, inHandId);
        _host.Log($"[EquipDiag] {why}: 0x{(uint)inHandId:X8} '{WeaponName(inHandId)}' -> 0x{(uint)weaponId:X8} '{WeaponName(weaponId)}' (try {SwapTracker.TriesFor(weaponId)}/{SwapTracker.MaxTries})");
    }

    /// <summary>A combat wield of <paramref name="weaponId"/> was just sent: the swap tracker
    /// holds further swaps until it lands, is refused, or times out.</summary>
    private void MarkWieldSent(int weaponId, int inHandId)
    {
        DateTime now = DateTime.Now;
        if (_host.HasGetLastUseDone && _host.TryGetLastUseDone(out int uSeq, out _)) _swapUseDoneSeq = uSeq;
        if (_host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int wSeq, out _, out _, out _)) _swapWeenieSeq = wSeq;
        SwapTracker.Sent(weaponId, inHandId, now);
        _inHandReadAt = DateTime.MinValue;   // read the hand fresh next time
        _offSlotReadAt = DateTime.MinValue;  // and the off hand
        _lastEquipTime = now;
    }

    /// <summary>A WeenieError, or a UseDone with an error, arrived after the wield was sent.</summary>
    private bool ServerRefusedSinceSend()
    {
        if (_host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int wSeq, out uint wErr, out _, out _)
            && wSeq != _swapWeenieSeq && wErr != 0)
        {
            _swapWeenieSeq = wSeq;
            return true;
        }
        if (_host.HasGetLastUseDone && _host.TryGetLastUseDone(out int uSeq, out uint uErr) && uSeq != _swapUseDoneSeq)
        {
            _swapUseDoneSeq = uSeq;
            return uErr != 0;
        }
        return false;
    }

    /// <summary>
    /// The combat mode already matches the chosen weapon's kind, but the chosen one isn't
    /// confirmed in hand: if another weapon of the same kind is in hand (a sword for a sword,
    /// a bow for a bow, a wand for a wand), wield the chosen one. One swap in flight, capped,
    /// no flip-flop (WeaponSwapTracker). True while the attack should wait for the wield.
    /// Needs a positive read of the other weapon in hand, so a stale wield read on the chosen
    /// weapon can't turn into a UseObject that takes it off. Wand-for-wand used to never
    /// happen: Magic mode was trusted, so whatever wand was in hand stayed there (2026-09-30).
    /// </summary>
    private bool SwapToChosenSameKind(int weaponId, int desiredMode)
    {
        int inHand = InHandWeaponId();
        if (inHand == 0 || inHand == weaponId) { SwapTracker.Confirmed(weaponId, DateTime.Now); if (inHand == weaponId) _equippedWeaponId = weaponId; return false; }
        if (WeaponKind(inHand) != desiredMode) return false;   // a cross-kind case; the branches below handle it

        _equippedWeaponId = inHand;
        switch (SwapStep(weaponId, inHand))
        {
            case WeaponSwapTracker.Step.Wait: return true;
            case WeaponSwapTracker.Step.Swap:
                // A two-hander or a launcher: the shield (or off-hand weapon) comes off first.
                var off = ClearOffhandFor(weaponId, _curOffhandMode);
                if (off == OffPrep.Busy) return true;
                if (off == OffPrep.Blocked) return false;     // fight on with what's in hand
                // A launcher for another ammo type: the wielded ammo goes into the pack first.
                if (desiredMode == CombatMode.Missile)
                {
                    var prep = PrepareAmmoFor(weaponId);
                    if (prep == AmmoPrep.Busy) return true;
                    if (prep == AmmoPrep.Blocked) return false;   // fight on with the launcher in hand
                }
                if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-weapon-swap")) return false;
                SendWield(weaponId, inHand, "swap");
                return true;
            default: return false;   // Hold / InHand: fight with what's in hand
        }
    }

    /// <summary>
    /// No weapon to wield: fight unarmed. Any combat mode already set is kept (as before).
    /// From peace mode, ask for Melee (with the stance backoff) and hold the attack until it
    /// lands: ACE drops a melee attack sent outside Melee mode, and the no-damage blacklist
    /// then sidelined every target one after another.
    /// </summary>
    private bool EnsureUnarmedStance()
    {
        if (CurrentCombatMode != CombatMode.NonCombat)
        {
            ResetStanceFlipBackoff();
            return true;
        }
        if ((DateTime.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs())
        {
            _host.ChangeCombatMode(CombatMode.Melee);
            lastStanceAttempt = DateTime.Now;
            NoteStanceFlipSent();
        }
        return false;
    }

    // Returns true when the correct weapon is wielded and combat mode matches — safe to attack.
    // Returns false when a weapon swap or stance change is in progress — caller should skip this tick.
    private bool EquipWeaponAndSetStance(WorldObject target, string monsterWeakness = "Auto")
    {
        if (target == null) return true;

        int targetWeaponId = 0;

        // Same rule the spell/debuff path uses (expression + meta-state aware). A raw name
        // substring match made a blank-named expression rule (IndexOf("") == 0) match every
        // monster and drive weapon choice for all of them.
        var rule = GetRuleForTarget(target);

        string desired = monsterWeakness;
        targetWeaponId = ChooseWeapon(target, rule, ref desired, out string weaponSource);

        if (targetWeaponId == 0 && CanAttackWithMagic)
        {
            targetWeaponId = FindWandInItems();
            if (targetWeaponId != 0) weaponSource = "FindWandInItems";
        }
        if (targetWeaponId == 0)
        {
            if ((DateTime.Now - _lastEquipDiagAt).TotalSeconds > 5)
            {
                _lastEquipDiagAt = DateTime.Now;
                _host.Log($"[EquipDiag] no weapon found (source=none, desired='{desired}', ItemRules={_settings.ItemRules.Count}, MonsterRule='{rule?.Name ?? "null"}') — proceeding unarmed");
            }
            return EnsureUnarmedStance();
        }

        var weaponObj = _worldFilter[targetWeaponId];
        if (weaponObj == null)
        {
            if ((DateTime.Now - _lastEquipDiagAt).TotalSeconds > 5)
            {
                _lastEquipDiagAt = DateTime.Now;
                _host.Log($"[EquipDiag] weapon 0x{targetWeaponId:X8} not in WorldFilter (source={weaponSource}) — proceeding unarmed");
            }
            return EnsureUnarmedStance();
        }

        // Another weapon in hand: one swap at a time, capped, no flip-flop. When the tracker
        // says hold (the chosen one failed its tries, or it is the weapon just swapped away
        // from), carry on with the one in hand.
        // With nothing readable in hand, a held swap sends no wield (the stance logic below
        // still runs, so a mage can cast bare-handed).
        int inHand = InHandWeaponId();
        bool allowWield = true;
        _curOffhandMode = OffhandChoiceFor(target, rule).Mode;

        // Dual wield: the chosen weapon already swings in the off hand, beside another listed
        // one-handed weapon in the main hand. Fight with both rather than moving it across.
        if (inHand != 0 && inHand != targetWeaponId
            && (weaponObj.Values(LongValueKey.CurrentWieldedLocation, 0) & OffhandRules.ShieldSlot) != 0
            && _worldFilter[inHand] is WorldObject mainObj && MainKindOf(mainObj) == MainHandKind.OneHandedMelee
            && _settings.ItemRules.Any(r => r.Id == inHand))
        {
            if (_offNoted.Add($"dual:{targetWeaponId}:{inHand}"))
                _host.Log($"[EquipDiag] 0x{(uint)targetWeaponId:X8} '{weaponObj.Name}' is in the off hand beside 0x{(uint)inHand:X8} '{mainObj.Name}' — fighting with both (dual wield)");
            weaponSource = $"dual wield (wanted 0x{(uint)targetWeaponId:X8} is in the off hand)";
            targetWeaponId = inHand;
            weaponObj = mainObj;
        }

        if (inHand != targetWeaponId && OffhandBlocksMain(targetWeaponId, _curOffhandMode))
        {
            // The off hand can't come off (Offhand None, or resting after failed tries) and the
            // server refuses this weapon with it: keep what's in hand, as for a held swap.
            if (inHand != 0 && _worldFilter[inHand] is WorldObject keepObj)
            {
                weaponSource = $"in hand (off hand stays); wanted 0x{(uint)targetWeaponId:X8}";
                targetWeaponId = inHand;
                weaponObj = keepObj;
            }
            else allowWield = false;
        }
        else if (inHand != targetWeaponId)
        {
            var step = SwapStep(targetWeaponId, inHand);
            if (step == WeaponSwapTracker.Step.Wait) { if (inHand != 0) _equippedWeaponId = inHand; return false; }
            if (step == WeaponSwapTracker.Step.Hold)
            {
                if (inHand != 0 && _worldFilter[inHand] is WorldObject handObj)
                {
                    weaponSource = $"in hand ({SwapTracker.HoldReason}); wanted 0x{(uint)targetWeaponId:X8}";
                    targetWeaponId = inHand;
                    weaponObj = handObj;
                }
                else allowWield = false;
            }
        }

        // Remember the weapon in hand (now confirmed in-world) so melee/missile damage
        // (0x01B1) learns under the real weapon, not a resolved-but-unwielded id.
        _equippedWeaponId = inHand != 0 ? inHand : targetWeaponId;

        // Use IsWandObject (ObjectClass + name fallback) so wands with
        // ObjectClass=Unknown (stale WorldFilter classification) still get
        // Magic mode instead of falling through to the Melee default.
        int desiredMode = IsWandObject(weaponObj)                                  ? CombatMode.Magic
                        : weaponObj.ObjectClass == AcObjectClass.MissileWeapon ? CombatMode.Missile
                        : CombatMode.Melee;

        bool diagNow = (DateTime.Now - _lastEquipDiagAt).TotalSeconds > 5;

        // Use CurrentWieldedLocation (stype=10) — has an InqInt fallback that works even
        // when the phys-obj offset probe hasn't fired yet (unlike TryGetObjectWielderInfo).
        bool alreadyWielded = weaponObj.Values(LongValueKey.CurrentWieldedLocation, 0) > 0;

        // Secondary check — BuffManager.EnsureMagicMode has always had this and this
        // path never did, which is the whole reason buffing could flip the stance and
        // combat could not. CurrentWieldedLocation reads 0 for a weapon that IS wielded
        // whenever the cached property hasn't been refreshed, and a mage's wand is
        // permanently in hand, so the primary read is exactly the one that goes stale.
        // Falling through to the not-wielded branch on a wielded wand sends UseObject
        // (a no-op MOVE) forever and never sends the stance flip that was the only
        // thing actually missing. Confirmed live 2026-09-02: entering Magic mode by
        // hand immediately unstuck combat.
        if (!alreadyWielded && _host.HasGetObjectWielderInfo)
        {
            uint pid = _host.GetPlayerId();
            if (pid != 0 && _host.TryGetObjectWielderInfo((uint)targetWeaponId, out uint wielder, out _)
                && wielder == pid)
            {
                alreadyWielded = true;
                // Must re-arm the throttle, as every other [EquipDiag] site does.
                // diagNow is computed from _lastEquipDiagAt; logging without
                // updating it leaves the gate permanently open on any path that
                // returns before reaching one of those sites — this logged at the
                // full 30Hz tick rate, 26,410 lines in one rotation (~70% of the
                // log), which churned the log every ~30min and destroyed
                // diagnostic history that was needed to investigate other issues.
                if (diagNow)
                {
                    _lastEquipDiagAt = DateTime.Now;
                    _host.Log($"[EquipDiag] wielded via wielder-info fallback (CurrentWieldedLocation read 0) 0x{targetWeaponId:X8} '{weaponObj.Name}'");
                }
            }
        }

        if (alreadyWielded)
        {
            // Just swapped to this launcher: wield its ammo (one in flight, confirmed, capped).
            if (desiredMode == CombatMode.Missile && AmmoFollowUp(targetWeaponId, target, rule))
            {
                _lastMissileNoAmmoAt = DateTime.Now;   // waiting on ammo is not a stall
                LastCombatSkipReason = "no-ammo";
                return false;
            }

            // Don't enter missile mode without ammo — AC rejects it and cycles stance
            if (desiredMode == CombatMode.Missile && !HasWieldedAmmo())
            {
                // Waiting on ammo is not a stall. The attack block's stamp is never reached
                // from here (Think returns on false), so the stall watchdog's no-ammo
                // exemption never applied and it ran its whole ladder on an empty quiver.
                _lastMissileNoAmmoAt = DateTime.Now;
                LastCombatSkipReason = "no-ammo";   // D4 record-only
                return false;
            }

            // The wand read as wielded, so whatever UseObject we had in flight landed.
            // Clear the gate here (not only on reaching the stance) so a slow stance
            // flip doesn't leave a stale pending-wield behind it.
            ResetCombatWandWieldGate();
            SwapTracker.Confirmed(targetWeaponId, DateTime.Now);

            if (CurrentCombatMode == desiredMode)
            {
                ResetStanceRecovery(); // reached the stance — clear the whole episode
                // The main weapon is in hand: now the off hand (a shield, or dual wield).
                if (OffhandFollowUp(target, rule, targetWeaponId)) return false;
                return true;
            }

            // Deadlock recovery: AC is refusing the stance change despite the wand
            // reading as wielded. After a stuck window, STOP trusting the stale wield
            // read — flush any jammed command interpreter (no m_cBusy pin → the
            // watchdog won't) and RE-EQUIP the wand (UseObject resyncs the wield; on a
            // truly-unwielded wand it wields it, which is the actual fix). Without this
            // the bot sat in NonCombat for 5h, buffs expired, never fighting (2026-06-10).
            if (_stanceStuckSince == DateTime.MinValue)
                _stanceStuckSince = DateTime.Now;
            double stuckMs = (DateTime.Now - _stanceStuckSince).TotalMilliseconds;
            // RE-EQUIP recovery — only for the first couple of attempts in an
            // episode (handles a stale "wielded reads true" on a wand that
            // actually isn't wielded). Capped: UseObject on a genuinely wielded
            // wand is a MOVE to AC, and unbounded re-equip jams the item queue.
            if (_stanceReEquipAttempts < StanceReEquipMaxAttempts
                && stuckMs > StanceStuckRecoverMs
                && (DateTime.Now - _lastStanceRecoverAt).TotalMilliseconds > StanceStuckRecoverMs
                // Recovery equips like any other path — must hold the swap gate or
                // it can collide with a buff wand-equip inside the ±3s window.
                // On refusal the && short-circuits: fall through to the throttled
                // ChangeCombatMode below and retry next tick.
                && (_weaponSwapGate == null || _weaponSwapGate.TryBeginSwap("stance-recovery")))
            {
                _lastStanceRecoverAt = DateTime.Now;
                _stanceReEquipAttempts++;
                _host.Log($"[EquipDiag] STANCE STUCK {stuckMs:0}ms mode={CurrentCombatMode}≠{desiredMode} (wielded reads true) — re-equip attempt {_stanceReEquipAttempts}/{StanceReEquipMaxAttempts} 0x{targetWeaponId:X8}");
                if (_host.HasForceResetBusyCount) _host.ForceResetBusyCount();
                _host.UseFor((uint)targetWeaponId, "Combat", "stance stuck: equip again");
                _lastEquipTime = DateTime.Now;
                lastStanceAttempt = DateTime.MinValue; // let ChangeCombatMode re-fire next tick
                return false;
            }
            // Past the re-equip cap: the wand really is wielded and AC just
            // won't flip the mode. Do NOT keep UseObject-ing (that's what jams
            // the item queue) — fall through to mode-change-only, and warn once
            // so a genuinely wedged stance is visible instead of silent.
            if (_stanceReEquipAttempts >= StanceReEquipMaxAttempts && !_stanceWedgeWarned
                && stuckMs > StanceStuckRecoverMs * 3)
            {
                _stanceWedgeWarned = true;
                _host.WriteToChat($"[RynthAi] Stance wedged: wand wielded but AC won't enter {(desiredMode == CombatMode.Magic ? "Magic" : "combat")} mode after {stuckMs / 1000:0}s. Re-equip stopped (was jamming item actions). Try /ra clearbusy, or relog if it persists.", 2);
            }

            if ((DateTime.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs())
            {
                if (diagNow) { _lastEquipDiagAt = DateTime.Now; _host.Log($"[EquipDiag] wielded=true mode={CurrentCombatMode}→{desiredMode} (weapon=0x{targetWeaponId:X8} '{weaponObj.Name}' src={weaponSource}) — sending ChangeCombatMode"); }
                _host.ChangeCombatMode(desiredMode);
                lastStanceAttempt = DateTime.Now;
                NoteStanceFlipSent();
            }
            else if (diagNow)
            {
                _lastEquipDiagAt = DateTime.Now;
                _host.Log($"[EquipDiag] wielded=true mode={CurrentCombatMode}≠{desiredMode} throttled (weapon=0x{targetWeaponId:X8} '{weaponObj.Name}') — waiting for mode change");
            }
            return false;
        }

        // Wield-location probe hasn't confirmed this item yet. Two cases:
        //
        // A) Already in the correct combat mode — AC enforces "weapon wielded ↔ mode matches",
        //    so trust it. Calling UseObject on an already-wielded wand is a no-op in AC
        //    (it opens the wand's properties), so we must NOT call it here.
        if (CurrentCombatMode == desiredMode)
        {
            _stanceStuckSince = DateTime.MinValue; ResetWandSwapState(); ResetStanceFlipBackoff(); // reached the stance — clear stuck timer + swap + flip backoff
            ResetCombatWandWieldGate();
            // The matching mode can come from ANOTHER weapon of the same kind: the weakness
            // pick and the Damage tab choose between weapons of one kind, and trusting the
            // mode here meant the chosen one was never put in hand. That covered melee only;
            // a wand in hand in Magic mode was kept whatever the list said (2026-09-30).
            if (SwapToChosenSameKind(targetWeaponId, desiredMode))
                return false;
            // The chosen weapon read in hand (the wield-location read was only late): the off hand.
            if (InHandWeaponId() == targetWeaponId && OffhandFollowUp(target, rule, targetWeaponId))
                return false;
            return true;
        }

        // B-wand) Held-slot wand, not yet wielded. The wand CANNOT wield while a
        //    melee/missile weapon occupies the main hand — stock ACE's CheckWeaponCollision
        //    refuses it, AND the server DENIES ChangeCombatMode(Magic) while the bow is
        //    wielded ("GetEquippedWand()==null"), which is the Missile↔NonCombat↔Magic flap.
        //    So: tear down the in-flight attack, stow the bow into an open pack FIRST, then
        //    UseObject(wand). Do NOT request Magic here — the alreadyWielded branch above
        //    flips the stance once CurrentWieldedLocation confirms the wand. Mirrors the
        //    proven BuffManager.EnsureMagicMode dequip-first path (the missing 4th site).
        if (desiredMode == CombatMode.Magic)
        {
            // The wand is resting after failed wields and the hands are empty: ask for Magic
            // mode bare-handed (throttled) and send no wield.
            if (!allowWield)
            {
                if ((DateTime.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs())
                {
                    _host.ChangeCombatMode(desiredMode);
                    lastStanceAttempt = DateTime.Now;
                    NoteStanceFlipSent();
                }
                return false;
            }

            // Claim the shared swap slot FIRST so a concurrent buff/combat equip can't
            // collide; only then tear down / move items (don't cancel an attack then fail
            // to claim the slot). The 3s gate interval paces the dequip→wield steps.
            if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-wand-swap"))
                return false;

            if (!_combatSwapTeardownDone
                && (CurrentCombatMode == CombatMode.Melee || CurrentCombatMode == CombatMode.Missile))
            {
                if (_host.HasCancelAttack)   _host.CancelAttack();
                if (_host.HasStopCompletely) _host.StopCompletelyBy("Combat");
                _combatSwapTeardownDone = true;
                _host.Log($"[EquipDiag] CancelAttack+StopCompletely before wand equip (mode was {CurrentCombatMode})");
            }

            // Clear the main hand. Yields (false) while a dequip is in flight or blocked;
            // returns true only once the bow is confirmed out of the main hand.
            if (!EnsureHandClearForWand(targetWeaponId, diagNow))
                return false;

            // Main hand is clear (no bow), so the reason this branch withheld the
            // stance request — "the server DENIES ChangeCombatMode(Magic) while the bow
            // is wielded" — no longer applies. Send it. If the wand is in fact already
            // wielded and only the cached wield-location read is stale, this flip IS the
            // entire fix and no amount of UseObject would ever have produced it. Costs
            // nothing when the wand genuinely isn't wielded: ACE just denies it, and the
            // gated UseObject below still does the wielding. Mirrors the non-wand branch,
            // which has always sent both.
            if ((DateTime.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs())
            {
                if (diagNow) _host.Log($"[EquipDiag] hand clear — ChangeCombatMode({desiredMode}) alongside wand equip (mode={CurrentCombatMode})");
                _host.ChangeCombatMode(desiredMode);
                lastStanceAttempt = DateTime.Now;
                NoteStanceFlipSent();
            }

            // Main hand is clear — wield the wand through the wield gate. Stance flip
            // happens next tick in the alreadyWielded branch once the wand reads as
            // wielded. Do NOT re-send UseObject on a fixed 2s tick: each send is an
            // item MOVE to AC, and a continuous stream of them keeps the item-action
            // queue jammed so the wield never resolves. Issue one, wait for it,
            // cool down, cap.
            DateTime wnow = DateTime.Now;

            if (wnow < _wandWieldCooldownUntil)
                return false;

            if (_wandPendingWieldId != 0)
            {
                if ((wnow - _wandPendingWieldAt).TotalMilliseconds < WandWieldResolveTimeoutMs)
                    return false; // still resolving — give the server time, send nothing

                _wandWieldFailCount++;
                _host.Log($"[EquipDiag] wand UseObject(0x{_wandPendingWieldId:X8}) not confirmed in {WandWieldResolveTimeoutMs:0}ms — " +
                          $"cooling down {WandWieldCooldownMs:0}ms (fail {_wandWieldFailCount}/{WandWieldFailMax})");
                _wandPendingWieldId     = 0;
                _wandWieldCooldownUntil = wnow.AddMilliseconds(WandWieldCooldownMs);

                // Past the cap the wand provably won't wield right now. Say so once,
                // loudly, instead of standing in peace mode issuing item actions
                // forever — that silence is what made this take an hour to spot.
                if (_wandWieldFailCount >= WandWieldFailMax && !_wandWieldWedgeWarned)
                {
                    _wandWieldWedgeWarned = true;
                    _host.Log($"[EquipDiag] WAND WIELD WEDGED: 0x{targetWeaponId:X8} '{weaponObj.Name}' would not wield after {WandWieldFailMax} attempts " +
                              $"(mode={CurrentCombatMode}, busy={BusyCount}) — combat cannot enter Magic. Retrying on the cooldown cadence.");
                    _host.WriteToChat($"[RynthAi] Can't wield '{weaponObj.Name}' — combat is stuck out of Magic mode. Check the wand is reachable (not in a closed pack) or re-equip it manually.", 2);
                }
                return false;
            }

            if (diagNow) { _lastEquipDiagAt = DateTime.Now; _host.Log($"[EquipDiag] hand clear — UseObject(0x{targetWeaponId:X8} '{weaponObj.Name}') wand equip"); }
            _host.UseFor((uint)targetWeaponId, "Combat", "equip the wand");
            MarkWieldSent(targetWeaponId, inHand);
            _wandPendingWieldId = targetWeaponId;
            _wandPendingWieldAt = wnow;
            _lastEquipTime      = wnow;
            return false;
        }

        // B) Non-wand (melee/missile) weapon not yet confirmed wielded. Either it genuinely
        //    isn't wielded, or CurrentCombatMode is stale (e.g. hot-reload didn't re-fire
        //    OnCombatModeChange). Request both a mode change and an equip — these go in the
        //    main hand, which AC swaps in place (no held-slot collision). ChangeCombatMode
        //    succeeds if already wielded (fixes hot-reload next tick); UseObject equips it if not.
        if ((DateTime.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs())
        {
            if (diagNow) { _lastEquipDiagAt = DateTime.Now; _host.Log($"[EquipDiag] wielded=FALSE mode={CurrentCombatMode}→{desiredMode} (weapon=0x{targetWeaponId:X8} '{weaponObj.Name}' src={weaponSource} class={weaponObj.ObjectClass}) — ChangeCombatMode"); }
            _host.ChangeCombatMode(desiredMode);
            lastStanceAttempt = DateTime.Now;
            NoteStanceFlipSent();
        }
        if (allowWield && (DateTime.Now - _lastEquipTime).TotalMilliseconds > 2000
            && ClearOffhandFor(targetWeaponId, _curOffhandMode) == OffPrep.Ready   // a shield off before a two-hander / launcher
            && (desiredMode != CombatMode.Missile || PrepareAmmoFor(targetWeaponId) == AmmoPrep.Ready)
            && (_weaponSwapGate == null || _weaponSwapGate.TryBeginSwap("combat-equip")))
        {
            if (diagNow) { _lastEquipDiagAt = DateTime.Now; _host.Log($"[EquipDiag] wielded=FALSE UseObject(0x{targetWeaponId:X8} '{weaponObj.Name}') — equip attempt"); }
            _host.UseFor((uint)targetWeaponId, "Combat", "equip the weapon");
            MarkWieldSent(targetWeaponId, inHand);
            _lastEquipTime = DateTime.Now;
        }
        return false;
    }

    // Stow the wielded non-wand weapon (the bow) blocking the Held-slot wand into a
    // capacity-verified open pack (AutoCram-pattern, AV-safe), fully bounded. Returns
    // true only when the main hand is confirmed clear (safe to wield the wand); false
    // while a dequip is resolving, blocked, or just issued — caller yields this tick.
    private bool EnsureHandClearForWand(int wandId, bool diagNow)
    {
        int bowId = FindWieldedNonWandWeapon(wandId);
        if (bowId == 0)
        {
            _combatBowDequipPendingId = 0; _combatBowDequipAttempts = 0;
            // The main hand is clear; a caster also needs an empty off hand (ACE refuses a
            // Held-slot caster beside a shield), so the shield goes into the pack too.
            // Both callers claim the swap gate before coming here.
            return ClearOffhandFor(wandId, _curOffhandMode, gateHeld: true) == OffPrep.Ready;
        }

        DateTime now = DateTime.Now;
        if (_combatBowDequipPendingId == bowId
            && (now - _combatBowDequipAt).TotalMilliseconds < WandSwapWieldResolveMs
            && IsWieldedLive(bowId))
            return false; // dequip still resolving

        if (IsWieldedLive(bowId))
        {
            int openPack = WorldObjectCache.FindPackFor(_host, _worldFilter, includeMainPack: true, requireFree: 1);
            if (openPack == 0 || _combatBowDequipAttempts >= CombatBowDequipMaxAttempts)
            {
                // No verified-open pack to receive the bow, or repeated failures — the swap
                // can't complete now. Reset the attempt counter and yield; combat keeps
                // running with the bow (missile) until a slot frees (e.g. after looting).
                if (diagNow)
                {
                    _lastEquipDiagAt = now;
                    _host.Log($"[EquipDiag] bow 0x{(uint)bowId:X8} dequip blocked (openPack=0x{(uint)openPack:X8}, attempts={_combatBowDequipAttempts}/{CombatBowDequipMaxAttempts}) — cannot swap to wand yet");
                }
                if (openPack == 0)
                    WorldObjectCache.WarnPackFull(_host, "can't put the weapon away to switch to the wand for combat spells");
                _combatBowDequipPendingId = 0;
                _combatBowDequipAttempts = 0;
                return false;
            }
            _combatBowDequipAttempts++;
            _host.MoveItemInternal((uint)bowId, (uint)openPack, 0, 1); // amount>=1 (engine rejects 0)
            _combatBowDequipPendingId = bowId;
            _combatBowDequipAt = now;
            _host.Log($"[EquipDiag] dequip bow 0x{(uint)bowId:X8} -> pack 0x{(uint)openPack:X8} (attempt {_combatBowDequipAttempts}/{CombatBowDequipMaxAttempts}) before wand equip");
            return false; // yield until the bow is out of hand
        }

        _combatBowDequipPendingId = 0;
        _combatBowDequipAttempts = 0;
        return true; // bow confirmed unwielded — main hand clear
    }

    // The currently-wielded non-wand weapon (the bow) that blocks the Held-slot wand.
    private int FindWieldedNonWandWeapon(int wandId)
    {
        foreach (var wo in _worldFilter.GetDirectInventory(forceRefresh: true))
        {
            if (wo.Id == wandId) continue;
            if (IsWandObject(wo)) continue;
            if (WorldObjectCache.IsAmmo(wo)) continue;   // arrows stay on; only the bow blocks the wand
            if ((wo.ObjectClass == AcObjectClass.MeleeWeapon
                 || wo.ObjectClass == AcObjectClass.MissileWeapon)
                && WorldObjectCache.IsWieldedByPlayer(_host, wo))
                return wo.Id;
        }
        return 0;
    }

    // Live (forceRefresh) wielded check — never trusts a stale cache snapshot.
    private bool IsWieldedLive(int id)
    {
        if (id == 0) return false;
        foreach (var wo in _worldFilter.GetDirectInventory(forceRefresh: true))
            if (wo.Id == id)
                return WorldObjectCache.IsWieldedByPlayer(_host, wo);
        if (_host.HasGetObjectWielderInfo)
        {
            uint pid = _host.GetPlayerId();
            if (pid != 0 && _host.TryGetObjectWielderInfo((uint)id, out uint wielder, out _))
                return wielder == pid;
        }
        return false;
    }

    /// <summary>
    /// Ensure a wand is wielded and the player is in Magic combat mode.
    /// Used by NavigationEngine to prepare for recall casts. Returns true only when
    /// ready to cast; when false, caller should re-tick (equip/stance are throttled
    /// internally so calling every tick is safe).
    /// </summary>
    // Nav calls EnsureMagicReady every tick while a Recall waypoint is firing. Until
    // this lapses, the idle peace-mode switch leaves the stance alone: it used to send
    // NonCombat on the tick Magic landed, right before nav's recall cast.
    private DateTime _navMagicWantedUntil = DateTime.MinValue;

    public bool EnsureMagicReady()
    {
        _navMagicWantedUntil = DateTime.Now.AddMilliseconds(2000);
        int wandId = FindWandInItems();
        // No target here: the global off-hand setting. With Offhand None and a shield on, the
        // wand can't be wielded (the server refuses a caster beside it): cast bare-handed.
        _curOffhandMode = OffhandRules.Parse(_settings.OffhandDefault, OffhandMode.Auto);
        if (wandId != 0 && _worldFilter[wandId] is WorldObject w0 && !WorldObjectCache.IsWieldedByPlayer(_host, w0)
            && OffhandBlocksMain(wandId, _curOffhandMode))
            wandId = 0;
        if (wandId == 0)
        {
            // No caster anywhere: recalls cast bare-handed. This used to return false
            // for good, even in Magic mode, so a Recall waypoint never cast and sat
            // out the whole portal timeout.
            if (CurrentCombatMode == CombatMode.Magic) { ResetStanceFlipBackoff(); return true; }
            if ((DateTime.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs())
            {
                _host.ChangeCombatMode(CombatMode.Magic);
                lastStanceAttempt = DateTime.Now;
                NoteStanceFlipSent();
            }
            return false;
        }

        var wand = _worldFilter[wandId];
        if (wand == null) return false;

        // Location, else the live wielder id: a wand in hand whose location read 0 was
        // used again and again (equip/unequip) and never got to magic mode (2026-09-29).
        bool alreadyWielded = WorldObjectCache.IsWieldedByPlayer(_host, wand);

        if (alreadyWielded)
        {
            if (CurrentCombatMode == CombatMode.Magic) { ResetWandSwapState(); ResetStanceFlipBackoff(); return true; }
            if ((DateTime.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs())
            {
                _host.ChangeCombatMode(CombatMode.Magic);
                lastStanceAttempt = DateTime.Now;
                NoteStanceFlipSent();
            }
            return false;
        }

        if (CurrentCombatMode == CombatMode.Magic) { ResetWandSwapState(); ResetStanceFlipBackoff(); return true; }

        // Wand not wielded. Dequip the blocking bow FIRST (stock ACE won't auto-dequip a
        // main-hand weapon for the Held-slot wand, and denies ChangeCombatMode(Magic) while
        // it's wielded). Do NOT request Magic until the wand is wielded — the alreadyWielded
        // branch above handles the stance flip. Mirrors EquipWeaponAndSetStance / BuffManager.
        if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-magicready-wand"))
            return false;
        if (!_combatSwapTeardownDone
            && (CurrentCombatMode == CombatMode.Melee || CurrentCombatMode == CombatMode.Missile))
        {
            if (_host.HasCancelAttack)   _host.CancelAttack();
            if (_host.HasStopCompletely) _host.StopCompletelyBy("Combat");
            _combatSwapTeardownDone = true;
        }
        if (!EnsureHandClearForWand(wandId, diagNow: true))
            return false;
        if ((DateTime.Now - _lastEquipTime).TotalMilliseconds > 2000)
        {
            _host.UseFor((uint)wandId, "Combat", "equip the wand");
            _lastEquipTime = DateTime.Now;
        }
        return false;
    }

    public string GetRaycastStatus()
    {
        if (_raycastSystem == null) return "Raycasting: NOT INITIALIZED";
        string status = _settings.EnableRaycasting ? "ACTIVE" : "DISABLED";
        return $"Raycasting: {status}\n  Status: {_raycastSystem.StatusMessage}\n  Checks: {RaycastCheckCount}, Blocks: {RaycastBlockCount}";
    }

    public List<string> GetRaycastDiagLog()
    {
        var lines = new List<string>();
        if (_raycastSystem?.GeometryLoader?.DiagLog != null)
            foreach (var line in _raycastSystem.GeometryLoader.DiagLog) lines.Add(line);
        return lines;
    }

    /// <summary>
    /// Attack type for the scan's LOS test. Missile mode names the weapon in hand so the
    /// bow/crossbow/atlatl arc and its velocity apply (the scan used to pass "", which made
    /// every missile LOS test a straight line). Peace mode with a missile weapon picked counts
    /// as missile, so a target acquired from peace is tested with the arc it will be shot on.
    /// Melee (or peace with a melee weapon) is one chest-to-chest ray. Magic stays a straight
    /// line with the dungeon silhouette rays, as before.
    /// </summary>
    private TargetingFSM.AttackType DetermineAttackTypeForLOS()
    {
        if (_raycastSystem?.TargetingFSM == null) return TargetingFSM.AttackType.Linear;

        int mode = CurrentCombatMode;
        WorldObject? weapon = _equippedWeaponId != 0 ? _worldFilter[_equippedWeaponId] : null;
        bool missileWeapon = weapon != null && weapon.ObjectClass == AcObjectClass.MissileWeapon;
        if (mode == CombatMode.NonCombat && missileWeapon)
            mode = CombatMode.Missile;
        // Same for melee: a target picked from peace is judged as the melee attack will be,
        // so the verdict doesn't change when the stance does.
        else if (mode == CombatMode.NonCombat && weapon != null && weapon.ObjectClass == AcObjectClass.MeleeWeapon)
            mode = CombatMode.Melee;

        string weaponName = mode == CombatMode.Missile && missileWeapon ? weapon!.Name ?? "" : "";
        return _raycastSystem.GetAttackType(mode, weaponName);
    }

    // ── LOS / target-distance diagnostics ────────────────────────────────────
    // "Far away" can't be judged from the log without positions, so target lock, switch and
    // drop lines carry the 3D distance, its horizontal and vertical parts and both cells.
    // With LosDebugLog on, the scan also logs each target's LOS verdict (line / arc, rise,
    // hit point) when it changes and at most every LosDebugRepeatMs otherwise.
    private readonly Dictionary<int, (DateTime At, bool Blocked)> _losDebugLast = new();
    private const double LosDebugRepeatMs = 5000;
    private const double LosDebugMinGapMs = 1000;
    private int _losDebugWeaponId;

    /// <summary>"d=6.2m (h=5.9 dz=+1.8) cell=0x61450123 me=0x61450119", or why it can't be measured.</summary>
    private string DescribeTargetPos(int targetId)
    {
        uint pid = _host.GetPlayerId();
        if (pid == 0 || targetId == 0) return "pos=?";
        if (!_host.TryGetObjectPosition(pid, out uint pc, out float px, out float py, out float pz))
            return "pos=? (no player position)";
        if (!_host.TryGetObjectPosition((uint)targetId, out uint tc, out float tx, out float ty, out float tz))
            return "pos=? (no target position)";
        double dx = (((tc >> 24) & 0xFF) * 192.0 + tx) - (((pc >> 24) & 0xFF) * 192.0 + px);
        double dy = (((tc >> 16) & 0xFF) * 192.0 + ty) - (((pc >> 16) & 0xFF) * 192.0 + py);
        double dz = tz - pz;
        double h = Math.Sqrt(dx * dx + dy * dy);
        double d = Math.Sqrt(h * h + dz * dz);
        return $"d={d:0.0}m (h={h:0.0} dz={dz:+0.0;-0.0;0.0}) cell=0x{tc:X8} me=0x{pc:X8}";
    }

    private void LogLosDebug(WorldObject wo, bool blocked, in TargetingFSM.LosDetail det)
    {
        DateTime now = DateTime.Now;
        if (_losDebugLast.TryGetValue(wo.Id, out var last))
        {
            double ms = (now - last.At).TotalMilliseconds;
            bool changed = last.Blocked != blocked;
            if (ms < LosDebugMinGapMs || (!changed && ms < LosDebugRepeatMs)) return;
        }
        if (_losDebugLast.Count > 256) _losDebugLast.Clear();
        _losDebugLast[wo.Id] = (now, blocked);

        string verdict;
        if (!det.Checked) verdict = "clear (not tested: no geometry near the path)";
        else if (det.LineBlocked) verdict = "BLOCKED line";
        else if (det.ArcChecked && det.Arc.OutOfReach) verdict = "BLOCKED arc: out of reach at this velocity";
        else if (det.ArcChecked && det.Arc.Blocked) verdict = $"BLOCKED arc: hit {det.Arc.HitAlong:0.0}m out, {det.Arc.HitZ:+0.0;-0.0;0.0}m vs launch";
        else verdict = "clear";

        string arcInfo = det.Velocity > 0
            ? $" v={det.Velocity:0.0} rise={det.Arc.Sag:0.00} apex={det.Arc.Apex:0.00} clr={_settings.MissileArcClearance:0.0}{(det.ArcChecked ? "" : " (flat: line only)")}"
            : "";
        _host.Log($"[LOS] 0x{(uint)wo.Id:X8} '{wo.Name}' {verdict} | {det.Type}{(det.Dungeon ? " dungeon" : "")}{arcInfo} | {DescribeTargetPos(wo.Id)}");
    }

    /// <summary>LOS debug: once per missile weapon, what the client knows of its launch speed.</summary>
    private void LogLosDebugWeapon()
    {
        int wid = _equippedWeaponId;
        if (wid == 0 || wid == _losDebugWeaponId) return;
        var weapon = _worldFilter[wid];
        if (weapon == null || weapon.ObjectClass != AcObjectClass.MissileWeapon) return;
        _losDebugWeaponId = wid;
        double maxVel = _worldFilter.GetDoubleProperty(wid, (uint)DoubleValueKey.MaximumVelocity, 0);
        var type = DetermineAttackTypeForLOS();
        float setting = _raycastSystem?.TargetingFSM?.VelocityFor(type) ?? 0f;
        _host.Log($"[LOS] missile weapon 0x{(uint)wid:X8} '{weapon.Name}': MaximumVelocity={(maxVel > 0 ? maxVel.ToString("0.0") : "unknown (not appraised)")}, " +
                  $"LOS uses {type} v={setting:0.0}");
    }

    private void FaceTarget(int targetId)
    {
        try
        {
            if (!_host.TryGetPlayerPose(out _, out float px, out float py, out _,
                    out float qw, out _, out _, out float qz))
                return;
            if (!_host.TryGetObjectPosition((uint)targetId, out _, out float tx, out float ty, out _))
                return;

            double dx = tx - px;
            double dy = ty - py;
            double desiredDeg = Math.Atan2(dx, dy) * (180.0 / Math.PI);
            if (desiredDeg < 0) desiredDeg += 360.0;

            double physYawDeg = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
            double currentDeg = ((-physYawDeg) % 360.0 + 720.0) % 360.0;

            double error = desiredDeg - currentDeg;
            while (error >  180.0) error -= 360.0;
            while (error < -180.0) error += 360.0;

            if (Math.Abs(error) <= FACE_TOLERANCE_DEG)
            {
                ClearCombatTurnMotions();
            }
            else if (error > 0)
            {
                _host.SetMotionBy("Combat", MotionTurnRight, true);
                _host.SetMotionBy("Combat", MotionTurnLeft,  false);
            }
            else
            {
                _host.SetMotionBy("Combat", MotionTurnLeft,  true);
                _host.SetMotionBy("Combat", MotionTurnRight, false);
            }
        }
        catch { }
    }

    private void ClearCombatTurnMotions()
    {
        _host.SetMotionBy("Combat", MotionTurnRight, false);
        _host.SetMotionBy("Combat", MotionTurnLeft,  false);
    }

    /// <summary>Let go of a turn the facing servo is holding. Called when combat stops
    /// ticking (macro off), which would otherwise leave the turn held for good.</summary>
    public void ReleaseHeldTurn()
    {
        if (!_facingTarget) return;
        _facingTarget = false;
        ClearCombatTurnMotions();
    }

    /// <summary>
    /// True while a previously-issued combat cast is still resolving on the ACE
    /// server. Serializes magic casts so the next cast's turn/stop motion can't
    /// orphan the prior cast's deferred windup. Cleared when a server UseDone
    /// (0x1C7) is observed since the cast (the server finished the action —
    /// completed or refused) or a hard timeout elapses. On an engine without
    /// UseDone observation (HasUseDoneSeq == false) the timeout is the sole gate.
    /// </summary>
    private bool IsAwaitingCastResolution()
    {
        if (!_awaitingCastResolution) return false;
        if (_host.HasUseDoneSeq && _host.GetUseDoneSeq() != _useDoneSeqAtCast)
        {
            _awaitingCastResolution = false;
            return false;
        }
        if (DateTime.Now >= _castResolutionDeadline)
        {
            _awaitingCastResolution = false;
            return false;
        }
        return true;
    }

    /// <summary>True while a targeted combat cast is still resolving on the server (windup
    /// in flight). Shares the bounded IsAwaitingCastResolution state, so it self-clears on
    /// the UseDone-seq advance or the 2.5s timeout — used to gate motion emitters that would
    /// otherwise truncate the cast gesture and strand server Player.IsBusy (relog-only wedge).</summary>
    private bool IsCastInFlight() => IsAwaitingCastResolution();

    /// <summary>Record that a targeted combat cast was just issued, so the next
    /// cast waits for it to resolve on the server (see IsAwaitingCastResolution).</summary>
    private void MarkCombatCastIssued()
    {
        _attackedThisTick = true;   // [CombatLatency]: a debuff or attack cast went out
        _awaitingCastResolution = true;
        _castResolutionDeadline = DateTime.Now.AddMilliseconds(CAST_RESOLUTION_TIMEOUT_MS);
        _useDoneSeqAtCast = _host.HasUseDoneSeq ? _host.GetUseDoneSeq() : 0;
    }

    /// <summary>
    /// Returns the absolute heading error (degrees) between the player's current
    /// facing and the direction to the target. Used to decide whether we need to
    /// wait for a heading change before firing a ranged attack.
    /// </summary>
    private double GetFacingError(int targetId)
    {
        try
        {
            if (!_host.TryGetPlayerPose(out _, out float px, out float py, out _,
                    out float qw, out _, out _, out float qz))
                return 180.0; // can't read pose — assume worst case

            if (!_host.TryGetObjectPosition((uint)targetId, out _, out float tx, out float ty, out _))
                return 180.0;

            // Desired heading to target (0=North CW)
            double dx = tx - px;
            double dy = ty - py;
            double desiredDeg = Math.Atan2(dx, dy) * (180.0 / Math.PI);
            if (desiredDeg < 0) desiredDeg += 360.0;

            // Current heading from quaternion (same formula as NavigationEngine)
            double physYawDeg = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
            double currentDeg = ((-physYawDeg) % 360.0 + 720.0) % 360.0;

            double error = desiredDeg - currentDeg;
            while (error > 180.0) error -= 360.0;
            while (error < -180.0) error += 360.0;
            return Math.Abs(error);
        }
        catch { return 180.0; }
    }

    private bool HasWieldedAmmo()
    {
        int playerId = unchecked((int)_playerId);

        // Walk via GetDirectInventory(forceRefresh:true). This is the same path
        // MissileCraftingManager uses successfully — it triggers per-item
        // wielder-info lookups on the cache, which populates Wielder /
        // WieldedLocation. AllKnownObjects() doesn't trigger those probes, so
        // arrows that arrived via OnCreateObject keep WieldedLocation=0 and
        // never match. The forced refresh adds ~one InqInt call per pack item
        // but is cheap and fixes detection definitively.
        foreach (var item in _worldFilter.GetDirectInventory(forceRefresh: true))
            if (LooksLikeWieldedAmmo(item, playerId)) return true;

        return false;
    }

    // EquipMask bit for the ammunition slot. Items wielded in this slot are ammo
    // by definition — far more reliable than name or ItemType inspection because
    // some servers type their arrows as MissileWeapon (0x100) rather than the
    // MissileAmmo bit (0x400) that AC's vanilla data has.
    private const int AmmunitionSlot = 0x00800000;
    private const int MissileWeaponSlot = 0x00400000;   // bow, crossbow, atlatl — or a thrown weapon

    private bool LooksLikeWieldedAmmo(WorldObject item, int playerId)
    {
        if (item == null) return false;

        // Authoritative: ask AC's runtime for the wielder + slot directly.
        // The cached WieldedLocation/Wielder fields can be 0 forever if the
        // item arrived via OnCreateObject and never went through the
        // GetDirectInventory walk that probes wielder info. Querying the
        // host API per-candidate side-steps that.
        int loc = 0;
        bool slotKnown = false;
        if (_host.HasGetObjectWielderInfo &&
            _host.TryGetObjectWielderInfo(unchecked((uint)item.Id), out uint wielder, out uint locFromApi))
        {
            if (playerId != 0 && wielder != 0 && wielder != (uint)playerId) return false;
            if (locFromApi > 0) { loc = unchecked((int)locFromApi); slotKnown = true; }
        }

        // Fall back to InqInt and the cache field if the wielder API didn't answer.
        if (!slotKnown)
        {
            int locInq   = item.Values(LongValueKey.CurrentWieldedLocation, 0);
            int locCache = item.WieldedLocation;
            loc = locInq > 0 ? locInq : locCache;
            if (loc <= 0) return false;
            if (playerId != 0 && item.Wielder != 0 && item.Wielder != playerId) return false;
        }

        // Authoritative: ammunition slot bit.
        if ((loc & AmmunitionSlot) != 0)
            return true;

        // A thrown weapon (throwing axe/club, shuriken, javelin, phial) sits in the
        // missile-weapon slot and is its own ammo. Launchers don't stack; thrown weapons do.
        // Without this, a thrown-weapon character never counted as having ammo and combat
        // held its target in the wrong mode forever.
        if ((loc & MissileWeaponSlot) != 0 && item.ObjectClass == AcObjectClass.MissileWeapon
            && item.Values(LongValueKey.MaxStackSize, 0) > 1)
            return true;

        // Name-based fallback for items in non-ammo slots that still match
        // ammo names (rare server-custom configurations).
        string n = item.Name;
        if (string.IsNullOrEmpty(n)) return false;
        if (n.Contains("Bundle") || n.Contains("Wrapped")) return false;
        return n.Contains("Arrow") || n.Contains("Quarrel") || n.Contains("Bolt") || n.Contains("Dart");
    }

    /// <summary>Send one melee/missile attack at the active target. False when nothing was
    /// sent this cycle (no ammo, or the server's attack had to be released first).</summary>
    private bool AttackTarget()
    {
        try
        {
            bool isMissile = CurrentCombatMode == CombatMode.Missile;
            uint targetId  = (uint)activeTargetId;

            // Don't fire in missile mode without ammo — let crafting manager handle it
            if (isMissile && !HasWieldedAmmo())
                return false;

            // The server's attack is still bound to another mob that may be alive: ACE
            // would drop this request and keep chasing that one (see _serverAttackTargetId).
            // Release it and attack on the next cycle — sent together, the cancel of an
            // in-progress swing lands after the swing, so the attack would be dropped too.
            if (_serverAttackTargetId != 0 && _serverAttackTargetId != activeTargetId
                && !IsKnownDead(_serverAttackTargetId))
            {
                _attackRebinds++;
                ReleaseServerAttack($"target is now 0x{targetId:X8} '{_worldFilter[activeTargetId]?.Name}' {DescribeTargetPos(activeTargetId)}", log: true);
                LastCombatSkipReason = "attack-rebind";
                return false;
            }

            float power;
            int powerPct = isMissile ? _settings.MissileAttackPower : _settings.MeleeAttackPower;
            if (powerPct < 0)
            {
                power = 1.0f;
                if (_settings.UseRecklessness && (_charSkills == null || _charSkills[AcSkillType.Recklessness].Training >= 2))
                    power = 0.8f;
            }
            else
            {
                power = powerPct / 100f;
            }

            int uiHeight = isMissile ? _settings.MissileAttackHeight : _settings.MeleeAttackHeight;
            int acHeight = uiHeight switch { 0 => 3, 2 => 1, _ => 2 }; // Low=3, Med=2, High=1

            // Native attack: select target, Start fills power bar, End fires the attack.
            // Called each attack cycle — the client handles turn-to-face naturally.
            if (_settings.UseNativeAttack && _host.HasNativeAttack)
            {
                _host.SelectItem(targetId);
                _host.NativeAttack(acHeight, power);
            }
            // Direct attack: raw game action (bypasses client facing)
            else if (isMissile)
                _host.MissileAttack(targetId, acHeight, power);
            else
                _host.MeleeAttack(targetId, acHeight, power);

            _serverAttackTargetId = activeTargetId;
            LastCombatSkipReason = "attack"; // D4 record-only: physical attack sent this tick
            return true;
        }
        catch { return false; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MAGIC COMBAT SYSTEM
    // ══════════════════════════════════════════════════════════════════════════

    private MonsterRule? GetRuleForTarget(WorldObject? target)
    {
        if (target == null) return null;

        string metaState = _settings.CurrentState ?? "Default";

        foreach (var r in _settings.MonsterRules)
        {
            if (r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;

            bool matches;
            if (!string.IsNullOrWhiteSpace(r.MatchExpression))
            {
                // Expression match: eval first; if expression is true AND name is non-empty, also check name.
                bool exprTrue = _monsterMatchEval?.Evaluate(r.MatchExpression, target, metaState) ?? false;
                if (!string.IsNullOrWhiteSpace(r.Name))
                    matches = exprTrue && target.Name.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0;
                else
                    matches = exprTrue;
            }
            else
            {
                matches = !string.IsNullOrWhiteSpace(r.Name) &&
                          target.Name.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (matches) return r;
        }

        return _settings.MonsterRules.FirstOrDefault(
            m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
    }

    private void AttackWithMagic(WorldObject target)
    {
        if (_spellManager == null || target == null) return;

        // Hard safety veto: never cast offensive magic at something AC's combat
        // system says is not attackable. AC NPCs/vendors are ItemType=Creature,
        // so classification (the ItemType-flag rescue path in WorldObjectCache)
        // can still promote them to "Monster" even after the engine fix cleans
        // the primary attackable-gated path. The attackable check is the game's
        // own authority for "can I fight this". The engine serves the REAL
        // ObjectIsAttackable from a main-thread snapshot to this off-thread
        // pump; if it says not-attackable, bail before any cast/select.
        if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)target.Id))
            return;

        // Keep the authoritative known-spell snapshot warm for the combat
        // resolver even when the char isn't buffing (GetDynamicSelfBuffId is
        // the only other pump). Throttled internally — cheap to call per tick.
        _spellManager.RefreshKnownSpells();

        // Combat spell selection is now purely predictive (known ∧ scarab ∧
        // skill-window tier ∧ configured shape). No empirical no-chat →
        // blacklist valve: it falsely poisoned KNOWN spells whenever a cast
        // didn't execute, which is what collapsed war to Force Bolt I and
        // re-poisoned bufftimers.txt every few seconds.

        // Don't cast while a weapon equip is in progress — the wand may not be registered
        // as wielded yet. _lastEquipTime is set whenever UseObject is called for a wand swap.
        if ((DateTime.Now - _lastEquipTime).TotalMilliseconds < 3000)
        {
            _host.Log($"[CombatCast] equip-gate: wand equip in progress ({(DateTime.Now - _lastEquipTime).TotalMilliseconds:0}ms < 3000ms), skipping cast");
            LastCombatSkipReason = "equip-gate"; // D4 record-only
            return;
        }
        // Cadence is set in Think before this is called: the previous cast must have resolved
        // (UseDone, IsAwaitingCastResolution), its gesture must be over (CanCastNow), and the
        // Attack Spell Delay must have passed since the last cast at this same target (the first
        // cast at a new target skips that delay when the two server gates exist — AttackIntervalMs).

        if (_waitingForDebuffResult)
        {
            if ((DateTime.Now - _pendingDebuffCastTime).TotalMilliseconds > DEBUFF_RESULT_TIMEOUT_MS)
            {
                _confirmedDebuffs.Add($"{_pendingDebuffTargetId}_{_pendingDebuffKey}");
                _waitingForDebuffResult = false;
            }
            else
            {
                LastCombatSkipReason = "awaiting-debuff-result"; // D4 record-only
                return;
            }
        }

        if (activeTargetId != _lastDebuffTargetId)
        {
            _confirmedDebuffs.Clear();
            _debuffFailures.Clear();
            _lastDebuffTargetId = activeTargetId;
        }

        var rule = GetRuleForTarget(target);
        string element = GetPreferredElement(target, rule);

        if (rule != null)
        {
            var pendingDebuffs = BuildDebuffList(rule, element);
            foreach (var debuffKey in pendingDebuffs)
            {
                string key = $"{activeTargetId}_{debuffKey}";
                if (_confirmedDebuffs.Contains(key)) continue;

                int spellId = 0;
                int castTier = 0;
                if (debuffKey.StartsWith("Vuln:"))
                    spellId = FindBestVulnSpellWithTier(debuffKey.Substring(5), out castTier);
                else if (debuffKey.StartsWith("Custom:"))
                    spellId = FindBestCustomDebuffWithTier(debuffKey.Substring(7), out castTier);
                else
                    spellId = FindBestDebuffSpellWithTier(debuffKey, out castTier);

                if (spellId == 0) continue;

                try
                {
                    _host.CastSpell((uint)activeTargetId, spellId);
                    MarkCombatCastIssued();
                    _lastSpellCast = DateTime.Now;
                    _lastCastWasRing = false;
                    _pendingDebuffKey = debuffKey;
                    _pendingDebuffTargetId = activeTargetId;
                    _pendingDebuffTier = castTier;
                    _waitingForDebuffResult = true;
                    _pendingDebuffCastTime = DateTime.Now;
                    _host.WriteToChat($"[RynthAi] Casting: {debuffKey} (T{castTier}) on {target.Name}", 5);
                }
                catch { }
                return;
            }
        }

        if (rule != null && PickBaseShape(rule) < 0)
        {
            _host.Log($"[CombatCast] rule '{rule.Name}' has no attack shapes enabled (UseArc/Ring/Streak/Bolt/Blast all false) — no offensive cast");
            LastCombatSkipReason = "no-attack-shapes"; // D4 record-only
            return;
        }

        int warTier  = _spellManager?.GetHighestSpellTier(AcSkillType.WarMagic)  ?? 0;
        int voidTier = _spellManager?.GetHighestSpellTier(AcSkillType.VoidMagic) ?? 0;
        int offensiveSpellId = FindBestShapedSpell(element, rule, out bool isRing, out int castShape, arcTargetId: activeTargetId);
        // No spell of that element known (a low caster whose listed knife says Slash, with no
        // Slash war spell): cast an element the character does know rather than nothing every
        // cycle (2026-10-02 log; found 2026-10-04). Not when an arc gave way to an obstacle.
        if (offensiveSpellId == 0 && !_lastShapeArcGaveWay)
        {
            foreach (string alt in WeaponPlanner.FallbackOrder)
            {
                if (alt.Equals(element, StringComparison.OrdinalIgnoreCase)) continue;
                int altId = FindBestShapedSpell(alt, rule, out bool altRing, out int altShape, arcTargetId: activeTargetId);
                if (altId == 0) continue;
                if (_noElementFallbackLogged.Add(element))
                    _host.Log($"[CombatCast] no {element} attack spell known: casting {alt} instead");
                element = alt; offensiveSpellId = altId; isRing = altRing; castShape = altShape;
                break;
            }
        }
        LogArcChoice(rule, target, castShape);
        if (offensiveSpellId != 0)
        {
            LastCombatSkipReason = "cast"; // D4 record-only: offensive cast issued this tick
            // Visibility: log exactly what war/void spell we're about to cast
            // (id + resolved name + element/tier/target). Diagnostic only.
            _host.Log($"[CombatCast] offensive id={offensiveSpellId} " +
                      $"'{SpellTableStub.GetById(offensiveSpellId)?.Name}' elem={element} " +
                      $"shape={ShapeName(castShape)} ring={isRing} warTier={warTier} voidTier={voidTier} target='{target.Name}'");
            try
            {
                _host.CastSpell((uint)activeTargetId, offensiveSpellId);
                MarkCombatCastIssued();
                SessionAttackCasts++;      // D6: offensive attack-cast tally for the casts-per-kill signal
                CastsSinceLastKill++;      //     reset in OnKillNotification when a kill lands
                _lastSpellCast = DateTime.Now;
                _lastCastWasRing = isRing;
                // _offensiveCastThisCycle is NOT set here anymore — a cast
                // attempt that the server refuses (e.g. "You're too busy!")
                // must not count toward the blacklist miss streak. The
                // RecordOffensiveCast trigger moved to the chat-confirmation
                // path so only chat-confirmed casts queue a judgement.
                _pendingOffensiveSpellId   = offensiveSpellId;
                _pendingOffensiveCastAt    = DateTime.Now;
                _pendingOffensiveTargetId  = activeTargetId;

                // Tag this cast's weapon + element + tier so the pushed damage event
                // can be attributed to it, count it for casts-to-kill learning, then
                // decide if it's a predicted kill shot.
                // The wand really in hand (it was the first listed wand, so the Damage tab
                // filed every cast under that one whatever was wielded).
                int castWand = InHandWeaponId();
                if (castWand == 0 || !IsWandObject(_worldFilter[castWand] ?? new WorldObject(0, ""))) castWand = FindWandInItems();
                _lastCastWeaponId = unchecked((uint)castWand);
                _lastCastElement  = element;
                // Tier = the LEVEL of the spell actually cast (1-8), not the caster's highest
                // castable tier (which made every magic kill record tier 8). Fall back to the
                // old max-tier only if the level can't be derived, so it never regresses to blank.
                int castLevel = SpellTableStub.GetById(offensiveSpellId)?.Level ?? 0;
                int castTierMag = castLevel > 0 ? castLevel : Math.Max(warTier, voidTier);
                // Ring spells are stored as a NEGATIVE tier so the Damage tab can render them as
                // "R<level>" (e.g. a level-2 ring = -2 -> "R2"); non-ring stays positive; 0 = none.
                _lastCastTier     = isRing ? -castTierMag : castTierMag;
                // Record the latest tier used vs this monster so the Damage tab's collapsed row
                // reflects it immediately (covers ring casts, which skip RecordHit).
                if (_fightTargetWcid != 0) _damageStore?.NoteCast(_fightTargetWcid, _lastCastTier, _fightTargetName);
                _fightCastCount++;
                // Snapshot this fight so a kill that lands after we've dropped/advanced
                // can still be credited by name (see OnKillNotification step 3).
                if (_fightTargetWcid != 0)
                {
                    _lastFightWcid     = _fightTargetWcid;
                    _lastFightName     = _worldFilter[activeTargetId]?.Name ?? _fightTargetName;
                    _lastFightWeaponId = _lastCastWeaponId;
                    _lastFightElement  = _lastCastElement;
                    _lastFightTier     = _lastCastTier;
                    _lastFightCount    = _fightCastCount;
                    StampRecentFight();
                }
                EvaluateKillShot(_lastCastWeaponId, element, _lastCastTier, isRing);
            }
            catch { }
        }
        else
        {
            bool snapWarm = _spellManager?.IsKnownSnapshotWarm == true;
            // Chat at most every 30 s (it used to go out every attack cycle; sustained chat is
            // the AddTextToScroll crash path). The file log keeps every occurrence.
            if ((DateTime.Now - _lastNoSpellChatAt).TotalMilliseconds >= NoSpellChatIntervalMs)
            {
                _lastNoSpellChatAt = DateTime.Now;
                _host.WriteToChat($"[RynthAi] No spell found: elem={element} warTier={warTier} voidTier={voidTier} snapshotWarm={snapWarm} pid={_playerId}", 2);
            }
            _host.Log($"[CombatCast] no offensive spell: elem={element} warTier={warTier} voidTier={voidTier} snapshotWarm={snapWarm} rule={rule?.Name ?? "null"} target='{target.Name}'" +
                      (_lastShapeArcGaveWay ? " (arc blocked, and no other shape or bolt known: no arc cast into the obstacle)" : ""));
            LastCombatSkipReason = "no-offensive-spell"; // D4 record-only (FindBestShapedSpell==0)
            _lastSpellCast = DateTime.Now;
        }
    }

    private int _autoElemDiagCount;
    private readonly HashSet<string> _noElementFallbackLogged = new(StringComparer.OrdinalIgnoreCase);

    private string GetPreferredElement(WorldObject? target, MonsterRule? rule)
    {
        if (rule != null && !string.IsNullOrEmpty(rule.DamageType) &&
            !rule.DamageType.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return rule.DamageType;

        // Auto: the same plan the weapon choice makes (WeaponPlanner), so the spell and the
        // wand agree: the weakest element a listed wand has (ties go to the wand in hand),
        // else the weakest the character can cast; weakness unknown: the wand in hand's
        // element; nothing known: Slash. It used to ignore the wand, and ties in the weakness
        // table went to Slash (listed first), so a fire wand cast Blades (2026-09-30).
        if (target == null) return WeaponPlanner.FallbackOrder[0];
        var plan = PlanFor(target, rule);
        if (_autoElemDiagCount < 20)
        {
            _autoElemDiagCount++;
            var weak = WeaknessFor(target);
            _host.Log($"[CombatCast] auto-element '{target.Name}': {plan.Element} (weapon 0x{(uint)plan.WeaponId:X8}, {plan.Source}; {weak?.Source ?? "weakness unknown"}: {weak?.Describe() ?? ""})");
        }
        return plan.Element.Length > 0 ? plan.Element : WeaponPlanner.FallbackOrder[0];
    }

    /// <summary>
    /// This character can cast <paramref name="element"/> itself (FindBestShapedSpell falls
    /// back to Fire for anything it can't, so a non-zero result alone doesn't say that).
    /// </summary>
    private bool CanCastElement(string element, MonsterRule? rule)
    {
        if (CanCastOverride != null) return CanCastOverride(element);
        bool isVoid = element.Equals("Nether", StringComparison.OrdinalIgnoreCase);
        if (isVoid)
        {
            if (_charSkills != null && _charSkills[AcSkillType.VoidMagic].Training < 2) return false;
            if (!VoidSpellShapes.ContainsKey("Nether")) return false;
        }
        else
        {
            if (_charSkills != null && _charSkills[AcSkillType.WarMagic].Training < 2) return false;
            if (!SpellShapes.ContainsKey(element)) return false;
        }
        return FindBestShapedSpell(element, rule, out _) != 0;
    }

    private bool HasPendingDebuffs(MonsterRule rule, string element)
    {
        if (rule == null) return false;
        foreach (var debuffKey in BuildDebuffList(rule, element))
            if (!_confirmedDebuffs.Contains($"{activeTargetId}_{debuffKey}")) return true;
        return false;
    }

    /// <summary>
    /// The wand to cast with: a listed one (the one in hand first). The pack is searched only
    /// when the Items list has no wand at all and WieldUnlistedWandWhenNoneListed is on, and
    /// then by object class only. It used to take any wand-named item from the pack.
    /// </summary>
    private int FindWandInItems()
    {
        int inHand = InHandWeaponId();
        int id = WeaponList.FindWand(_settings.ItemRules, i => _worldFilter[i], wo => wo.Id == inHand,
            _worldFilter.GetInventory(), _settings.WieldUnlistedWandWhenNoneListed, out bool unlisted);
        if (unlisted && _unlistedNoted.Add(id))
            _host.Log($"[EquipDiag] no wand in the Items list — using 0x{(uint)id:X8} '{_worldFilter[id]?.Name}' from the pack (WieldUnlistedWandWhenNoneListed)");
        return id;
    }

    private static bool IsWandObject(WorldObject wo) => WeaponList.IsWand(wo);

    /// <summary>War or Void Magic trained — the character can attack with a caster.
    /// Skills not read yet count as trained, like the other skill gates here.</summary>
    private bool CanAttackWithMagic =>
        _charSkills == null
        || _charSkills[AcSkillType.WarMagic].Training >= 2
        || _charSkills[AcSkillType.VoidMagic].Training >= 2;

    /// <summary>A weapon combat may fight with: anything the character has (in the world
    /// cache) but a caster when the character has no attack magic. Debuff casting picks its
    /// wand separately. An id missing from the cache is not a candidate: a weapon that was
    /// traded, destroyed or copied in with another character's profile used to win the
    /// Items list, shadow every real weapon below it, and send combat in "unarmed" from
    /// peace mode, where the server drops the attacks and every target got blacklisted.</summary>
    private bool IsUsableCombatWeapon(int id)
    {
        var wo = _worldFilter[id];
        if (wo == null) return false;
        return CanAttackWithMagic || !IsWandObject(wo);
    }

    private static List<string> BuildDebuffList(MonsterRule rule, string element)
    {
        var list = new List<string>();
        if (rule.Imperil)   list.Add("Imperil");
        if (rule.Vuln)      list.Add("Vuln:" + element);
        if (!string.IsNullOrEmpty(rule.ExVuln) && !rule.ExVuln.Equals("None", StringComparison.OrdinalIgnoreCase))
            list.Add("Vuln:" + rule.ExVuln);
        if (rule.Fester)    list.Add("Fester");
        if (rule.Yield)     list.Add("Yield");
        if (rule.Broadside) list.Add("Broadside");
        if (rule.GravityWell) list.Add("Gravity");
        if (!string.IsNullOrWhiteSpace(rule.CustomDebuffs))
            foreach (string part in rule.CustomDebuffs.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string nm = part.Trim();
                if (nm.Length > 0) list.Add("Custom:" + nm);
            }
        return list;
    }

    /// <summary>
    /// A debuff the player typed in: a full spell name casts as is; a base name casts the best
    /// tier the character knows ("Incantation of X", then X VII..I), like the built-in ones.
    /// </summary>
    private int FindBestCustomDebuffWithTier(string name, out int tier)
    {
        tier = 0;
        if (_spellManager == null || string.IsNullOrWhiteSpace(name)) return 0;
        name = name.Trim();
        int id = TrySpellByName(name);
        if (id != 0) return id;
        int maxTier = Math.Max(EffectiveMaxTier(AcSkillType.CreatureEnchantment), EffectiveMaxTier(AcSkillType.LifeMagic));
        if (maxTier >= 8) { id = TrySpellByName($"Incantation of {name}"); if (id != 0) { tier = 8; return id; } }
        for (int t = Math.Min(maxTier, 7); t >= 1; t--)
        {
            id = TrySpellByName($"{name} {GetRomanNumeral(t)}");
            if (id != 0) { tier = t; return id; }
        }
        return 0;
    }

    private int FindBestDebuffSpellWithTier(string debuffType, out int tier)
    {
        tier = 0;
        if (_spellManager == null) return 0;

        if (!DebuffSpells.TryGetValue(debuffType, out string[]? spellInfo) || spellInfo.Length < 2) return 0;

        string tieredBase = spellInfo[0];
        string loreName   = spellInfo[1];

        int maxTier = EffectiveMaxTier(AcSkillType.CreatureEnchantment);

        if (maxTier >= 8) { int id = TrySpellByName($"Incantation of {tieredBase}"); if (id != 0) { tier = 8; return id; } }
        if (maxTier >= 7 && !string.IsNullOrEmpty(loreName)) { int id = TrySpellByName(loreName); if (id != 0) { tier = 7; return id; } }

        for (int t = Math.Min(maxTier, 7); t >= 1; t--)
        {
            int id = TrySpellByName($"{tieredBase} {GetRomanNumeral(t)}");
            if (id != 0) { tier = t; return id; }
        }
        return 0;
    }

    private int FindBestVulnSpellWithTier(string element, out int tier)
    {
        tier = 0;
        if (_spellManager == null) return 0;
        if (!VulnSpells.TryGetValue(element, out string[]? vulnBases)) return 0;

        int maxTier = EffectiveMaxTier(AcSkillType.CreatureEnchantment);

        foreach (string baseName in vulnBases)
        {
            if (maxTier >= 8) { int id = TrySpellByName($"Incantation of {baseName}"); if (id != 0) { tier = 8; return id; } }
            for (int t = Math.Min(maxTier, 7); t >= 1; t--)
            {
                int id = TrySpellByName($"{baseName} {GetRomanNumeral(t)}");
                if (id != 0) { tier = t; return id; }
            }
        }
        return 0;
    }

    private int TrySpellByName(string name)
    {
        if (_spellManager == null) return 0;
        // Single source of truth shared with the buff path: unresolvable skip +
        // the engine's main-thread known-spell snapshot. Combat used to roll
        // its own check that trusted the mis-bound IsSpellKnown oracle (lies
        // "true" for unknown spells) and never saw _knownSpellIds, so an
        // unknown tier-8 (e.g. Incantation of Flame Bolt) resolved as castable
        // and never tiered down. Delegating fixes that for war/void/debuff/ring
        // resolution alike. RefreshKnownSpells() is pumped on the attack path.
        // Deterministic: name → id, REQUIRE the char knows it (authoritative
        // warm spellbook snapshot). No empirical unresolvable blacklist, no
        // mis-bound engine oracle — selection is purely predictive so nothing
        // self-poisons.
        if (!_spellManager.TryResolveKnownSpellId(name, out int id)) return 0;

        // Scarab gate DISABLED (EnablePredictiveComponentGate=false). Field-
        // tested 2026-05-17, pid 15372: it rejected every KNOWN Force Arc
        // tier ("NO-SCARAB" 2724/2723) while the char had the scarab — the
        // dat-decoded SpellComponentTable scarab name does not match the ACE
        // inventory item name. This is the documented predictive-components
        // failure on ACE (see rynthai_predictive_components.md): only the
        // server knows its real component rules. Selection therefore stays
        // known ∧ skill-window tier ∧ configured shape; the player keeps
        // components stocked. Code retained behind the flag for a future
        // retry IF a verified scarab-name↔inventory-name mapping exists.
        if (EnablePredictiveComponentGate)
        {
            EnsureInventoryNameCache();
            if (!ComponentDatabase.HasRequiredScarab(id, _invNamesLower))
            {
                if (_compSkipLogged.Add(id))
                    _host.Log($"[CombatCast] NO-SCARAB id={id} " +
                              $"'{SpellTableStub.GetById(id)?.Name}' — required scarab " +
                              $"not in inventory; tiering down.");
                return 0;
            }
        }
        return id;
    }

    private void EnsureInventoryNameCache()
    {
        if ((DateTime.Now - _invNamesBuiltAt).TotalMilliseconds < InvNameCacheMs) return;
        _invNamesBuiltAt = DateTime.Now;
        _invNamesLower.Clear();
        _compSkipLogged.Clear();
        foreach (var wo in _worldFilter.GetInventory())
            if (!string.IsNullOrEmpty(wo.Name))
                _invNamesLower.Add(wo.Name.ToLowerInvariant());
    }

    private int CountMonstersInRange(double rangeYards)
    {
        if (_playerId == 0) return 0;
        int pid = (int)_playerId;
        int count = 0;
        foreach (var wo in _worldFilter.GetLandscape())
        {
            if (wo.ObjectClass != AcObjectClass.Monster) continue;
            float hp = _worldFilter.GetHealthRatio(wo.Id);
            if (hp == 0f || hp < 0f) continue;
            if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)wo.Id)) continue;
            if (_worldFilter.Distance(pid, wo.Id) <= rangeYards)
                count++;
        }
        return count;
    }

    private int FindBestShapedSpell(string element, MonsterRule? rule) =>
        FindBestShapedSpell(element, rule, out _);

    private int FindBestShapedSpell(string element, MonsterRule? rule, out bool isRing) =>
        FindBestShapedSpell(element, rule, out isRing, out _);

    /// <summary>
    /// The base shape a rule asks for when no ring is due: Arc, then Streak, then Blast, then
    /// Bolt (the first one on wins; Blast beats Bolt so turning Blast on next to the default Bolt
    /// does something). A ring-only rule casts Bolt between rings. -1 = no shape on at all.
    /// </summary>
    internal static int PickBaseShape(MonsterRule? rule)
    {
        if (rule == null) return ShapeBolt;
        if (rule.UseArc)    return ShapeArc;
        if (rule.UseStreak) return ShapeStreak;
        if (rule.UseBlast)  return ShapeBlast;
        if (rule.UseBolt)   return ShapeBolt;
        return rule.UseRing ? ShapeBolt : -1;
    }

    /// <summary>
    /// The base shape when Blast is conditional (Blast Range on): Blast is then an override like
    /// the ring, so the base is the rule's other shape (Arc, Streak, Bolt), else Bolt.
    /// </summary>
    internal static int PickBaseShapeWithoutBlast(MonsterRule? rule)
    {
        if (rule == null) return ShapeBolt;
        if (rule.UseArc)    return ShapeArc;
        if (rule.UseStreak) return ShapeStreak;
        return ShapeBolt;
    }

    // A blast is 3 projectiles over a 90 degree fan (spell table: num_Projectiles 3,
    // spread_Angle 90), centred on the target we face before casting. Counted with a margin.
    internal const double BlastFanHalfAngle = 55.0;

    /// <summary>Monsters (the target included) within <paramref name="rangeYards"/> and inside
    /// the blast's fan ahead (we face the target to cast). From the combat scan: LOS-clear and
    /// attackable.</summary>
    private int CountMonstersInBlastFan(double rangeYards)
    {
        int count = 0;
        foreach (var c in _scannedTargets)
            if (c.Distance <= rangeYards && c.Angle <= BlastFanHalfAngle && !_recentlyKilled.ContainsKey(c.Id))
                count++;
        return count;
    }

    internal static string ShapeName(int shape) => shape switch
    {
        ShapeArc => "Arc", ShapeRing => "Ring", ShapeStreak => "Streak", ShapeBolt => "Bolt", ShapeBlast => "Blast", _ => "?",
    };

    /// <summary>
    /// The spell base name for <paramref name="element"/> in <paramref name="shape"/> and, for war
    /// streaks, bolts and blasts, the lore name of its tier 7. Void looks in the void rows (any
    /// element it has no row for casts Nether); war casts Fire for an element it has no row for
    /// (<paramref name="forcedWar"/> is then true: the caller casts with War Magic).
    /// </summary>
    internal static bool TryGetShapeBase(string element, int shape, bool useVoid,
        out string baseName, out string? tier7Lore, out bool forcedWar)
    {
        baseName = ""; tier7Lore = null; forcedWar = false;
        var shapes = useVoid ? VoidSpellShapes : SpellShapes;
        if (!shapes.TryGetValue(element, out string[]? elementShapes))
        {
            if (useVoid)
            {
                // Void Magic only damages with Nether — fall back to Nether shapes for
                // ANY element not in VoidSpellShapes (Cold/Lightning/Acid/Blade/Pierce/
                // Bludgeon/Slash). Previously this fell through to War Magic Fire, which
                // forced War on Void-only casters whose War skill was untrained.
                if (!VoidSpellShapes.TryGetValue("Nether", out elementShapes)) return false;
            }
            else
            {
                if (!SpellShapes.TryGetValue("Fire", out elementShapes)) return false;
                forcedWar = true;
            }
        }
        if (shape < 0) return false;
        if (shape >= elementShapes.Length) shape = elementShapes.Length - 1;
        baseName = elementShapes[shape];

        // Streak / Bolt / Blast war lines have a lore-named tier-7 (no "{base} VII").
        // Arc uses Roman "VII" (exists); Void has no lore tier-7 in this table.
        if (!useVoid && WarTier7Lore.TryGetValue(forcedWar ? "Fire" : element, out string[]? w7))
        {
            if (shape == ShapeStreak)     tier7Lore = w7[0];
            else if (shape == ShapeBolt)  tier7Lore = w7[1];
            else if (shape == ShapeBlast) tier7Lore = w7[2];
        }
        return true;
    }

    internal int FindBestShapedSpell(string element, MonsterRule? rule, out bool isRing, out int castShape)
        => FindBestShapedSpell(element, rule, out isRing, out castShape, arcTargetId: 0);

    /// <summary>
    /// Which shape this cast is, before the spell is looked up. In order:
    ///   1. the rule's base shape: Arc, Streak, Blast, Bolt (<see cref="PickBaseShape"/>);
    ///   2. Blast override (Blast Range on): Blast when its fan count is met, else the rule's
    ///      other shape (<paramref name="blastConditional"/>, <paramref name="blastFanMet"/>);
    ///   3. Ring override (Ring Range on): Ring when its count is met (<paramref name="ringDue"/>);
    ///   4. Arc when clear: an Arc whose flight path to the target is blocked (a wall, or a
    ///      ceiling the arc rises into) gives way to the rule's other shape: Streak, then Blast
    ///      (when Blast isn't conditional), then Bolt. An Arc-only rule casts Bolt, as a ring-only
    ///      rule does between rings.
    /// <paramref name="arcBlocked"/> is asked only in step 4, so a ring or a non-arc rule costs
    /// no ray test; null (no test, or it can't tell: raycasting off or not ready) casts the Arc
    /// as before. <paramref name="arcGaveWay"/> says step 4 replaced the Arc.
    /// </summary>
    internal static int ChooseCastShape(MonsterRule? rule, bool blastConditional, bool blastFanMet, bool ringDue,
        Func<bool?>? arcBlocked, out bool arcGaveWay)
    {
        arcGaveWay = false;
        int shape = PickBaseShape(rule);
        if (shape < 0) return -1;
        if (blastConditional)
            shape = blastFanMet ? ShapeBlast : PickBaseShapeWithoutBlast(rule);
        if (ringDue)
            return ShapeRing;
        if (shape == ShapeArc && arcBlocked != null && arcBlocked() == true)
        {
            arcGaveWay = true;
            shape = ArcFallbackShape(rule, blastConditional);
        }
        return shape;
    }

    /// <summary>The shape a blocked Arc gives way to: the rule's other shape (Streak, then Blast
    /// unless Blast is conditional, then Bolt); Bolt for an Arc-only rule.</summary>
    internal static int ArcFallbackShape(MonsterRule? rule, bool blastConditional)
    {
        if (rule == null) return ShapeBolt;
        if (rule.UseStreak) return ShapeStreak;
        if (rule.UseBlast && !blastConditional) return ShapeBlast;
        return ShapeBolt;
    }

    // ── Arc when clear ───────────────────────────────────────────────────────────
    // An Arc is cast only when the arc ACE flies (fixed 40 m/s horizontal speed, from the
    // caster's head, gravity 9.8; TargetingFSM.IsMagicArcBlocked) reaches the target: target
    // selection in magic mode tests only the straight line, so an arc into a low ceiling used to
    // be cast anyway. Tested per cast (not per frame), and the verdict is kept 300 ms per target.
    internal const double ArcVerdictCacheMs = 300;
    private int _arcVerdictTarget;
    private DateTime _arcVerdictAt = DateTime.MinValue;
    private bool? _arcVerdictBlocked;
    private TargetingFSM.LosDetail _arcVerdictDetail;
    private bool _lastShapeArcGaveWay;
    private int _arcLogTarget;
    private string _arcLogState = "";
    private DateTime _arcLogAt = DateTime.MinValue;
    private const double ArcLogRepeatMs = 10000;
    private readonly HashSet<string> _arcRuleLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tests: the arc verdict per target instead of the ray test (null = can't tell).</summary>
    internal Func<int, bool?>? ArcBlockedOverride { get; set; }
    /// <summary>How many arc verdicts were worked out (not served from the 300 ms cache).</summary>
    internal int ArcVerdictComputeCount { get; private set; }

    /// <summary>Is the arc to <paramref name="targetId"/> blocked? null = can't tell (raycasting
    /// off or not ready): the arc is cast as before.</summary>
    internal bool? IsArcBlockedTo(int targetId)
    {
        if (targetId == 0) return null;
        DateTime now = DateTime.UtcNow;
        if (targetId == _arcVerdictTarget && (now - _arcVerdictAt).TotalMilliseconds < ArcVerdictCacheMs)
            return _arcVerdictBlocked;

        bool? blocked = null;
        TargetingFSM.LosDetail det = default;
        if (ArcBlockedOverride != null)
            blocked = ArcBlockedOverride(targetId);
        else if (_settings.EnableRaycasting && RaycastInitialized && _raycastSystem?.TargetingFSM != null)
            blocked = _raycastSystem.TargetingFSM.IsMagicArcBlocked(_host, (uint)targetId, out det);

        ArcVerdictComputeCount++;
        _arcVerdictTarget = targetId;
        _arcVerdictAt = now;
        _arcVerdictBlocked = blocked;
        _arcVerdictDetail = det;
        return blocked;
    }

    /// <summary>Drops the cached arc verdict: the next cast tests the arc again.</summary>
    internal void ForgetArcVerdict() => _arcVerdictAt = DateTime.MinValue;

    /// <summary>"ceiling 2.1 m out", "wall in the straight line": why an arc verdict is blocked.</summary>
    internal static string DescribeArcBlock(in TargetingFSM.LosDetail d)
    {
        if (d.LineBlocked) return "wall in the straight line";
        if (d.Arc.OutOfReach) return "out of reach";
        if (d.Arc.Blocked)
            return d.Arc.HitZ > 0.3f
                ? $"{(d.Dungeon ? "ceiling" : "overhead")} {d.Arc.HitAlong:0.0} m out"
                : $"obstacle {d.Arc.HitAlong:0.0} m out";
        return "blocked";
    }

    /// <summary>Logs the Arc-when-clear choice: once per rule how it behaves, then per target
    /// when the verdict changes (and at most every 10 s while it stays blocked).</summary>
    private void LogArcChoice(MonsterRule? rule, WorldObject target, int castShape)
    {
        if (rule == null || !rule.UseArc) return;
        if (_arcRuleLogged.Add(rule.Name ?? ""))
        {
            string other = ShapeName(ArcFallbackShape(rule, rule.UseBlast && _settings.BlastRange > 0));
            _host.Log($"[CombatCast] rule '{rule.Name}': Arc when its flight path is clear, else {other}" +
                      (_settings.EnableRaycasting ? "" : " (raycasting off: arcs are always cast)"));
        }
        if (castShape == ShapeRing) return;   // the ring override won: no arc question this cast
        string state = _lastShapeArcGaveWay
            ? $"arc blocked ({(ArcBlockedOverride != null ? "test" : DescribeArcBlock(in _arcVerdictDetail))}): {(castShape >= 0 ? ShapeName(castShape) : "nothing")} instead"
            : "arc clear";
        DateTime now = DateTime.UtcNow;
        bool changed = target.Id != _arcLogTarget || state != _arcLogState;
        if (!changed && (!_lastShapeArcGaveWay || (now - _arcLogAt).TotalMilliseconds < ArcLogRepeatMs)) return;
        bool firstClear = !_lastShapeArcGaveWay && (target.Id != _arcLogTarget || _arcLogState.Length == 0);
        _arcLogTarget = target.Id;
        _arcLogState = state;
        _arcLogAt = now;
        if (firstClear) return;   // a clear arc at a new target is the normal case: not logged
        _host.Log($"[CombatCast] {state} -> '{target.Name}'");
    }

    /// <param name="arcTargetId">The target this cast goes to. An Arc is then cast only when its
    /// flight path there is clear (see <see cref="ChooseCastShape"/>). 0 = no test (element
    /// probes, tests of the plain shape choice).</param>
    internal int FindBestShapedSpell(string element, MonsterRule? rule, out bool isRing, out int castShape, int arcTargetId)
    {
        isRing = false;
        castShape = -1;
        if (arcTargetId != 0) _lastShapeArcGaveWay = false;
        if (_spellManager == null) return 0;

        bool useVoid = element.Equals("Nether", StringComparison.OrdinalIgnoreCase);
        bool warTrained  = _charSkills == null || _charSkills[AcSkillType.WarMagic].Training >= 2;
        bool voidTrained = _charSkills == null || _charSkills[AcSkillType.VoidMagic].Training >= 2;
        if (!warTrained && !voidTrained) return 0; // no attack magic — used to fall through to War anyway

        if (useVoid && !voidTrained && warTrained)  useVoid = false;
        if (!useVoid && !warTrained && voidTrained) useVoid = true;

        AcSkillType skill = useVoid ? AcSkillType.VoidMagic : AcSkillType.WarMagic;

        if (PickBaseShape(rule) < 0) return 0; // no shape enabled at all

        // Blast override (Blast Range on): a blast only when enough monsters are in its fan.
        bool blastConditional = rule != null && rule.UseBlast && _settings.BlastRange > 0;
        bool blastFanMet = blastConditional
            && CountMonstersInBlastFan(_settings.BlastRange) >= Math.Max(1, _settings.MinBlastTargets);

        // Ring override: when UseRing is enabled, check if enough monsters are within
        // ring range. If so, upgrade to ring; otherwise keep the base shape (arc/streak/blast/bolt).
        bool ringDue = rule != null && rule.UseRing && _settings.RingRange > 0
            && CountMonstersInRange(_settings.RingRange) >= Math.Max(1, _settings.MinRingTargets);

        int shapeIdx = ChooseCastShape(rule, blastConditional, blastFanMet, ringDue,
            arcTargetId != 0 ? () => IsArcBlockedTo(arcTargetId) : null, out bool arcGaveWay);
        if (arcTargetId != 0) _lastShapeArcGaveWay = arcGaveWay;

        if (!TryGetShapeBase(element, shapeIdx, useVoid, out string baseName, out string? t7, out bool forcedWar)) return 0;
        if (forcedWar) skill = AcSkillType.WarMagic;

        if (shapeIdx == ShapeRing)
        {
            int ringId = FindBestRingSpell(element, skill);
            if (ringId != 0) { isRing = true; castShape = ShapeRing; return ringId; }
        }

        // Strict TYPE adherence: cast ONLY the shape configured in the
        // Monsters tab (Arc/Bolt/Streak/Blast) — plus the ring override handled
        // above. No cross-shape fallthrough; that was casting Streak when
        // Arc was configured but its tiers weren't resolvable.
        // One exception: Blast falls back to Bolt of the same element when the
        // character knows no blast at all — retail has no blast I or II, so a low
        // character set to Blast would otherwise never attack.
        int id = FindBestOffensiveSpellId(baseName, skill, t7);
        castShape = id != 0 ? shapeIdx : -1;
        // A blocked Arc whose other shape isn't known casts the same element's Bolt (never the arc).
        if (id == 0 && arcGaveWay && shapeIdx == ShapeStreak
            && TryGetShapeBase(element, ShapeBolt, useVoid, out string arcBoltBase, out string? arcBoltT7, out _))
        {
            id = FindBestOffensiveSpellId(arcBoltBase, skill, arcBoltT7);
            if (id != 0) castShape = ShapeBolt;
        }
        if (id == 0 && shapeIdx == ShapeBlast
            && TryGetShapeBase(element, ShapeBolt, useVoid, out string boltBase, out string? boltT7, out _))
        {
            id = FindBestOffensiveSpellId(boltBase, skill, boltT7);
            if (id != 0)
            {
                castShape = ShapeBolt;
                if (_blastFallbackLogged.Add(element))
                    _host.Log($"[CombatCast] no {baseName} known (blasts start at III): casting {boltBase} instead");
            }
        }
        return id;
    }

    private readonly HashSet<string> _blastFallbackLogged = new(StringComparer.OrdinalIgnoreCase);

    private int FindBestRingSpell(string element, AcSkillType skill)
    {
        // Void Magic doesn't have lore-named rings (Cassius'/Halo/etc. are all War
        // Magic spells). Returning 0 here makes FindBestShapedSpell fall through to
        // FindBestOffensiveSpellId(elementShapes[1], skill) where elementShapes comes
        // from VoidSpellShapes — e.g. "Nether Ring" or "Corrosion Ring" — and the
        // Void caster gets the right Void ring tier instead of an unknown War spell.
        if (skill == AcSkillType.VoidMagic)
            return 0;

        if (!RingLoreNames.TryGetValue(element, out string[]? loreNames) || loreNames.Length < 2) return 0;

        int maxTier = EffectiveMaxTier(skill);

        if (maxTier >= 7)
        {
            int id = TrySpellByName(loreNames[1]); if (id != 0) return id;
            id = TrySpellByName(loreNames[1].Replace("'", "\u2019")); if (id != 0) return id;
            id = TrySpellByName(loreNames[1].Replace("'", "`")); if (id != 0) return id;
        }
        if (maxTier >= 6)
        {
            int id = TrySpellByName(loreNames[0]); if (id != 0) return id;
            id = TrySpellByName(loreNames[0].Replace("'", "\u2019")); if (id != 0) return id;
            id = TrySpellByName(loreNames[0].Replace("'", "`")); if (id != 0) return id;
        }
        if (maxTier >= 8)
        {
            int id = TrySpellByName($"Incantation of {loreNames[0]}"); if (id != 0) return id;
        }

        if (SpellShapes.TryGetValue(element, out string[]? genericRingBases) && genericRingBases.Length > 1)
        {
            string ringBase = genericRingBases[1];
            for (int t = Math.Min(maxTier, 5); t >= 1; t--)
            {
                int id = TrySpellByName($"{ringBase} {GetRomanNumeral(t)}"); if (id != 0) return id;
            }
        }

        // Reached from every element probe (CanCastElement inside GetPreferredElement) as well
        // as the cast itself, several times per attack cycle while a pack stands in ring range:
        // say it (chat + file log) once per element per NoSpellChatIntervalMs.
        DateTime nowRing = DateTime.Now;
        if (!_ringMissingChatAt.TryGetValue(element, out DateTime lastRing)
            || (nowRing - lastRing).TotalMilliseconds >= NoSpellChatIntervalMs)
        {
            _ringMissingChatAt[element] = nowRing;
            _host.WriteToChat($"[RynthAi] Ring spell not found for {element} (tried: {loreNames[1]}, {loreNames[0]})", 2);
            _host.Log($"[CombatCast] ring spell not found for {element} (tried: {loreNames[1]}, {loreNames[0]})");
        }
        return 0;
    }

    private int FindBestOffensiveSpellId(string baseName, AcSkillType skill, string? tier7Lore = null)
    {
        if (_spellManager == null) return 0;
        int maxTier = EffectiveMaxTier(skill);

        if (maxTier >= 8)
        {
            int id = TrySpellByName($"Incantation of {baseName}"); if (id != 0) return id;
            id = TrySpellByName(baseName + " VIII"); if (id != 0) return id;
        }

        for (int tier = Math.Min(maxTier, 7); tier >= 1; tier--)
        {
            // Streak/Bolt war lines have no "{base} VII" — tier-7 is a lore
            // name (e.g. Force Streak VII = "Outlander's Insolence"). Try it
            // at the tier-7 step so the highest tier still wins; Arc falls
            // through to the Roman "{base} VII" below (which exists).
            if (tier == 7 && !string.IsNullOrEmpty(tier7Lore))
            {
                int loreId = TrySpellByName(tier7Lore); if (loreId != 0) return loreId;
            }
            int id = TrySpellByName($"{baseName} {GetRomanNumeral(tier)}"); if (id != 0) return id;
        }
        return 0;
    }

    /// <summary>
    /// Combat tier ceiling. When the authoritative known-spell snapshot is
    /// cold the resolver can't trust IsSpellKnown (it lies "true" for unknown
    /// spells), so refuse to blind-pick the tier-8 Incantation — clamp to 7
    /// until the snapshot warms. A char who really knows L8 loses Incantations
    /// only during the brief cold window; one who doesn't no longer spams an
    /// uncastable L8 every fight.
    /// </summary>
    private int EffectiveMaxTier(AcSkillType skill)
    {
        if (_spellManager == null) return 0;
        int t = _spellManager.GetHighestSpellTier(skill);
        if (t > 7 && !_spellManager.IsKnownSnapshotWarm) t = 7;
        return t;
    }

    private static string GetRomanNumeral(int tier) => tier switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV",
        5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII",
        _ => "I"
    };

    public void Dispose() => _raycastSystem?.Dispose();
}
