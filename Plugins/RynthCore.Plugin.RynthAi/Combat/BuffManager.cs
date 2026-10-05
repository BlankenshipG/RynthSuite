using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

public class BuffManager : IDisposable
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly SpellManager _spellManager;
    private readonly PlayerVitalsCache _vitals;

    private CharacterSkills? _charSkills;
    private WorldObjectCache? _worldObjectCache;

    private DateTime _lastCastAttempt = DateTime.MinValue;
    /// <summary>When buffing last sent a cast (buff, vital or refill). The buffing-coma watchdog's sign of progress.</summary>
    public DateTime LastCastAttemptAt => _lastCastAttempt;
    // Throttles ChangeCombatMode(Magic) from EnsureMagicMode's "wand wielded but
    // mode != Magic" branch. Without this, when the server rejects/silently-drops
    // mode flips (e.g. wedged-target combat state), the branch fires every tick
    // (~2.5Hz) and spams AC with thousands of stance-change packets, eventually
    // crashing the client. 2026-05-25 AcActionTrace caught 256 consecutive
    // ChangeCombatMode(Magic) calls at ~400ms intervals immediately before pid
    // 39584 died.
    //
    // 2026-05-25 update: a fixed 1s cooldown only paced the spam at 1Hz (pid
    // 16656 still crashed after ~48min on engine-side AV at 0x04F24E0 ref 0x18).
    // Switched to exponential backoff that doubles wait on each consecutive
    // failed flip: 1s → 2s → 4s → 8s → 16s → 30s cap. The bot KEEPS TRYING
    // forever (per user requirement — bot must continue) but at a rate that
    // decays as failures accumulate. Counter resets to 0 as soon as
    // EnsureMagicMode is entered with mode == Magic (i.e. any successful flip
    // re-enables fast cadence for the next problem). Worst-case wedge load:
    // ~100 calls over 48min instead of ~2,880.
    private DateTime _lastBuffStanceAttempt = DateTime.MinValue;
    private int _buffStanceConsecutiveFails = 0;
    private bool _isForceRebuffing = false;
    // Set with _isForceRebuffing when the pass is the automatic batch (one buff fell
    // under RebuffSecondsRemaining), not the Force Rebuff button. The batch only tops
    // off buffs under RebuffTopOffSecondsRemaining; Force Rebuff recasts everything.
    private bool _isAutoBatchRebuff = false;
    private int _lastLivePlayerEnchantCount = -1;   // previous live registry read; -1 = none yet
    private int _pendingSpellId = 0;
    private Action<string>? _onCastResolved;

    // ARMOR/ITEM enchants only: a cast that gets ZERO chat back (unknown tier /
    // no comps — AC silently drops it) never resolves, so pending never clears
    // and the cycle wedges. Last-resort bound: abandon after this long. Self-
    // buffs do NOT use this path — they confirm via the live registry below.
    private const double NoChatResolveTimeoutMs = 5000;

    // SELF-BUFFS confirm via the live player-enchantment registry, not chat
    // (ReadPlayerEnchantments returns player buffs in real time; item enchants
    // are NOT in it). Wait SelfBuffConfirmMs for the cast to settle server-side,
    // then poll the registry; if still absent by SelfBuffGiveUpMs the tier
    // didn't take (drop a tier only when the known-spell snapshot is cold).
    private const double SelfBuffConfirmMs = 600;
    private const double SelfBuffGiveUpMs  = 2500;
    private DateTime _lastSelfBuffPollAt = DateTime.MinValue;

    // Wired by RynthAiPlugin. Invoked the instant a buff cast resolves in chat
    // (success / fizzle / hard-fail / too-busy) so the owner can clear the
    // leaked busy-count immediately instead of waiting out the 10s watchdog —
    // that post-cast stall is the entire "slow buffing" symptom.
    public void SetCastResolvedCallback(Action<string> cb) => _onCastResolved = cb;

    private class RamTimerInfo
    {
        public DateTime Expiration;
        public int SpellLevel;
        public string SpellName = "";
        // True when this is a permanent player enchantment (server reports no
        // expiry). It is a presence-only marker re-derived from the live
        // registry on every RefreshFromLiveMemory — NOT a synthetic timer and
        // NEVER persisted (a relog after death/dispel must not resurrect it).
        public bool IsPermanent;
    }

    // "Never expires" sentinel for permanent player enchantments. Far enough
    // from DateTime.MaxValue that IsBuffActive's Expiration.AddSeconds(-rebufferSec)
    // (rebufferSec ≤ 1800) can't underflow.
    private static readonly DateTime PermanentSentinel = DateTime.MaxValue.AddDays(-1);

    // Permanent-enchant families already logged this session (once-per-family
    // diagnostic so the 30s refresh doesn't spam the log).
    private readonly HashSet<int> _loggedPermanentFamilies = new();

    /// <summary>
    /// Buff timer for an enchantment on a specific equipped item (armor/weapon).
    /// </summary>
    private class ItemBuffTimerInfo
    {
        public uint ObjectId;
        public string ObjectName = "";
        public DateTime Expiration;
        public int SpellLevel;
        public string SpellName = "";
    }

    /// <summary>
    /// Timer for an item spell (armor bane / Impenetrability) cast by this plugin.
    /// Recorded immediately on cast — independent of chat parsing and enchantment hooks.
    /// </summary>
    private class ItemSpellRecord
    {
        public DateTime CastAt;
        public DateTime ExpiresAt;
        public string SpellName = "";
        public int SpellLevel;
    }

    private readonly Dictionary<int, RamTimerInfo> _ramBuffTimers = new();
    // Highest tier we've actually seen LAND for a family. Incantations (nominal
    // tier 8) land at a skill-capped lower tier; without this the upgrade rule
    // in IsBuffActive recasts forever. Updated whenever a family's timer is
    // recorded (live refresh or post-cast). Max-of-observed so it converges to
    // the character's true ceiling after the first cast.
    private readonly Dictionary<int, int> _familyAchievedTier = new();
    // Every live player enchantment per family from the last RefreshFromLiveMemory
    // (_ramBuffTimers keeps only the strongest). Lets a family read "on" when the
    // strongest entry is running out but another one at the tier we'd cast is under it:
    // a buff bot's level 8 expiring over our own fresh VI (2026-10-05). Permanent marks an
    // item-granted entry (no expiry), which CastBuffsOverItemBuffs leaves out.
    private readonly Dictionary<int, List<(int Level, DateTime Expiration, bool Permanent)>> _familyLiveEntries = new();
    // Highest tier this session has CAST per family (nominal level). The achieved-tier cap
    // applies only once the target tier has been tried, so a family deliberately cast low
    // first (the tier-7 creature bootstrap before 8s) still upgrades.
    private readonly Dictionary<int, int> _familyAttemptedTier = new();
    /// <summary>Keyed by (objectId, spellFamily) packed as long.</summary>
    private readonly Dictionary<long, ItemBuffTimerInfo> _itemBuffTimers = new();
    /// <summary>Keyed by spell family. Tracks item-enchantment casts (armor banes, Impenetrability) directly.</summary>
    private readonly Dictionary<int, ItemSpellRecord> _itemSpellTimers = new();
    private string _buffTimerPath = "";

    // === Item-enchantment duration experiment (BuffManager_Review.md §headline #3) ===
    // Toggled via `/ra bufftest`. Stays ON until toggled OFF — captures every cast.
    public bool EnableCastRegistryDiagnostic = false;
    private readonly Queue<CastDiagnostic> _pendingDiagnostics = new();

    // Force Rebuff cycle tracking — when set, IsBuffActive consults this instead
    // of the live timer dicts so FR re-casts every spell to align all timers.
    private readonly HashSet<int> _forceRebuffCastFamilies = new();

    // Per-family diagnostic throttle — last reason string we logged. Suppresses
    // dup spam when IsBuffActive is polled every tick by NeedsAnyBuff + CheckAndCastSelfBuffs.
    private readonly Dictionary<int, string> _lastArmorRecastReason = new();

    // Per-family failure cooldown. A buff the server deterministically rejects
    // (no components, must specify a target, etc.) must NOT be re-picked every
    // buff cycle — sustained re-cast → sustained AC AddTextToScroll re-entry →
    // AC access-violation (the same AV class the cast-gate work targeted).
    // Hard rejections park that family for BuffFailCooldownSec; soft random
    // fizzles are NOT cooled (they should retry promptly).
    private readonly Dictionary<int, DateTime> _buffFailCooldownUntil = new();
    private const double BuffFailCooldownSec = 120.0;

    // Per-family SILENT no-show counter. Distinct from _buffFailCooldownUntil
    // (which catches chat-explicit hard rejects). This catches the /god case:
    // skill is Trained → IsSkillUsable says yes → BuildDynamicBuffList includes
    // the buff → cast is issued → AC silently does nothing because the spell
    // isn't in the spellbook → no chat, no enchantment-added event, no _ramBuffTimers
    // entry → IsBuffActive returns false → loop forever. The existing no-show
    // branch in TickBuffing only blacklists when IsKnownSnapshotWarm == false;
    // with /god the snapshot stays warm (knowledge bits exist) so retry never
    // exits. This counter bounds the retry: after SilentNoShowThreshold misses
    // we park the family in _buffFailCooldownUntil for SilentNoShowCooldown so
    // CheckAndCastSelfBuffs skips it on subsequent cycles. Counter + cooldown
    // are cleared in OnEnchantmentAdded when the family actually lands later
    // (character learns the spell, components arrive, server-side fix, etc.).
    private readonly Dictionary<int, int> _silentNoShowCounts = new();
    private const int SilentNoShowThreshold = 2;
    private static readonly TimeSpan SilentNoShowCooldown = TimeSpan.FromMinutes(30);

    // Family → the spell id we last ISSUED a buff cast for. Lets the post-batch
    // audit re-check exactly what it tried instead of re-resolving the tier.
    private readonly Dictionary<int, int> _lastCastSpellIdByFamily = new();

    // Per-family "cast landed but the buff STILL reads inactive" strikes.
    // Distinct from both existing park mechanisms: _buffFailCooldownUntil catches
    // chat-explicit hard rejects, _silentNoShowCounts catches casts that produce
    // no confirmation at all. This catches the nastier third case — AC confirms
    // the cast, the enchantment really is on the character, and IsBuffActive
    // still returns false. Nothing in the old code bounded that: the family
    // re-triggers the auto-batch on the very next tick, and since a single buff
    // below threshold escalates to a FULL rebuff, one such family makes the bot
    // recast all ~65 spells back-to-back forever. Two consecutive unsatisfied
    // batches park the family exactly like a no-show does — time-boxed and
    // self-recovering, so a genuine transient can't shelve a buff permanently.
    private readonly Dictionary<int, int> _unsatisfiedStrikes = new();
    private const int UnsatisfiedStrikeThreshold = 2;

    // When each family's cast was issued, and what its stored expiry was at that
    // moment. The audit needs both to avoid punishing a perfectly healthy buff:
    // a refresh takes time to reach the registry, so a family cast near the end
    // of a batch can still read its OLD timer when the batch ends. Judging it
    // there produced a false strike on 'Magic Item Tinkering Expertise Self VI'
    // (observed 2026-09-02 22:34) that read active again a minute later. A family
    // is only judged once AuditSettleMs has passed since its cast, and only
    // counts as unsatisfied if its expiry never moved forward. A genuinely stuck
    // family recurs in every batch, so it still accumulates strikes across them;
    // a healthy one is simply skipped this round.
    // ── "You're too busy!" backoff ───────────────────────────────────────────
    // A refusal means AC is mid-gesture and would not accept the cast. The PARK
    // path clears _pendingSpellId and says it will "re-issue when the cast gate
    // reopens", but nothing actually waits for the gate — the next tick re-issues
    // under SpellCastIntervalMs (400ms). A cast gesture runs ~2s, so under load
    // that is ~2 refusals/second for as long as the condition lasts: 675 of them
    // in two episodes on 2026-09-03, with CanCastNow reporting clear and
    // BusyCount 0 throughout (so neither existing gate saw it).
    //
    // Every refusal also pushes a chat line through AC's text pipeline, which is
    // the documented AddTextToScroll re-entry AV pressure this file already
    // guards against elsewhere — so a refusal storm is not merely wasted work.
    //
    // The refusal is authoritative: AC just told us it is busy. Back off with an
    // exponential delay and reset the moment a cast is accepted, so a transient
    // busy window costs a few attempts instead of thousands.
    // ── Server-paced cast serialization (UseDone / GameEvent 0x01C7) ────────
    // The server sends UseDone when it FINISHES an action — completed, or refused
    // with an error (0x1D = YoureTooBusy). It is the authoritative ACE
    // Player.IsBusy lifecycle signal, and the engine already exposes a monotonic
    // counter for it. CombatManager has serialized its casts on this since it was
    // added; BuffManager never did, and paced instead on a flat
    // SpellCastIntervalMs (400ms) measured from cast ISSUE.
    //
    // That interval is shorter than a cast actually takes. Measured over 612
    // paired buff casts on 2026-09-03, refusal rate by gap since the previous
    // issue: 400-450ms → 54%, 450-600ms → 100%, 600-900ms → 33%, >900ms → 9.5%,
    // with "You cast X" arriving at a median 652ms and "too busy" at 307ms. So
    // the next cast was routinely issued while the previous one was still
    // resolving. The local CanCastNow gate cannot catch this: it reads
    // CMotionInterp's pending-motions head, which empties when the LOCAL
    // animation ends, well before the server is done.
    //
    // Waiting for UseDone is self-tuning and needs no per-spell duration table —
    // a slower tier-8 cast simply produces a later UseDone. Bounded by a timeout
    // so a dropped event can't wedge buffing, and inert (never waits) on an
    // engine build without UseDone observation.
    //
    // 2026-10-05 (Drakkon, DreamWeave): "finished" used to be ANY new UseDone, and the refusal
    // of the next cast is a UseDone too (err 0x1D), so under a refusal storm the wait cleared at
    // once. The wait now belongs to the cast: see CastTracker.AwaitingServer.
    private const double CastResolutionTimeoutMs = 2500; // from the send; later when the incantation is late
    // Sends, incantations, results and the server's UseDones for the casts this class sends.
    private readonly CastTracker _cast;

    /// <summary>Clock for the cast-safety timing (give-up, server wait, held-cast nudge, stance
    /// settle). The host tests replace it; everything else here reads DateTime.Now.</summary>
    internal Func<DateTime> Clock { get => _cast.Clock; set => _cast.Clock = value; }
    private DateTime Now => _cast.Now;

    // Held-cast nudge (2026-10-05): a cast the server holds open (incantation, no result) is
    // only let go by a movement packet; StopCompletely from a standing character may send none.
    // Once per held cast: autorun on and straight off again, then no cast for this long.
    private const double NudgeHoldMs = 2000;
    /// <summary>Movement nudges sent for held casts this session (/ra status, tests).</summary>
    internal int CastNudges { get; private set; }

    private int _busyRefusalStreak;
    private DateTime _busyBackoffUntil = DateTime.MinValue;
    private const double BusyBackoffBaseMs = 500;
    private const double BusyBackoffMaxMs  = 8000;
    // During a too-busy backoff a heal may still retry, but no faster than this.
    private const double BusyHealRetryMs   = 1500;
    // Refusals in a row (with backoff, about 3.5 s or more) before StopCompletely: longer than any real gesture.
    private const int BusyStopAtStreak     = 4;

    // "Not enough mana" refusal (see OnChatWindowText): the refused family rests
    // briefly and buffing pauses; neither is a silent-no-show strike.
    private DateTime _lowManaPauseUntil = DateTime.MinValue;
    private const double LowManaBuffPauseMs = 5000;
    private const double LowManaHealRestSec = 2;
    private const double LowManaFamilyRestSec = 15;

    private readonly Dictionary<int, DateTime> _castIssuedAtByFamily = new();
    private readonly Dictionary<int, DateTime> _expiryAtCastByFamily = new();
    private const double AuditSettleMs = 15_000.0;

    private sealed class RegistrySnapshot
    {
        public uint OwnerId;
        public string OwnerName = "";
        public bool IsPlayer;
        public uint[] SpellIds = Array.Empty<uint>();
        public double[] ExpiryTimes = Array.Empty<double>();
        public int Count;
        public double ServerTime;
    }

    private sealed class CastDiagnostic
    {
        public DateTime CastedAt;
        public string SpellName = "";
        public int SpellId;
        public int SpellFamily;
        public List<RegistrySnapshot> PreSnapshots = new();
    }

    private bool _isRechargingMana = false;
    private bool _isRechargingStamina = false;
    private bool _isHealingSelf = false;

    // Pre-buff combat teardown: one-shot flag set the first tick we issue
    // UseObject(wand) while in a physical combat mode. Reset on reaching Magic.
    // Without the teardown, AC sees the equip request while a melee/missile
    // attack is still pending server-side and the resulting "you can only
    // move or use one item at a time" notice can wedge all item actions
    // until relog.
    private bool _combatTeardownDoneForCurrentBuffCycle;

    // Wield gate: suppress duplicate UseObject(wand) calls while a prior
    // equip is in flight. Cleared the moment the wielded check confirms;
    // if it never does, the cooldown short-circuits further retries so we
    // don't compound the same desync the missing CancelAttack causes.
    private int _pendingWieldId;
    private DateTime _pendingWieldAt = DateTime.MinValue;
    private DateTime _wieldCooldownUntil = DateTime.MinValue;
    private const double WieldResolveTimeoutMs = 2500;
    private const double WieldCooldownMs = 5000;

    // FIX (2026-06-24, bow->wand buff swap): stock ACE refuses to wield a Held-slot wand
    // while the bow is in the main hand and does NOT auto-dequip it (CheckWeaponCollision),
    // so the bare UseObject(wand) below never confirmed -> permanent buffing coma. We must
    // stow the bow into a capacity-verified open pack FIRST (AutoCram pattern), then wield
    // the wand. All paths are bounded so a stale/no-op dequip or unwieldable wand can never
    // loop forever; on exhaustion the buff family is parked and the bot fights unbuffed.
    private int _bowDequipPendingId;
    private DateTime _bowDequipAt = DateTime.MinValue;
    private int _bowDequipAttempts;
    private const int BowDequipMaxAttempts = 3;
    private int _wieldGateFailCount;
    private const int WieldGateFailMax = 3;
    private bool _wandSwapExhausted;   // per-EnsureMagicMode(forBuff) signal: swap can't succeed now
    // Per-EnsureMagicMode signal for EVERY caller (vitals included): the switch to Magic
    // can't complete right now. A vital cast used to report "handled" here, so the potion
    // after it never ran and the heal flag held Buffing over combat - an archer with a
    // full pack stood still at low health, drinking nothing and not shooting back.
    private bool _magicModeBlocked;
    // After a blocked switch, vital casts skip the swap for this long (kits and potions
    // still run), so the failing swap isn't retried and logged every tick.
    private DateTime _vitalSwapBlockedUntil = DateTime.MinValue;
    private const double VitalSwapBlockedRetrySec = 10;

    // Shared cross-subsystem weapon-swap serializer (set by RynthAiPlugin).
    // Prevents the buff wand-equip from racing CombatManager's weapon equip.
    private WeaponSwapGate? _weaponSwapGate;
    public void SetWeaponSwapGate(WeaponSwapGate gate) => _weaponSwapGate = gate;

    // CombatManager handle (set by RynthAiPlugin after CombatManager is built).
    // CheckVitals consults HasCloseThreat(MonsterRange) to pick between the
    // in-combat thresholds (HealAt / RestamAt / GetManaAt) and the idle top-off
    // thresholds (TopOffHP / TopOffStam / TopOffMana). Null = treat as idle.
    private CombatManager? _combatManager;
    public void SetCombatManager(CombatManager cm) => _combatManager = cm;

    /// <summary>Live combat mode read from AC each access — never drifts if OnCombatModeChange is missed.</summary>
    public int CurrentCombatMode =>
        _host.HasGetCurrentCombatMode ? _host.GetCurrentCombatMode() : CombatMode.NonCombat;

    /// <summary>Client busy count — when > 0, don't send any game actions (but see
    /// <see cref="BusyHoldsHeal"/>: a heal goes ahead of a leftover count).</summary>
    public int BusyCount
    {
        get => _busyCount;
        set
        {
            _busyCount = value;
            _busyGate.NoteCount(value, UseDoneSeqNow());
        }
    }
    private int _busyCount;

    // A heal and a leftover busy count (2026-10-05, a mage's heals "sometimes several seconds
    // late"): a war or void cast raises the plugin's busy count, and AC lowers its own count
    // inline when the server's UseDone arrives, where the busy hook can't see it, so the plugin's
    // count sits at 1 until CheckBusyTimeout force-clears it 5 s after it rose (CombatBusyGate).
    // Combat checks for that leftover before each attack. A heal never did: once health crossed a
    // line the arbiter gave the tick to vitals, combat stopped running, nobody cleared the count,
    // and the heal waited out the rest of the 5 s. How late depended on how long before the drop
    // the last war spell went out. Heals now use the same rule combat does.
    private readonly CombatBusyGate _busyGate = new();
    private Action<string>? _onStaleBusy;
    /// <summary>Resets the plugin's shared busy mirror when a heal finds the count is a leftover
    /// (the same callback combat uses). Set by RynthAiPlugin.</summary>
    public void SetStaleBusyCallback(Action<string> cb) => _onStaleBusy = cb;

    /// <summary>
    /// True while the busy count should hold a heal: it is positive and not a leftover. A leftover
    /// (a UseDone arrived after the count last rose and no cast gesture is animating: the server
    /// finished every action behind it) is cleared here, as combat does, and lets the heal go.
    /// </summary>
    internal bool BusyHoldsHeal()
    {
        if (BusyCount <= 0) return false;
        bool gesture = _host.HasGetCastBusyState && !_host.CanCastNow;
        if (!_busyGate.IsStale(BusyCount, _host.HasUseDoneSeq, UseDoneSeqNow(), gesture)) return true;
        int was = BusyCount;
        StaleBusyClears++;
        _onStaleBusy?.Invoke($"vitals: health is under a heal line and the server finished the action (UseDone) — was {was}");
        if (BusyCount > 0) BusyCount = 0;   // no callback, or the shared mirror was already 0
        _busyGate.Reset();
        return false;
    }
    /// <summary>Times a heal found the busy count was a leftover and went ahead (tests, status).</summary>
    internal int StaleBusyClears { get; private set; }

    /// <summary>
    /// Record-only diagnostic (D4): the reason the most recent buff-decision cycle
    /// did NOT issue a self/player buff cast (or the site that yielded the tick).
    /// Purely additive — set at each skip site, never read by control flow.
    /// Surfaced through GetStateSnapshot()/BuffStateSnapshot for /ra why + arbiter.
    /// </summary>
    public string LastBuffSkipReason { get; private set; } = "";

    private readonly List<string> BaseCreatureBuffs = new()
    {
        "Strength Self", "Endurance Self", "Coordination Self",
        "Quickness Self", "Focus Self", "Willpower Self",
        "Magic Resistance Self", "Invulnerability Self", "Impregnability Self"
    };

    private readonly Dictionary<AcSkillType, string> CreatureSkillBuffs = new()
    {
        { AcSkillType.MeleeDefense, "Invulnerability Self" },
        { AcSkillType.MissileDefense, "Impregnability Self" },
        { AcSkillType.MagicDefense, "Magic Resistance Self" },
        { AcSkillType.HeavyWeapons, "Heavy Weapon Mastery" },
        { AcSkillType.LightWeapons, "Light Weapon Mastery" },
        { AcSkillType.FinesseWeapons, "Finesse Weapon Mastery" },
        // Missile Weapons was missing entirely, so archers never got their mastery buff.
        { AcSkillType.MissileWeapons, "Missile Weapon Mastery" },
        { AcSkillType.TwoHandedCombat, "Two Handed Combat Mastery" },
        { AcSkillType.Shield, "Shield Mastery" },
        { AcSkillType.DualWield, "Dual Wield Mastery" },
        { AcSkillType.Recklessness, "Recklessness Mastery" },
        { AcSkillType.SneakAttack, "Sneak Attack Mastery" },
        { AcSkillType.DirtyFighting, "Dirty Fighting Mastery" },
        { AcSkillType.AssessCreature, "Monster Attunement" },
        { AcSkillType.AssessPerson, "Person Attunement" },
        { AcSkillType.ArcaneLore, "Arcane Enlightenment" },
        { AcSkillType.ArmorTinkering, "Armor Tinkering Expertise" },
        { AcSkillType.ItemTinkering, "Item Tinkering Expertise" },
        { AcSkillType.MagicItemTinkering, "Magic Item Tinkering Expertise" },
        { AcSkillType.WeaponTinkering, "Weapon Tinkering Expertise" },
        { AcSkillType.Salvaging, "Arcanum Salvaging" },
        { AcSkillType.Run, "Sprint" },
        { AcSkillType.Jump, "Jumping Mastery" },
        { AcSkillType.Loyalty, "Fealty" },
        { AcSkillType.Leadership, "Leadership Mastery" },
        { AcSkillType.Deception, "Deception Mastery" },
        { AcSkillType.Healing, "Healing Mastery" },
        { AcSkillType.Lockpick, "Lockpick Mastery" },
        { AcSkillType.Cooking, "Cooking Mastery" },
        { AcSkillType.Fletching, "Fletching Mastery" },
        { AcSkillType.Alchemy, "Alchemy Mastery" },
        { AcSkillType.ManaConversion, "Mana Conversion Mastery" },
        { AcSkillType.CreatureEnchantment, "Creature Enchantment Mastery" },
        { AcSkillType.ItemEnchantment, "Item Enchantment Mastery" },
        { AcSkillType.LifeMagic, "Life Magic Mastery" },
        { AcSkillType.WarMagic, "War Magic Mastery" },
        { AcSkillType.VoidMagic, "Void Magic Mastery" },
        { AcSkillType.Summoning, "Summoning Mastery" },
    };

    public BuffManager(RynthCoreHost host, LegacyUiSettings settings, SpellManager spellManager, PlayerVitalsCache vitals)
    {
        _host = host;
        _settings = settings;
        _spellManager = spellManager;
        _vitals = vitals;
        _cast = new CastTracker(s => _host.Log(s));
        _healLatency = new HealLatencyTracker(s => _host.Log(s));
    }

    public void SetCharacterSkills(CharacterSkills skills) => _charSkills = skills;
    public void SetWorldObjectCache(WorldObjectCache cache) => _worldObjectCache = cache;

    public void SetTimerPath(string charFolder)
    {
        _buffTimerPath = System.IO.Path.Combine(charFolder, "bufftimers.txt");
        LoadBuffTimers();
    }

    public void SaveBuffTimers()
    {
        if (string.IsNullOrEmpty(_buffTimerPath)) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_buffTimerPath)!);
            var lines = new List<string>();
            foreach (var kvp in _ramBuffTimers)
            {
                var info = kvp.Value;
                // Never persist permanent player enchants: they are a live-read
                // presence cache. After a death/dispel + relog the buff is gone
                // server-side, so it must be re-proven by a fresh live read,
                // not restored from disk as still-active.
                if (info.IsPermanent) continue;
                lines.Add($"ram|{kvp.Key}|{info.Expiration.Ticks}|{info.SpellLevel}|{info.SpellName}");
            }
            foreach (var kvp in _itemSpellTimers)
            {
                var info = kvp.Value;
                lines.Add($"item|{kvp.Key}|{info.CastAt.Ticks}|{info.ExpiresAt.Ticks}|{info.SpellLevel}|{info.SpellName}");
            }
            // The "char doesn't know this spell" blacklist is NO LONGER persisted.
            // Persisting it meant one lag-induced no-chat timeout permanently
            // poisoned a KNOWN spell across all future sessions, collapsing the
            // tier walk down to level 1. It is now session-only: re-proven each
            // login, cleared on relog.
            System.IO.File.WriteAllLines(_buffTimerPath, lines);
        }
        catch { }
    }

    public void LoadBuffTimers()
    {
        if (string.IsNullOrEmpty(_buffTimerPath)) return;
        if (!System.IO.File.Exists(_buffTimerPath)) return;
        try
        {
            _ramBuffTimers.Clear();
            _itemSpellTimers.Clear();
            foreach (string line in System.IO.File.ReadAllLines(_buffTimerPath))
            {
                string[] p = line.Split('|');

                if (p[0] == "item" && p.Length >= 6)
                {
                    DateTime castAt    = new DateTime(long.Parse(p[2]));
                    DateTime expiresAt = new DateTime(long.Parse(p[3]));
                    int level = int.Parse(p[4]);
                    string name = p[5];
                    if (expiresAt > DateTime.Now)
                    {
                        int family = new SpellInfo(0, name).Family;
                        _itemSpellTimers[family] = new ItemSpellRecord
                        {
                            CastAt = castAt, ExpiresAt = expiresAt,
                            SpellLevel = level, SpellName = name,
                        };
                    }
                    continue;
                }

                // Legacy "unknown|<id>" blacklist lines are intentionally ignored
                // (no longer restored — see SaveBuffTimers). Skip so they don't
                // fall through and misparse. Existing files self-clean on next save.
                if (p[0] == "unknown") continue;

                // Legacy single-prefix "family|ticks|level|name" or new "ram|family|ticks|level|name"
                int offset = (p[0] == "ram") ? 1 : 0;
                if (p.Length < offset + 4) continue;
                {
                    DateTime expiration = new DateTime(long.Parse(p[offset + 1]));
                    int level = int.Parse(p[offset + 2]);
                    string name = p[offset + 3];
                    int family = new SpellInfo(0, name).Family;
                    if (expiration > DateTime.Now)
                        _ramBuffTimers[family] = new RamTimerInfo { Expiration = expiration, SpellLevel = level, SpellName = name };
                }
            }
            int total = _ramBuffTimers.Count + _itemSpellTimers.Count;
            if (total > 0)
                _host.WriteToChat($"[RynthAi] Restored {_ramBuffTimers.Count} player + {_itemSpellTimers.Count} item spell timer(s).", 1);
        }
        catch { }
    }

    public void Dispose() { }

    // Force Rebuff / Cancel requested from another thread (the ImGui FR button runs on
    // the render thread, /ra chat commands on AC's main thread, the UI exports on the
    // caller's). ForceFullRebuff clears and rewrites the timer dictionaries that
    // OnHeartbeat enumerates on the plugin pump thread, so the request is only recorded
    // here and applied at the top of the next OnHeartbeat.
    private int _forceRebuffRequested;
    private int _cancelBuffingRequested;
    public void RequestForceFullRebuff() => System.Threading.Interlocked.Exchange(ref _forceRebuffRequested, 1);
    public void RequestCancelBuffing()   => System.Threading.Interlocked.Exchange(ref _cancelBuffingRequested, 1);

    public void ForceFullRebuff()
    {
        _isForceRebuffing = true;
        _isAutoBatchRebuff = false;
        _forceRebuffCastFamilies.Clear();
        _buffFailCooldownUntil.Clear(); // explicit recast-all must not be blocked by stale cooldowns
        _silentNoShowCounts.Clear();    // give parked families a fresh shot on FR too
        _unsatisfiedStrikes.Clear();    // ditto for the post-batch satisfaction audit
        _castIssuedAtByFamily.Clear();
        _expiryAtCastByFamily.Clear();
        _ramBuffTimers.Clear();
        _itemSpellTimers.Clear();
        _pendingSpellId = 0;
        _lastCastAttempt = DateTime.MinValue;
        SaveBuffTimers();
        _host.WriteToChat("[RynthAi] Starting Force Rebuff...", 5);

        var list = BuildDynamicBuffList();
        _host.Log($"[FR] buff list ({list.Count}): {string.Join(", ", list)}");
        _host.Log($"[FR] macroRunning={_settings.IsMacroRunning} liveRefreshed={_liveBuffsRefreshed}");
    }

    public void CancelBuffing()
    {
        _isForceRebuffing = false;
        _isAutoBatchRebuff = false;
        _isRechargingMana = false;
        _isRechargingStamina = false;
        _isHealingSelf = false;
        _pendingSpellId = 0;
        _host.WriteToChat("[RynthAi] Sequence cancelled.", 5);
    }

    /// <summary>
    /// Spell id of a cast awaiting server confirmation, or 0. Read by the activity
    /// arbiter: a cast in flight must keep Buffing winning so CombatManager can't
    /// slip a peace-mode switch in mid-cast and fizzle it.
    /// </summary>
    public int PendingSpellId => _pendingSpellId;

    /// <summary>
    /// True when the last vitals evaluation wanted a heal / restam / remana. Also
    /// read by the arbiter — vital recharges run even with buffing switched off,
    /// and they need the same mid-cast protection a buff gets. Side-effect free:
    /// CheckVitals repopulates these flags every cycle it reaches.
    /// </summary>
    public bool WantsVitalRecharge => _isHealingSelf || _isRechargingMana || _isRechargingStamina || WantsHealNow();

    // Health below a heal line wins the arbiter's tick by itself. The flags above are only set on
    // a tick where CheckVitals actually ran and acted, and it doesn't run while the client is busy
    // (CanCastNow / BusyCount): looting keeps it busy almost constantly, so in a pack the arbiter
    // never saw "wants to heal", looting and nav kept the tick, and the character nearly died
    // (2026-10-04). Backs off for HealUnavailableMs after a check that found nothing able to
    // heal, so an unhealable state can't hold the bot (the old reason the flags were strict).
    private DateTime _healUnavailableUntil = DateTime.MinValue;
    private const double HealUnavailableMs = 5000;
    private const double HealInterruptMinMs = 600;   // a buff gesture is never shorter; CanCastNow also gates

    internal bool WantsHealNow()
    {
        if (!_settings.IsMacroRunning || _vitals.MaxHealth == 0) return false;
        if (DateTime.Now < _healUnavailableUntil) return false;
        return HealthUnderAnyLine(_vitals.HealthPct, _vitals.StaminaPct);
    }

    private bool HealthUnderAnyLine(int hp, int stamPct) => ActiveHealLine(hp, stamPct, out _, out _).Length > 0;

    /// <summary>For the engine's reload deferral (ReloadSafety): the heal line health is under now
    /// ("" = none, or vitals not read yet), with the health and the line's value.</summary>
    internal string HealLineNow(out int healthPct, out int lineValue)
    {
        healthPct = _vitals.HealthPct;
        lineValue = 0;
        if (_vitals.MaxHealth == 0) return "";
        return ActiveHealLine(healthPct, _vitals.StaminaPct, out lineValue, out _);
    }

    /// <summary>For the engine's reload deferral: the cast awaiting its result (pending, or held
    /// open by the server), or "".</summary>
    internal string CastInFlightName
    {
        get
        {
            if (_pendingSpellId != 0) return SpellTableStub.GetById(_pendingSpellId)?.Name ?? $"spell {_pendingSpellId}";
            return _cast.Held?.Name ?? "";
        }
    }

    /// <summary>
    /// The heal line health is under, by name ("" = none): Emergency Heal At, Stamina To Health At,
    /// Heal At (a monster within MonsterRange) or Top Off HP (idle). The same test HealthUnderAnyLine
    /// always made; the name and value are for [HealLatency].
    /// </summary>
    private string ActiveHealLine(int hp, int stamPct, out int value, out bool inCombat)
    {
        inCombat = _combatManager?.HasCloseThreat(System.Math.Max(1, _settings.MonsterRange)) == true;
        value = 0;
        if (_settings.EmergencyHealAt > 0 && hp <= _settings.EmergencyHealAt) { value = _settings.EmergencyHealAt; return "Emergency Heal At"; }
        if (StaminaToHealthAllowed(_settings, hp, stamPct)) { value = _settings.StaminaToHealthAt; return "Stamina To Health At"; }
        value = inCombat ? _settings.HealAt : _settings.TopOffHP;
        if (hp < value) return inCombat ? "Heal At" : "Top Off HP";
        return "";
    }

    // ── What the activity display says while the arbiter's slot is "Buffing" ──
    // BotAction stays "Buffing" for every vitals job: it is a control string (CombatManager's
    // canRun and CorpseOpenController read it). The dashboard and the phone showed it as is, so a
    // heal read "Buffing" (2026-10-05). The display label says which job it is.
    public const string LabelHealing = "Healing";
    public const string LabelMana = "Restoring mana";
    public const string LabelStamina = "Restoring stamina";
    public const string LabelBuffing = "Buffing";

    /// <summary>
    /// Why the vitals/buff slot is held, for display: Healing, Restoring mana, Restoring stamina or
    /// Buffing. The cast or kit in flight says it first; then health under a heal line; then what
    /// the last vitals check started. Side-effect free (KitPending is not called).
    /// </summary>
    public string BuffingLabel
    {
        get
        {
            if (_pendingSpellId != 0 && _pendingSpellId == _pendingVitalSpellId && _pendingVitalLabel.Length > 0)
                return _pendingVitalLabel;
            if (_kitUsedAt != DateTime.MinValue && (DateTime.Now - _kitUsedAt).TotalMilliseconds < KitResultTimeoutMs)
                return _kitIsStamina ? LabelStamina : LabelHealing;
            if (_isHealingSelf || WantsHealNow()) return LabelHealing;
            if (_pendingSpellId != 0) return LabelBuffing;
            if (_isRechargingMana) return LabelMana;
            if (_isRechargingStamina) return LabelStamina;
            return LabelBuffing;
        }
    }

    /// <summary>The display label for a vital spell's base name (AttemptVitalCast's argument).</summary>
    internal static string VitalLabel(string baseName) => baseName switch
    {
        "Heal Self" or "Stamina to Health Self" => LabelHealing,
        "Stamina to Mana Self" => LabelMana,
        "Revitalize Self" => LabelStamina,
        _ => LabelBuffing,
    };
    private string _pendingVitalLabel = "";
    private bool _kitIsStamina;

    public BuffStateSnapshot GetStateSnapshot() => new()
    {
        EnableBuffing       = _settings.EnableBuffing,
        IsForceRebuffing    = _isForceRebuffing,
        IsRechargingMana    = _isRechargingMana,
        IsRechargingStamina = _isRechargingStamina,
        IsHealingSelf       = _isHealingSelf,
        PendingSpellId      = _pendingSpellId,
        LastCastAttempt     = _lastCastAttempt,
        RamBuffTimerCount   = _ramBuffTimers.Count,
        ItemSpellTimerCount = _itemSpellTimers.Count,
        NeedsAnyBuffNow     = NeedsAnyBuff(),
        LastBuffSkipReason  = LastBuffSkipReason,
        HealthPct           = _vitals.HealthPct,
        ManaPct             = _vitals.ManaPct,
        StaminaPct          = _vitals.StaminaPct,
    };

    public struct BuffStateSnapshot
    {
        public bool     EnableBuffing;
        public bool     IsForceRebuffing;
        public bool     IsRechargingMana;
        public bool     IsRechargingStamina;
        public bool     IsHealingSelf;
        public int      PendingSpellId;
        public DateTime LastCastAttempt;
        public int      RamBuffTimerCount;
        public int      ItemSpellTimerCount;
        public bool     NeedsAnyBuffNow;
        public int      HealthPct;
        public int      ManaPct;
        public int      StaminaPct;
        public string   LastBuffSkipReason;
    }

    private bool _liveBuffsRefreshed;
    /// <summary>Host tests: skip the login refresh wait so OnHeartbeat runs at once.</summary>
    internal void MarkLiveBuffsReadyForTests() => _liveBuffsRefreshed = true;
    /// <summary>The login refresh has a trusted snapshot of the live buffs (buff casting may start).</summary>
    public bool LiveBuffsReady => _liveBuffsRefreshed;
    /// <summary>
    /// Set by RynthAiPlugin when this login is a hot reload (engine or plugin) of a character
    /// that was already in game in this client: the enchantment registry is complete, so the
    /// login refresh trusts its first read instead of waiting for two equal reads (5.8 s after
    /// the 2026-10-05 reload).
    /// </summary>
    public bool CarriedOverSession { get; set; }
    private DateTime _lastLiveRefreshAttempt = DateTime.MinValue;
    private DateTime _lastPeriodicRefreshAt = DateTime.MinValue;
    private const int PeriodicRefreshIntervalMs = 30_000;

    // Login-refresh stabilization: the server streams the active-enchantment
    // registry over a few seconds after login, so an early read returns a
    // partial/empty set. Opening the gate on that wipes timers → full rebuff
    // every login. Wait until the count is the SAME across two consecutive 1s
    // reads (registry done streaming — works for 0 buffs or 50), or a timeout.
    private DateTime _loginRefreshStartAt = DateTime.MinValue;
    private int _lastLoginRefreshCount = -1;
    private const int LoginRefreshMaxWaitMs = 20_000;

    // ── [HealLatency] (see HealLatencyTracker) ─────────────────────────────────
    private readonly HealLatencyTracker _healLatency;
    internal HealLatencyTracker HealLatency => _healLatency;
    // Why this heartbeat sent no heal (the tracker files the tick's time under it), plus a detail.
    private string _healHold = "";
    private string _healHoldDetail = "";
    // What a vitals step that "handled" the tick without sending anything was waiting on.
    private string _healWaitKind = "";
    /// <summary>The arbiter's last decision (set by RynthAiPlugin after it decides), for [HealLatency].</summary>
    internal string ArbiterActivity { get; set; } = "";

    private void Hold(string why, string detail = "")
    {
        _healHold = why;
        _healHoldDetail = detail;
    }

    /// <summary>A heal (kit or spell) sent earlier hasn't resolved yet.</summary>
    private bool HealInFlight() =>
        (_pendingSpellId != 0 && _pendingSpellId == _pendingVitalSpellId && _pendingVitalLabel == LabelHealing)
        || (_kitUsedAt != DateTime.MinValue && !_kitIsStamina && (DateTime.Now - _kitUsedAt).TotalMilliseconds < KitResultTimeoutMs);

    public void OnHeartbeat()
    {
        bool track = _settings.IsMacroRunning && _vitals.MaxHealth > 0;
        _healHold = "";
        _healHoldDetail = "";
        _healWaitKind = "";
        if (track)
        {
            string line = ActiveHealLine(_vitals.HealthPct, _vitals.StaminaPct, out int lineValue, out bool inCombat);
            _healLatency.Observe(Now, _vitals.HealthPct, line, lineValue, inCombat, HealInFlight());
        }
        try { HeartbeatCore(); }
        finally
        {
            NoteStanceLateHolder();
            if (track)
            {
                string hold = _healHold;
                if (hold.Length == 0 && DateTime.Now < _healUnavailableUntil) hold = "heal backoff (nothing could heal)";
                _healLatency.EndTick(Now, hold, _healHoldDetail, ArbiterActivity);
            }
        }
    }

    private void HeartbeatCore()
    {
        // Requests queued from other threads (see RequestForceFullRebuff), in click order
        // as far as it matters: a cancel after a rebuff request wins.
        if (System.Threading.Interlocked.Exchange(ref _forceRebuffRequested, 0) != 0)
            ForceFullRebuff();
        if (System.Threading.Interlocked.Exchange(ref _cancelBuffingRequested, 0) != 0)
            CancelBuffing();

        // Post-cast diffs for the bufftest experiment run unconditionally — even if the
        // user toggled the macro off mid-experiment, we still want to capture and log them.
        // Drain any pending diagnostics whose 2s post-cast window has elapsed.
        while (_pendingDiagnostics.Count > 0 &&
               (DateTime.Now - _pendingDiagnostics.Peek().CastedAt).TotalSeconds >= 2.0)
        {
            var diag = _pendingDiagnostics.Dequeue();
            LogPostCastDiff(diag);
        }

        if (!_settings.IsMacroRunning) return;

        // Close casts the server reports finished, and say once when one is being held open.
        PollCastTracker();

        // A Magic stance we asked for: watch the client's mode every tick (see ObserveStance).
        ObserveStance();

        // RefreshFromLiveMemory at OnLoginComplete fails when the server-time
        // packet hasn't landed yet (GetServerTime returns 0). On those
        // logins we have NO timers and would recast every buff that has
        // hours left server-side. Retry every second until we get the
        // live snapshot, and hold BUFF casts in the meantime.
        //
        // Heals are not held (2026-10-05, Drakkon died after an engine hot reload mid-fight:
        // "health 12% under Emergency Heal At 30% ... held by: login buff refresh 5737 ms").
        // The refresh only protects buff timers; a heal, a kit or a potion never reads them,
        // so the vitals step below runs during the refresh and only the buff casting after it
        // waits (buffsHeldForRefresh).
        bool buffsHeldForRefresh = false;
        if (!_liveBuffsRefreshed)
        {
            if ((DateTime.Now - _lastLiveRefreshAttempt).TotalMilliseconds > 1000)
            {
                _lastLiveRefreshAttempt = DateTime.Now;
                int n = RefreshFromLiveMemory();
                if (n >= 0)
                {
                    if (_loginRefreshStartAt == DateTime.MinValue) _loginRefreshStartAt = DateTime.Now;
                    // Only trust the snapshot once the registry stops growing
                    // (two equal 1s reads), so we don't open the gate on a
                    // half-streamed set and rebuff buffs that are still landing.
                    // After a hot reload (engine or plugin) the client never left the world and
                    // its registry is complete: the first read is trusted (CarriedOverSession), unless
                    // it is empty (a read that found nothing yet must not wipe every timer).
                    bool stable    = (n == _lastLoginRefreshCount);
                    bool timedOut  = (DateTime.Now - _loginRefreshStartAt).TotalMilliseconds > LoginRefreshMaxWaitMs;
                    bool carried   = CarriedOverSession && n > 0;
                    _lastLoginRefreshCount = n;
                    if (stable || timedOut || carried)
                    {
                        _liveBuffsRefreshed = true;
                        string why = carried && !stable && !timedOut
                            ? "hot reload: still in game, the first read is complete"
                            : $"stable={stable} timedOut={timedOut}";
                        _host.Log($"[BuffDiag] login refresh ready: {n} enchantment(s) ({why})");
                        _host.WriteToChat($"[RynthAi] Live buff timers ready ({n} loaded).", 1);
                    }
                }
            }
            // Don't cast a buff until we have a real snapshot — otherwise
            // we'd recast over a 1-hour-old buff at the start of every login.
            buffsHeldForRefresh = !_liveBuffsRefreshed;
            if (buffsHeldForRefresh && !HealthUnderAnyLine(_vitals.HealthPct, _vitals.StaminaPct))
            {
                Hold("login buff refresh");
                return;
            }
        }

        // Periodic re-sync: if the character died and lost all enchantments, the
        // in-memory timers still show buffs as "active" until their timestamps
        // expire normally — causing NeedsAnyBuff() to return false while the char
        // is completely unbuffed. Re-read from live AC memory every 30s to detect
        // this gap and clear stale timers so rebuffing resumes promptly.
        if ((DateTime.Now - _lastPeriodicRefreshAt).TotalMilliseconds > PeriodicRefreshIntervalMs)
        {
            _lastPeriodicRefreshAt = DateTime.Now;
            int n = RefreshFromLiveMemory();
            if (n >= 0)
                _host.Log($"[BuffDiag] periodic sync: {n} active enchantment(s) in RAM timers.");
        }

        // ── Pending-cast resolution — self-buffs and armor use DIFFERENT signals ──
        // Self-buffs ARE in the live player-enchantment registry, so we confirm
        // them by reading it (no chat, no no-chat blacklisting that poisons the
        // tier walk). Armor/item enchants are NOT in the player registry — they
        // live on the item — so they still resolve via chat + the no-chat valve.
        // Healing interrupts buffing: a BUFF awaiting confirmation (up to 2.5 s, 5 s for armor)
        // held every heal but an emergency kit/potion. Once its gesture is done the server has
        // the cast; with health under a heal line, stop waiting for the confirmation and let
        // CheckVitals heal now. A late confirmation still lands through the enchantment events;
        // at worst a buff is recast later (2026-10-04).
        if (_pendingSpellId != 0 && _pendingSpellId != _pendingVitalSpellId && WantsHealNow()
            && (DateTime.Now - _lastCastAttempt).TotalMilliseconds >= HealInterruptMinMs
            && CastGateWatchdog.CanCastNow(_host.CanCastNow, s => _host.Log(s)) && !BusyHoldsHeal())
        {
            _host.Log($"[BuffDiag] health {_vitals.HealthPct}% under a heal line: released pending buff id={_pendingSpellId} to heal first.");
            _pendingSpellId = 0;
            _onCastResolved?.Invoke("released to heal");
        }

        if (_pendingSpellId != 0)
        {
            var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
            bool pendingIsArmor = pendingSpell != null && IsItemEnchantment(pendingSpell.Name);

            // VITAL (Heal Self, Stamina to Health, Revitalize, Stamina to Mana): instant, never
            // in the enchantment registry. Its "You cast ... and restore N points" line clears
            // it; when that line is late, the registry poll below counted a no-show, and two
            // of those parked Heal Self for 30 minutes, so a mage healed nothing between
            // Emergency Heal At and Heal At (tester's log, 2026-10-01). Now a late line just
            // releases the pending cast, with no strike.
            if (pendingSpell != null && _pendingSpellId == _pendingVitalSpellId)
            {
                // Timed from the incantation when one arrived (2026-10-05): a healthy DreamWeave
                // vital takes 2.2-2.5 s after its incantation, and 2.5 s from the send gave up on
                // casts the server was still finishing.
                if (PendingGiveUpReached(SelfBuffGiveUpMs))
                {
                    _host.Log($"[BuffDiag] vital cast '{pendingSpell.Name}' (id={_pendingSpellId}) not confirmed by chat ({GiveUpWhy(SelfBuffGiveUpMs)}) — released, no strike.");
                    _pendingSpellId = 0;
                    _pendingVitalSpellId = 0;
                    _onCastResolved?.Invoke("vital cast unconfirmed");
                }
                else
                {
                    TryEmergencyHealWhilePending();
                    Hold(_pendingVitalLabel == LabelHealing ? "heal cast in flight" : "vital cast in flight", pendingSpell.Name);
                    return;
                }
            }
            else if (pendingSpell != null && !pendingIsArmor)
            {
                // SELF-BUFF: confirm against the live registry once the cast has
                // settled. (OnEnchantmentAdded usually clears pending faster; this
                // poll is the authoritative fallback when the hook doesn't fire.)
                double sinceCastMs = (DateTime.Now - _lastCastAttempt).TotalMilliseconds;
                if (sinceCastMs > SelfBuffConfirmMs
                    && (DateTime.Now - _lastSelfBuffPollAt).TotalMilliseconds > 250)
                {
                    _lastSelfBuffPollAt = DateTime.Now;
                    RefreshFromLiveMemory();
                    bool active = _ramBuffTimers.TryGetValue(pendingSpell.Family, out RamTimerInfo? st)
                                  && st.Expiration > DateTime.Now;
                    // A top-off (auto batch), Force Rebuff or tier upgrade casts over a
                    // buff that is still on, so "active" is true at the first poll
                    // whatever happened: a fizzle counted as landed, the buff wasn't
                    // refreshed, and the late fizzle line then cleared the NEXT cast.
                    // While the old entry hasn't moved, wait for the chat line or the
                    // enchantment event; at the give-up it is judged exactly as before.
                    // Give-up timed from the incantation when one arrived (see the vital path).
                    bool pastGiveUp = PendingGiveUpReached(SelfBuffGiveUpMs);
                    if (active && !pastGiveUp
                        && _castIssuedAtByFamily.TryGetValue(pendingSpell.Family, out DateTime issuedAt)
                        && _expiryAtCastByFamily.TryGetValue(pendingSpell.Family, out DateTime expiryThen)
                        && expiryThen > issuedAt                          // it was on when we cast
                        && st!.Expiration <= expiryThen.AddSeconds(2))    // and hasn't been refreshed
                        active = false;
                    if (active)
                    {
                        // During an auto-batch / force-rebuff, IsBuffActive gates on
                        // _forceRebuffCastFamilies (not timers), so the family MUST be
                        // marked cast here or the batch respins this buff forever.
                        if (_isForceRebuffing) _forceRebuffCastFamilies.Add(pendingSpell.Family);
                        _cast.Confirmed(_pendingSpellId, "confirmed in the enchantment registry");
                        _pendingSpellId = 0; // landed — registry confirms it; advance
                        NoteCastAccepted();  // it went through — end any too-busy backoff
                        _onCastResolved?.Invoke("self-buff confirmed (registry)");
                    }
                    else if (pastGiveUp)
                    {
                        // Not in the registry after settling → this tier didn't take.
                        // A WARM snapshot already excludes unknown tiers, so a no-show
                        // is lag/fizzle → retry (NO blacklist). A COLD snapshot has no
                        // other tier-down signal → blacklist this id to drop a tier.
                        bool cold = !_spellManager.IsKnownSnapshotWarm;
                        if (cold) _spellManager.MarkSpellUnresolvable(_pendingSpellId);

                        // Per-family silent-no-show bookkeeping (the /god loop break).
                        // Increment regardless of warmth — even the "warm → retry" path
                        // needs an upper bound, otherwise a buff whose family is in the
                        // dynamic list but can never actually land loops forever.
                        int fam = pendingSpell.Family;
                        int noShowCount = _silentNoShowCounts.TryGetValue(fam, out int prev) ? prev + 1 : 1;
                        _silentNoShowCounts[fam] = noShowCount;
                        bool parked = false;
                        if (noShowCount >= SilentNoShowThreshold)
                        {
                            _buffFailCooldownUntil[fam] = DateTime.Now + SilentNoShowCooldown;
                            parked = true;
                        }

                        _host.Log($"[BuffDiag] self-buff '{pendingSpell.Name}' (id={_pendingSpellId}, fam={fam}) absent from live registry after {sinceCastMs:0}ms — " +
                                  (cold ? "cold snapshot → blacklisted, tier-down." : "warm snapshot → retry (lag/fizzle).") +
                                  $" noShows={noShowCount}/{SilentNoShowThreshold}" +
                                  (parked ? $" — family parked {SilentNoShowCooldown.TotalMinutes:0}min." : ""));
                        _pendingSpellId = 0;
                        _onCastResolved?.Invoke("self-buff no-show (registry)");
                    }
                }
                // Yield while the self-buff cast is in flight. PendingSpellId keeps
                // the arbiter on Buffing, so CombatManager can't sneak a peace-mode
                // switch in mid-cast.
                if (_pendingSpellId != 0) { TryEmergencyHealWhilePending(); Hold("buff cast in flight", pendingSpell.Name); return; }
            }
            else if (pendingIsArmor)
            {
                // ARMOR/ITEM: chat is authoritative. No-chat valve abandons a cast
                // AC never answers so the cycle can't wedge; the chat handlers do
                // the cooldown. Blacklist (unless snapshot confirms known) drops tier.
                // From the send, or later when the incantation came late (never earlier than before).
                if (PendingGiveUpReached(NoChatResolveTimeoutMs))
                {
                    int stuckId = _pendingSpellId;
                    var stuck = SpellTableStub.GetById(stuckId);
                    bool confirmedKnown = _spellManager.IsKnownSnapshotWarm && _spellManager.IsKnownSpellId(stuckId);
                    if (!confirmedKnown)
                        _spellManager.MarkSpellUnresolvable(stuckId);

                    // 2026-05-25 — per-family silent-no-show bookkeeping on the
                    // ARMOR path, mirroring the self-buff path above.
                    //
                    // Without this, an armor enchant whose target item isn't
                    // wielded — for example after the character died and lost
                    // her armor — loops forever at the 5s no-chat timeout:
                    // AC accepts the cast attempt ("Casting Impenetrability
                    // VI"), can't bind it to an item, silently drops it, never
                    // produces success chat. confirmedKnown=True (Impen is in
                    // the spellbook), so the existing path logged "NOT
                    // blacklisted — lag/busy" and ForceRebuff re-fired the same
                    // cast 12+ times/minute. Every retry pushes "Casting <X>"
                    // through AC's text pipeline, feeding the documented text-
                    // parser singleton race
                    // (rynthcore_crash_investigation.md 2026-05-25 entry).
                    //
                    // Park after SilentNoShowThreshold consecutive misses; the
                    // selectors at lines 763/823 honour _buffFailCooldownUntil
                    // so the family is skipped during the cooldown window. The
                    // bot keeps trying eventually — just not 12×/min while
                    // there's no armor to bind to.
                    int fam = stuck?.Family ?? 0;
                    bool parked = false;
                    if (fam != 0)
                    {
                        int noShowCount = _silentNoShowCounts.TryGetValue(fam, out int prev) ? prev + 1 : 1;
                        _silentNoShowCounts[fam] = noShowCount;
                        if (noShowCount >= SilentNoShowThreshold)
                        {
                            _buffFailCooldownUntil[fam] = DateTime.Now + SilentNoShowCooldown;
                            parked = true;
                        }
                    }

                    _host.Log($"[BuffChat] NO-CHAT TIMEOUT (armor) pending={stuckId} ('{stuck?.Name}') — no chat in " +
                              $"{NoChatResolveTimeoutMs:0}ms. confirmedKnown={confirmedKnown}. " +
                              (confirmedKnown ? "Lag/busy — NOT blacklisted." : "Blacklisted → tier-down.") +
                              (fam != 0 ? $" noShows={_silentNoShowCounts[fam]}/{SilentNoShowThreshold}" : "") +
                              (parked ? $" — family parked {SilentNoShowCooldown.TotalMinutes:0}min." : ""));
                    _pendingSpellId = 0;
                    _onCastResolved?.Invoke("no-chat timeout (armor)");
                }
                // Hold the cycle until chat resolves the item cast.
                if (_pendingSpellId != 0) { TryEmergencyHealWhilePending(); Hold("item spell in flight", pendingSpell?.Name ?? ""); return; }
            }
        }

        if ((DateTime.Now - _lastCastAttempt).TotalMilliseconds < _settings.SpellCastIntervalMs)
        {
            Hold("cast interval");
            return;
        }

        // Don't issue a cast while the previous cast GESTURE is still animating.
        // CanCastNow is the engine's CMotionInterp gesture gate (the REAL cast
        // gate). AC refuses a cast issued mid-gesture with a server-driven
        // "You're too busy!" notice; the old code gated on BusyCount (the
        // ClientUISystem hourglass) which reads 0 during a cast, so it never
        // blocked the retry → tight refusal loop → AddTextToScroll re-entry AV
        // at 0x00460D1D. On an engine without the gate, CanCastNow defaults to
        // true and the SpellCastIntervalMs throttle + parked-pending still bound
        // retries. BusyCount stays as a secondary guard so we also don't queue
        // casts/UseObject while the hourglass action is mid-flight.
        // Buffing priority (including "hold it while our cast is in flight so
        // CombatManager can't sneak in a peace-mode switch mid-cast") is the
        // arbiter's call now — it reads PendingSpellId / WantsVitalRecharge /
        // NeedsAnyBuff and is the sole writer of the "Buffing" string.
        // A heal goes ahead of a busy count the server has already finished (BusyHoldsHeal);
        // everything else still waits for the count to drop.
        bool gateOpen = CastGateWatchdog.CanCastNow(_host.CanCastNow, s => _host.Log(s));
        bool busyHolds = BusyCount > 0
            && (ActiveHealLine(_vitals.HealthPct, _vitals.StaminaPct, out _, out _).Length == 0 || BusyHoldsHeal());
        if (!gateOpen || busyHolds)
        {
            LastBuffSkipReason = busyHolds ? "busy (BusyCount>0)" : "cast gate closed (CanCastNow=false / gesture animating)";
            if (!gateOpen) Hold("cast gate (a gesture is animating)");
            else Hold("busy count", $"busy {BusyCount}");
            return;
        }

        if (CheckVitals())
        {
            if (_healHold.Length == 0) Hold(_healWaitKind.Length > 0 ? _healWaitKind : "vitals step busy", _vitalWhy);
            return;
        }

        // The login refresh above held buffs only: the heal had its turn, the buffs wait.
        if (buffsHeldForRefresh)
        {
            Hold("login buff refresh");
            return;
        }

        // Mana floor: no buff while mana is under Get Mana At. CheckVitals above already tried
        // Stamina to Mana and a mana potion; when neither could run (stamina too low, none
        // carried) buffing used to carry on and run mana to 0. Wait for it instead.
        int manaFloor = _settings.GetManaAt;
        if (manaFloor > 0 && _vitals.MaxMana > 0 && _vitals.ManaPct < manaFloor)
        {
            // The floor refills on its own: Stamina to Mana (or a potion) right here, then buffing
            // carries on. CheckVitals only refills below Top Off Mana out of combat, so a Top Off
            // Mana under Get Mana At left buffing waiting on natural regen - one buff every 1-2
            // minutes (tester, 10-01: 85 minutes of "buffing waits", Stamina to Mana never cast).
            // A low stamina is topped up first (kit, Revitalize, potion) so Stamina to Mana can run.
            if (_vitals.StaminaPct > 15 && AttemptVitalCast("Stamina to Mana Self"))
            {
                _isRechargingMana = true;
                return;
            }
            if (_vitals.StaminaPct <= 15 && (AttemptStaminaKitUse() || AttemptVitalCast("Revitalize Self")
                                              || AttemptPotion("StaminaPotion", "Stamina")))
            {
                _isRechargingStamina = true;
                return;
            }
            if (AttemptPotion("ManaPotion"))
            {
                _isRechargingMana = true;
                return;
            }
            LastBuffSkipReason = $"mana {_vitals.ManaPct}% under Get Mana At {manaFloor}%";
            if ((DateTime.Now - _lastManaFloorLogAt).TotalSeconds >= 30)
            {
                _lastManaFloorLogAt = DateTime.Now;
                _host.Log($"[RynthAi] Vitals: buffing waits: mana {_vitals.ManaPct}% is under Get Mana At {manaFloor}%, stamina {_vitals.StaminaPct}%, and nothing refilled it (Stamina to Mana / stamina / potion: {Why(_vitalWhy)})");
            }
            return;
        }

        // Too-busy backoff gates BUFFING only, and sits after CheckVitals on
        // purpose: a refused heal is worth retrying hard, a refused buff is not.
        // Blocking vitals here would have delayed emergency heals by up to 8s,
        // and the observed refusal episode happened while the character was being
        // swarmed by Olthoi — exactly when that would be most dangerous.
        // Wait for the server to finish the previous cast before issuing another.
        // Sits with the backoff, after CheckVitals, so it paces BUFFING only — a
        // heal must never queue behind a buff's resolution.
        if (IsAwaitingCastResolution())
        {
            LastBuffSkipReason = "awaiting server UseDone for the previous cast";
            return;
        }

        if (DateTime.Now < _busyBackoffUntil)
        {
            LastBuffSkipReason = $"too-busy backoff ({(_busyBackoffUntil - DateTime.Now).TotalMilliseconds:0}ms left, streak={_busyRefusalStreak})";
            return;
        }

        if (DateTime.Now < _lowManaPauseUntil)
        {
            LastBuffSkipReason = $"not enough mana ({(_lowManaPauseUntil - DateTime.Now).TotalMilliseconds:0}ms left)";
            return;
        }

        // Buff items and field rations sit behind the same gates as a buff cast (no cast
        // pending, server finished the last one, client not busy). They ran first, gated
        // only on the local gesture, so their UseObject landed while a buff was still
        // resolving server-side; the "You're too busy!" that drew cleared that buff's
        // pending state. When one of them acts, no buff cast follows in the same tick.
        DateTime beforeItems = _lastCastAttempt;
        try { TryUseBuffItems(); TryMakeRations(); } catch { }
        if (_lastCastAttempt != beforeItems) return;

        if (_settings.EnableBuffing)
        {
            if (CheckAndCastSelfBuffs())
                return;

            if (_isForceRebuffing)
            {
                bool wasAutoBatch = _isAutoBatchRebuff;
                _isForceRebuffing = false;
                _isAutoBatchRebuff = false;
                _host.Log($"[FR] complete — cast {_forceRebuffCastFamilies.Count} spell families{(wasAutoBatch ? " (auto batch)" : "")}");
                // Order matters: the flag must already be false (IsBuffActive
                // short-circuits on _forceRebuffCastFamilies while it is set, so
                // every family would read "active"), and the set must still be
                // populated — it is what the audit iterates.
                AuditBatchSatisfied();
                _forceRebuffCastFamilies.Clear();
                _host.WriteToChat(wasAutoBatch ? "[RynthAi] Rebuff complete." : "[RynthAi] Force Rebuff Complete.", 1);
            }
        }
    }

    /// <summary>
    /// Post-batch audit. Every family the batch actually CAST should read active
    /// now that the batch is over. One that doesn't is not going to be fixed by
    /// casting it again: the cast goes out, AC confirms it, the enchantment is
    /// genuinely on the character, and IsBuffActive still says false.
    ///
    /// The case this was written for: a permanent item-granted lower tier living
    /// in the same spell family as the tier we want, which keeps the tier-upgrade
    /// rule firing no matter how many times the higher tier lands. Because ANY
    /// single buff below threshold escalates to a full rebuff, one such family is
    /// enough to make the bot recast every buff it owns, back-to-back, forever.
    ///
    /// Park the family after UnsatisfiedStrikeThreshold consecutive batches. This
    /// is a backstop, not the cure — it bounds the damage from any future
    /// "lands but never reads active" bug rather than one specific cause.
    ///
    /// Must be called with _isForceRebuffing already false: while it is set,
    /// IsBuffActive answers from _forceRebuffCastFamilies and every family in
    /// that set reads active by construction.
    /// </summary>
    /// <summary>Stored expiry for a family across BOTH timer dictionaries
    /// (player buffs in _ramBuffTimers, item enchants in _itemSpellTimers), or
    /// DateTime.MinValue when the family has no timer at all.</summary>
    private DateTime CurrentExpiryFor(int family)
    {
        // An item-granted permanent isn't ours when CastBuffsOverItemBuffs is on (only possible
        // between the setting being switched on and the next live read).
        if (_ramBuffTimers.TryGetValue(family, out RamTimerInfo? t) && !(t.IsPermanent && _settings.CastBuffsOverItemBuffs))
            return t.Expiration;
        if (_itemSpellTimers.TryGetValue(family, out ItemSpellRecord? i)) return i.ExpiresAt;
        return DateTime.MinValue;
    }

    private void AuditBatchSatisfied()
    {
        foreach (int family in _forceRebuffCastFamilies)
        {
            if (!_lastCastSpellIdByFamily.TryGetValue(family, out int spellId) || spellId == 0)
                continue;

            if (IsBuffActive(spellId))
            {
                _unsatisfiedStrikes.Remove(family); // satisfied — forget past strikes
                continue;
            }

            // Give the refresh time to reach the registry before judging. A cast
            // issued seconds ago legitimately still reads its old timer.
            if (!_castIssuedAtByFamily.TryGetValue(family, out DateTime castAt)
                || (DateTime.Now - castAt).TotalMilliseconds < AuditSettleMs)
                continue;

            // The expiry moved forward, so the cast DID land — this family is
            // simply still inside the rebuff window for some other reason (a
            // duration shorter than RebuffSecondsRemaining, say). Not "cast that
            // never takes effect", so it must not accumulate strikes.
            DateTime expiryNow = CurrentExpiryFor(family);
            if (_expiryAtCastByFamily.TryGetValue(family, out DateTime expiryThen)
                && expiryNow > expiryThen)
            {
                _unsatisfiedStrikes.Remove(family);
                continue;
            }

            string name = SpellTableStub.GetById(spellId)?.Name ?? spellId.ToString();
            int strikes = _unsatisfiedStrikes.TryGetValue(family, out int prev) ? prev + 1 : 1;
            _unsatisfiedStrikes[family] = strikes;

            if (strikes < UnsatisfiedStrikeThreshold)
            {
                _host.Log($"[BuffDiag] batch audit: '{name}' (id={spellId}, fam={family}) was cast this batch but still reads inactive — strike {strikes}/{UnsatisfiedStrikeThreshold}.");
                continue;
            }

            _unsatisfiedStrikes.Remove(family);
            _buffFailCooldownUntil[family] = DateTime.Now + SilentNoShowCooldown;

            // Spell out the stored timer: the two causes that produce this look
            // identical from the outside. Either a lower-tier enchantment is
            // stuck in the family (storedLvl below the tier we cast), or
            // RebuffSecondsRemaining is set above this spell's own duration, so
            // it is "due for recast" the instant it lands. The numbers below
            // separate them at a glance.
            string stored = _ramBuffTimers.TryGetValue(family, out RamTimerInfo? t)
                ? $"stored='{t.SpellName}' lvl={t.SpellLevel} remainSec={(t.IsPermanent ? "permanent" : (t.Expiration - DateTime.Now).TotalSeconds.ToString("F0"))}"
                : "no timer entry";
            _host.Log($"[BuffDiag] batch audit: '{name}' (id={spellId}, fam={family}) lands but never reads active after {UnsatisfiedStrikeThreshold} batches — " +
                      $"parking {SilentNoShowCooldown.TotalMinutes:0}min so it can't drive continuous rebuff cycles. " +
                      $"{stored}, rebuffThresholdSec={_settings.RebuffSecondsRemaining}.");
        }
    }

    internal bool CheckVitals()   // internal for the host tests (VitalsTests)
    {
        int curHealthPct = _vitals.HealthPct;
        int curManaPct   = _vitals.ManaPct;
        int curStamPct   = _vitals.StaminaPct;

        // Reset per-tick recharge flags; they reflect "would recharge right now"
        // and are repopulated from the predicates below. The /ra buff snapshot
        // reads these for diagnostics.
        _isHealingSelf       = false;
        _isRechargingMana    = false;
        _isRechargingStamina = false;

        // A kit's heal hasn't landed yet: nothing else for vitals until it has (see KitPending).
        if (KitPending())
        {
            _isHealingSelf = true;
            Hold(_kitIsStamina ? "stamina kit in flight" : "healing kit in flight");
            return true;
        }

        // Emergency override regardless of mode, ahead of the configurable Heal At /
        // Top Off thresholds so even a "do nothing" recharge config still saves the
        // character. Two independent lines (each 0 = off):
        //   Emergency Heal At (EmergencyHealAt, default 30): healing kits (and, while a
        //     cast is pending, kits and potions - TryEmergencyHealWhilePending).
        //   Stamina To Health At (StaminaToHealthAt, default 30) with stamina over
        //     Stamina To Health Min Stamina (StaminaToHealthMinStamina, default 20):
        //     burn stamina for health with Stamina to Health Self.
        // Order:
        //   1. Out of Magic mode, a kit first: the spell needs a wand swap (seconds with
        //      no heal and no attack at critical health). Used when either line is crossed.
        //   2. Stamina to Health Self, when its own line and stamina floor allow.
        //   3. Otherwise fall through to the normal chain below (kit, Heal Self, potion)
        //      rather than giving up at critical health.
        if (TryEmergencyVitals(curHealthPct, curStamPct))
        {
            _isHealingSelf = true;
            return true;
        }

        // Pick the threshold set based on hunting state. A target within
        // MonsterRange = active combat → react at the LOW (HealAt / RestamAt /
        // GetManaAt) thresholds so we don't bloat cast traffic mid-fight.
        // Otherwise idle → top up to the HIGH (TopOffHP / TopOffStam /
        // TopOffMana) thresholds so we re-engage at full. Thresholds are
        // strict "<": set a value to 0 to disable that vital in that mode.
        bool inCombat = _combatManager?.HasCloseThreat(System.Math.Max(1, _settings.MonsterRange)) == true;
        int hpThreshold   = inCombat ? _settings.HealAt    : _settings.TopOffHP;
        int manaThreshold = inCombat ? _settings.GetManaAt : _settings.TopOffMana;
        int stamThreshold = inCombat ? _settings.RestamAt  : _settings.TopOffStam;

        // A flag is set only when something was actually done about the vital.
        // WantsVitalRecharge holds the arbiter on Buffing, above combat and nav, so
        // flagging a heal that can't happen — spell unknown, Life Magic untrained,
        // or parked after the server refused it (no components) — stalled the bot:
        // no route, no fighting back, until natural regen topped it up.
        // Kits, then spells, then potions (a consumable only when nothing free
        // works: no spell, Life Magic untrained, or out of components).
        if (curHealthPct < hpThreshold)
        {
            _vitalWhy = "";
            bool healed = AttemptHealthKitUse();
            string kitWhy = _vitalWhy;
            if (!healed) { _vitalWhy = ""; healed = AttemptVitalCast("Heal Self"); }
            string spellWhy = _vitalWhy;
            if (!healed) { _vitalWhy = ""; healed = AttemptPotion("HealthPotion"); }
            if (healed)
            {
                _isHealingSelf = true;
                return true;
            }
            _healUnavailableUntil = DateTime.Now.AddMilliseconds(HealUnavailableMs);
            Hold("nothing could heal", $"kit: {Why(kitWhy)}; Heal Self: {Why(spellWhy)}; potion: {Why(_vitalWhy)}");
            LogNoHeal($"health {curHealthPct}% < {(inCombat ? "Heal At" : "Top Off HP")} {hpThreshold}% but nothing healed: " +
                      $"kit: {Why(kitWhy)}; Heal Self: {Why(spellWhy)}; potion: {Why(_vitalWhy)}");
        }
        else if (!inCombat && curHealthPct < _settings.HealAt)
        {
            LogNoHeal($"health {curHealthPct}% < Heal At {_settings.HealAt}% but no monster within MonsterRange " +
                      $"{_settings.MonsterRange} yd, so the idle Top Off HP {hpThreshold}% applies");
        }
        // Reaching here means nothing healed this check. If health is still under a line
        // (e.g. an emergency line above Heal At whose kit failed), back off so WantsHealNow
        // doesn't hold the tick for a heal that can't happen.
        if (HealthUnderAnyLine(curHealthPct, curStamPct))
            _healUnavailableUntil = DateTime.Now.AddMilliseconds(HealUnavailableMs);

        if (curManaPct < manaThreshold
            && ((curStamPct > 15 && AttemptVitalCast("Stamina to Mana Self")) || AttemptPotion("ManaPotion")))
        {
            _isRechargingMana = true;
            return true;
        }
        // Stamina kits first, like healing kits for health: Lucy carried Greater Stamina Kits
        // and cast Revitalize/Robustification instead, because stamina never looked for a kit.
        if (curStamPct < stamThreshold && (AttemptStaminaKitUse() || AttemptVitalCast("Revitalize Self")
                                           || AttemptPotion("StaminaPotion", "Stamina")))
        {
            _isRechargingStamina = true;
            return true;
        }

        return false;
    }

    /// <summary>True when Stamina To Health At / Min Stamina allow Stamina to Health Self now.</summary>
    internal static bool StaminaToHealthAllowed(LegacyUiSettings s, int healthPct, int staminaPct)
        => s.StaminaToHealthAt > 0 && healthPct <= s.StaminaToHealthAt && staminaPct > s.StaminaToHealthMinStamina;

    /// <summary>The emergency step of CheckVitals (order in the comment there). True = handled.</summary>
    private bool TryEmergencyVitals(int curHealthPct, int curStamPct)
    {
        int emergencyAt = _settings.EmergencyHealAt;
        bool emergencyKit = emergencyAt > 0 && curHealthPct <= emergencyAt;
        bool stamToHealth = StaminaToHealthAllowed(_settings, curHealthPct, curStamPct);
        if (!emergencyKit && !stamToHealth) return false;

        // 1. Out of Magic mode a kit first (no wand swap at critical health).
        if (CurrentCombatMode != CombatMode.Magic && AttemptHealthKitUse()) return true;
        // 2. Stamina to Health Self, by its own line and stamina floor.
        if (stamToHealth && AttemptVitalCast("Stamina to Health Self")) return true;
        // 3. Not handled: the normal Heal At chain (kit, Heal Self, potion) gets its turn.
        return false;
    }

    private DateTime _lastPendingEmergencyHealAt = DateTime.MinValue;

    /// <summary>
    /// A cast awaiting confirmation holds the tick for up to 2.5 s (self-buff give-up)
    /// or 5 s (armor with no chat), and CheckVitals sits after that hold, so a character
    /// at critical health mid-batch got no kit or potion until the cast resolved. At
    /// Emergency Heal At (EmergencyHealAt, not Stamina To Health At), use a kit or potion (not a spell: that
    /// would replace the pending cast) once the server has finished the cast (UseDone)
    /// and the client isn't busy, so it doesn't draw "You're too busy!".
    /// </summary>
    private void TryEmergencyHealWhilePending()
    {
        int emergencyAt = _settings.EmergencyHealAt;
        if (emergencyAt <= 0 || _vitals.HealthPct > emergencyAt) return;
        if ((DateTime.Now - _lastPendingEmergencyHealAt).TotalMilliseconds < 2500) return;
        if (IsAwaitingCastResolution() || BusyHoldsHeal() || KitPending()) return;
        if (!CastGateWatchdog.CanCastNow(_host.CanCastNow, s => _host.Log(s))) return;
        if (AttemptHealthKitUse() || AttemptPotion("HealthPotion"))
        {
            _lastPendingEmergencyHealAt = DateTime.Now;
            _isHealingSelf = true;
        }
    }

    // Why the last kit / vital spell / potion attempt did nothing (the "no heal" log line).
    private string _vitalWhy = "";
    private DateTime _lastNoHealLogAt = DateTime.MinValue;

    private bool VitalNo(string why) { _vitalWhy = why; return false; }

    // The pending cast is a vital one when _pendingSpellId equals this (set by AttemptVitalCast).
    private int _pendingVitalSpellId;
    private DateTime _lastManaFloorLogAt = DateTime.MinValue;
    private static string Why(string why) => why.Length > 0 ? why : "-";

    /// <summary>
    /// One line every 10 s while health is low and nothing heals: which of kit, spell and
    /// potion was skipped and why (a mage stuck between Stamina to Health's 30% and Heal At
    /// healed nothing, and every skip returned silently, 2026-09-30).
    /// </summary>
    private void LogNoHeal(string line)
    {
        if ((DateTime.Now - _lastNoHealLogAt).TotalSeconds < 10) return;
        _lastNoHealLogAt = DateTime.Now;
        _host.Log("[RynthAi] Vitals: " + line);
    }

    private bool AttemptVitalCast(string baseName)
    {
        if (!IsSkillUsable(AcSkillType.LifeMagic)) return VitalNo("Life Magic not usable");
        // AC just said "You're too busy!": a cast now draws another refusal and can keep the
        // gesture from finishing (Drakkon, 2026-10-05: Stamina to Mana every 0.4 s until he was
        // moved). Mana and stamina wait out the backoff; a heal still retries, but no faster
        // than BusyHealRetryMs. Waiting counts as handled, so no potion is drunk meanwhile.
        if (DateTime.Now < _busyBackoffUntil)
        {
            bool healthSpell = baseName is "Heal Self" or "Stamina to Health Self";
            if (!healthSpell || (DateTime.Now - _lastCastAttempt).TotalMilliseconds < BusyHealRetryMs)
            {
                _vitalWhy = $"AC too busy, waiting {(_busyBackoffUntil - DateTime.Now).TotalMilliseconds:0} ms";
                _healWaitKind = "too-busy backoff";
                return true;
            }
        }
        // Mana and stamina also wait until the server has finished the last cast (its result, or
        // its UseDone after the incantation; bounded by its give-up time). A heal never waits.
        if (baseName is not ("Heal Self" or "Stamina to Health Self") && IsAwaitingCastResolution())
        {
            _vitalWhy = "the server hasn't finished the last cast";
            _healWaitKind = "server finishing the last cast";
            return true;
        }
        int spellId = FindBestSpellId(baseName, AcSkillType.LifeMagic);
        if (spellId == 0) return VitalNo($"no known '{baseName}' spell at the allowed tiers");
        // Parked after a hard server refusal (see OnChatWindowText) — the same
        // per-family cooldown the buff selectors honour.
        int family = SpellTableStub.GetById(spellId)?.Family ?? 0;
        if (family != 0
            && _buffFailCooldownUntil.TryGetValue(family, out DateTime coolUntil)
            && DateTime.Now < coolUntil)
            return VitalNo($"parked for {(coolUntil - DateTime.Now).TotalSeconds:0} s more after a refusal or no-show");
        if (CurrentCombatMode != CombatMode.Magic && DateTime.Now < _vitalSwapBlockedUntil)
            return VitalNo("wand swap blocked, retrying shortly");
        bool urgentHeal = baseName is "Heal Self" or "Stamina to Health Self" && HealIsUrgent(_vitals.HealthPct, _vitals.StaminaPct);
        if (!EnsureMagicMode(urgentHeal: urgentHeal))
        {
            if (!_magicModeBlocked) { _healWaitKind = "wand swap / Magic stance"; return true; }   // a swap step is in flight: yield the tick
            // The switch can't complete now (no room to put the bow away, the wand won't
            // wield): not handled, so the kit/potion after this gets its turn.
            _vitalSwapBlockedUntil = DateTime.Now.AddSeconds(VitalSwapBlockedRetrySec);
            return VitalNo("couldn't switch to Magic mode");
        }
        // A locally rejected cast sends nothing, so no chat or registry change will
        // ever come; setting pending anyway waited 2.5 s and counted a no-show
        // strike. Mirror the buff path: not handled, so the potion gets its turn.
        _pendingSpellId = spellId;
        _pendingVitalSpellId = spellId;   // instant: confirmed by chat, never by the registry
        _pendingVitalLabel = VitalLabel(baseName);
        bool castOk = _host.CastSpell((uint)_host.GetPlayerId(), spellId);
        _lastCastAttempt = DateTime.Now;
        if (!castOk)
        {
            _pendingSpellId = 0;
            return VitalNo($"the client refused the cast (spell {spellId})");
        }
        NoteCastIssued(spellId);
        if (_pendingVitalLabel == LabelHealing)
        {
            _healSpellIds.Add(spellId);
            _healLatency.HealSent(Now, baseName);
        }
        return true;
    }

    private static bool IsHealthPotionType(string[] types) =>
        Array.FindIndex(types, t => t.Equals("HealthPotion", StringComparison.OrdinalIgnoreCase)) >= 0;

    /// <summary>
    /// Drinks the first potion of <paramref name="types"/> from the Items panel.
    /// "Stamina" is the older name for stamina potions. Off with UsePotions.
    /// </summary>
    private bool AttemptPotion(params string[] types)
    {
        if (!_settings.UsePotions || _worldObjectCache == null) return VitalNo("Use Potions is off");
        if ((DateTime.Now - _lastPotionAt).TotalMilliseconds < 2500) return VitalNo("one drunk under 2.5 s ago");
        foreach (var rule in _settings.ConsumableRules)
        {
            if (Array.FindIndex(types, t => t.Equals(rule.Type, StringComparison.OrdinalIgnoreCase)) < 0) continue;
            if (_worldObjectCache[rule.Id] == null) continue;
            if (HeldCastBlocksItemUse(out string heldWhy)) return VitalNo(heldWhy);
            _host.Log($"[RynthAi] Vitals: drinking {rule.Name} ({rule.Type}).");
            _host.UseFor(unchecked((uint)rule.Id), "Buff", $"vitals: drink {rule.Name} ({rule.Type}) from the Items list");
            _lastPotionAt = DateTime.Now;
            _lastCastAttempt = DateTime.Now;
            if (IsHealthPotionType(types)) _healLatency.HealSent(Now, rule.Name);
            return true;
        }
        // Nothing on the Items panel: anything in the pack of that kind (Field Rations
        // weren't used unless someone added them by hand, 2026-09-29).
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
        {
            string? kind = RynthCore.Plugin.RynthAi.LegacyUi.LegacyWeaponsUi.PotionType(wo);
            if (kind == null || Array.FindIndex(types, t => t.Equals(kind, StringComparison.OrdinalIgnoreCase)) < 0) continue;
            if (HeldCastBlocksItemUse(out string heldWhy)) return VitalNo(heldWhy);
            _host.Log($"[RynthAi] Vitals: using {wo.Name} from the pack ({kind}).");
            _host.UseFor(unchecked((uint)wo.Id), "Buff", $"vitals: {kind} potion from the pack");
            _lastPotionAt = DateTime.Now;
            _lastCastAttempt = DateTime.Now;
            if (IsHealthPotionType(types)) _healLatency.HealSent(Now, wo.Name ?? "health potion");
            return true;
        }
        return VitalNo("none in the Items list or the pack");
    }

    // ── Buff items: unlimited gems that cast a buff when used ─────────────
    // name -> the spell ids that mean its buff is already on (Benediction and the
    // Lesser one give the same +10% max health, so either counts for both).
    private static readonly (string Name, uint[] Spells)[] BuffItems =
    {
        ("Asheron's Benediction",        new uint[] { 3810, 4024 }),
        ("Asheron's Lesser Benediction", new uint[] { 3810, 4024 }),
        ("Blackmoor's Favor",            new uint[] { 3811 }),
    };
    private readonly Dictionary<string, DateTime> _buffItemUsedAt = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastBuffItemCheck = DateTime.MinValue;

    /// <summary>
    /// Uses a buff item whose buff isn't on the player (Asheron's Benediction, Blackmoor's
    /// Favor). They were never used. One item per check, 3 s apart; 15 s before the same
    /// item again so its buff can land. Off with UseBuffItems.
    /// </summary>
    private void TryUseBuffItems()
    {
        if (!_settings.UseBuffItems || _worldObjectCache == null || !_host.HasReadPlayerEnchantments) return;
        if ((DateTime.Now - _lastBuffItemCheck).TotalMilliseconds < 3000) return;
        _lastBuffItemCheck = DateTime.Now;
        if (!_host.CanCastNow) return;

        var active = new HashSet<uint>();
        var sids = new uint[256];
        var exp = new double[256];
        int n = _host.ReadPlayerEnchantments(sids, exp, sids.Length);
        for (int i = 0; i < n && i < sids.Length; i++) active.Add(sids[i]);

        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
        {
            foreach (var (name, spells) in BuffItems)
            {
                if (!wo.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                bool on = false;
                foreach (uint s in spells) if (active.Contains(s)) { on = true; break; }
                if (on) break;
                if (_buffItemUsedAt.TryGetValue(name, out DateTime at) && (DateTime.Now - at).TotalSeconds < 15) break;
                _buffItemUsedAt[name] = DateTime.Now;
                // The same buff from the other Benediction counts as done too.
                foreach (var (other, os) in BuffItems) if (os == spells || os[0] == spells[0]) _buffItemUsedAt[other] = DateTime.Now;
                _host.Log($"[RynthAi] Buff item: using {wo.Name} (its buff isn't on).");
                _host.UseFor(unchecked((uint)wo.Id), "Buff", $"buff item: the buff of {wo.Name} is not on");
                _lastCastAttempt = DateTime.Now;
                return;
            }
        }
    }

    // ── Field rations: Cooking Pot on (Infinite) Dried Rations, 25 at a time ──
    private DateTime _lastRationAttempt = DateTime.MinValue, _rationRestUntil = DateTime.MinValue;
    private int _rationTries, _rationCountBefore = -1;

    /// <summary>
    /// Cooks field rations when the pack holds fewer than MakeRationsBelow: Elaborate from
    /// (Infinite) Elaborate Dried Rations, else Simple from Simple ones. Not in a fight.
    /// Three tries that don't raise the count (Cooking too low, missing pot) rest it 10 min.
    /// </summary>
    private void TryMakeRations()
    {
        int want = _settings.MakeRationsBelow;
        if (want <= 0 || _worldObjectCache == null || !_host.HasUseObjectOn) return;
        if (DateTime.Now < _rationRestUntil) return;
        if ((DateTime.Now - _lastRationAttempt).TotalSeconds < 6) return;
        if (_combatManager?.HasCloseThreat(Math.Max(1, _settings.MonsterRange)) == true) return;
        if (!_host.CanCastNow) return;

        int pot = 0, elaborateDried = 0, simpleDried = 0, elaborate = 0, simple = 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
        {
            string nm = wo.Name ?? "";
            int count = Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
            if (nm.Equals("Cooking Pot", StringComparison.OrdinalIgnoreCase)) pot = wo.Id;
            else if (nm.EndsWith("Elaborate Dried Rations", StringComparison.OrdinalIgnoreCase))
            { if (elaborateDried == 0 || nm.StartsWith("Infinite", StringComparison.OrdinalIgnoreCase)) elaborateDried = wo.Id; }
            else if (nm.EndsWith("Simple Dried Rations", StringComparison.OrdinalIgnoreCase))
            { if (simpleDried == 0 || nm.StartsWith("Infinite", StringComparison.OrdinalIgnoreCase)) simpleDried = wo.Id; }
            else if (nm.Equals("Elaborate Field Rations", StringComparison.OrdinalIgnoreCase)) elaborate += count;
            else if (nm.Equals("Simple Field Rations", StringComparison.OrdinalIgnoreCase)) simple += count;
        }
        if (pot == 0) return;
        int dried = elaborateDried != 0 ? elaborateDried : simpleDried;
        int have = elaborateDried != 0 ? elaborate : simple;
        if (dried == 0 || have >= want) { _rationTries = 0; _rationCountBefore = -1; return; }

        if (_rationCountBefore >= 0 && have <= _rationCountBefore) _rationTries++;
        else _rationTries = 0;
        if (_rationTries >= 3)
        {
            _rationTries = 0;
            _rationCountBefore = -1;
            _rationRestUntil = DateTime.Now.AddMinutes(10);
            _host.WriteToChat("[RynthAi] Couldn't cook field rations (3 tries). Is your Cooking high enough? Trying again in 10 minutes.", 2);
            return;
        }
        _rationCountBefore = have;
        _lastRationAttempt = DateTime.Now;
        _lastCastAttempt = DateTime.Now;
        _host.Log($"[RynthAi] Rations: {have} left (< {want}), cooking with the Cooking Pot.");
        _host.UseOnFor(unchecked((uint)pot), unchecked((uint)dried), "Buff", $"rations: {have} left, cooking more");
    }
    private DateTime _lastPotionAt = DateTime.MinValue;

    private bool AttemptHealthKitUse()
    {
        if (_worldObjectCache == null) return false;
        // VTank's kit options: skip kits in magic mode unless allowed; optionally
        // drop to peace mode first (kits work best out of combat stance).
        int mode = CurrentCombatMode;
        if (mode == CombatMode.Magic && !_settings.UseKitsInMagicMode) return VitalNo("in Magic mode (Use Kits in Magic Mode is off)");
        // The first kit on the Items panel that's still there, else the best healing kit in the
        // pack. Rules name one exact kit, which vanishes when its uses run out (Lucy carried
        // Peerless, Gifted and Adept kits with none listed and healed with spells, 2026-09-29).
        WorldObject? kit = null;
        bool fromPack = false;
        foreach (var r in _settings.ConsumableRules)
            if (r.Type.Equals("HealthKit", StringComparison.OrdinalIgnoreCase) && _worldObjectCache[r.Id] is WorldObject wo) { kit = wo; break; }
        if (kit == null) { kit = BestPackHealingKit(); fromPack = true; }
        if (kit == null) return VitalNo("no healing kit");
        if (KitRetryWait(out string kitWait)) return VitalNo(kitWait);
        // Too likely to fail on what's missing (KitMinSuccessPct): let the spell have it.
        uint missing = _vitals.MaxHealth > _vitals.CurrentHealth ? _vitals.MaxHealth - _vitals.CurrentHealth : 0;
        if (!KitChanceOk(kit.Id, kit.Name ?? "", missing, _settings.PeaceModeForKits)) return VitalNo($"{kit.Name} under Kit Min Success %");
        if (_settings.PeaceModeForKits && mode != CombatMode.NonCombat && _host.HasChangeCombatMode)
        {
            _host.ChangeCombatMode(CombatMode.NonCombat);
            _lastCastAttempt = DateTime.Now;
            _healWaitKind = "peace mode for the kit";
            return true;   // use the kit next tick, once in peace mode
        }

        uint playerId = _host.GetPlayerId();
        if (playerId == 0) return false;
        if (HeldCastBlocksItemUse(out string heldWhy)) return VitalNo(heldWhy);
        if (fromPack) _host.Log($"[RynthAi] Vitals: using {kit.Name} from the pack.");
        _host.UseOnFor(unchecked((uint)kit.Id), playerId, "Buff", "vitals: healing kit on yourself");
        _lastCastAttempt = DateTime.Now;
        MarkKitUsed();
        _healLatency.HealSent(Now, kit.Name ?? "healing kit");
        return true;
    }

    /// <summary>
    /// Uses the best stamina kit in the pack on the player, with the same rules as healing
    /// kits: not in Magic mode unless UseKitsInMagicMode, peace mode first if PeaceModeForKits.
    /// </summary>
    private bool AttemptStaminaKitUse()
    {
        if (_worldObjectCache == null || !_host.HasUseObjectOn) return false;
        int mode = CurrentCombatMode;
        if (mode == CombatMode.Magic && !_settings.UseKitsInMagicMode) return false;
        WorldObject? kit = BestPackStaminaKit();
        if (kit == null) return false;
        if (KitRetryWait(out _)) return false;
        uint missing = _vitals.MaxStamina > _vitals.CurrentStamina ? _vitals.MaxStamina - _vitals.CurrentStamina : 0;
        if (!KitChanceOk(kit.Id, kit.Name ?? "", missing, _settings.PeaceModeForKits)) return false;
        if (_settings.PeaceModeForKits && mode != CombatMode.NonCombat && _host.HasChangeCombatMode)
        {
            _host.ChangeCombatMode(CombatMode.NonCombat);
            _lastCastAttempt = DateTime.Now;
            return true;   // use the kit next tick, once in peace mode
        }
        uint playerId = _host.GetPlayerId();
        if (playerId == 0) return false;
        if (HeldCastBlocksItemUse(out _)) return false;
        _host.Log($"[RynthAi] Vitals: using {kit.Name} from the pack.");
        _host.UseOnFor(unchecked((uint)kit.Id), playerId, "Buff", "vitals: stamina kit on yourself");
        _lastCastAttempt = DateTime.Now;
        MarkKitUsed(stamina: true);
        return true;
    }

    // ── Kit result wait ──────────────────────────────────────────────────────
    // ACE (Healer.cs) updates the vital and says "You heal yourself for N ..." before UseDone
    // clears the busy state, but _vitals can lag that, so the next tick still saw low health
    // and used a second kit, every time (2026-10-03). After a kit, vitals wait for the
    // kit's own result line (heal, fail, movement disrupted, already at full), plus
    // KitLandGraceMs for the vitals to catch up, or KitResultTimeoutMs with no line at all.
    private DateTime _kitUsedAt = DateTime.MinValue;
    private DateTime _kitLandedAt = DateTime.MinValue;
    private const double KitResultTimeoutMs = 8000;
    private const double KitLandGraceMs = 700;

    // A refused kit (2026-10-05): ACE answers a kit used while the character is busy (a swing, a
    // cast still resolving), in the air, or on something it can't heal with a UseDone error and
    // no chat result (Healer.cs: YoureTooBusy, YouCantDoThatWhileInTheAir, YouCantHealThat). No
    // result line ever came, so every vital waited the full KitResultTimeoutMs, 8 s with no heal
    // at all (CheckVitals returns at KitPending, Heal Self included). Now the kit's refusal ends
    // the wait: a UseDone with an error after the kit went out, or "You're too busy!" within
    // KitRefusalWindowMs of it. The next kit waits KitRetryAfterRefusalMs, so Heal Self or a
    // potion gets the turn first and a refusal can't become a kit every tick.
    private int _kitSeqAtUse;
    private DateTime _kitRefusedAt = DateTime.MinValue;
    private const double KitRefusalWindowMs = 3000;
    private const double KitRetryAfterRefusalMs = 1000;
    /// <summary>Kit uses the server refused (tests, status).</summary>
    internal int KitRefusals { get; private set; }

    internal void MarkKitUsed(bool stamina = false)   // internal for the host tests (VitalsTests)
    {
        _kitUsedAt = DateTime.Now;
        _kitLandedAt = DateTime.MinValue;
        _kitIsStamina = stamina;
        _kitSeqAtUse = UseDoneSeqNow();
    }

    internal bool KitPending()
    {
        if (_kitUsedAt == DateTime.MinValue) return false;
        // Refused by the server: a UseDone carrying an error since the kit went out.
        if (_kitLandedAt == DateTime.MinValue && _host.TryGetLastUseDone(out int seq, out uint err)
            && seq != _kitSeqAtUse && err != 0)
        {
            KitRefused($"UseDone error 0x{err:X}");
            return false;
        }
        DateTime now = DateTime.Now;
        bool done = _kitLandedAt != DateTime.MinValue
            ? (now - _kitLandedAt).TotalMilliseconds >= KitLandGraceMs
            : (now - _kitUsedAt).TotalMilliseconds >= KitResultTimeoutMs;
        if (done) { _kitUsedAt = DateTime.MinValue; _kitLandedAt = DateTime.MinValue; }
        return !done;
    }

    /// <summary>The kit in flight was refused: no result line will come, so stop waiting for one.</summary>
    private void KitRefused(string why)
    {
        if (_kitUsedAt == DateTime.MinValue || _kitLandedAt != DateTime.MinValue) return;
        double ms = (DateTime.Now - _kitUsedAt).TotalMilliseconds;
        _kitUsedAt = DateTime.MinValue;
        _kitRefusedAt = DateTime.Now;
        KitRefusals++;
        _host.Log($"[RynthAi] Vitals: the {(_kitIsStamina ? "stamina" : "healing")} kit was refused {ms:0} ms after use ({why}): " +
                  $"no result will come, vitals carry on (next kit in {KitRetryAfterRefusalMs:0} ms).");
    }

    /// <summary>True (with why) while a kit must wait after its last refusal.</summary>
    private bool KitRetryWait(out string why)
    {
        double since = (DateTime.Now - _kitRefusedAt).TotalMilliseconds;
        if (since < KitRetryAfterRefusalMs)
        {
            why = $"kit refused {since:0} ms ago, retrying shortly";
            return true;
        }
        why = "";
        return false;
    }

    /// <summary>True for the chat lines that end a kit use on yourself (ACE Healer.cs).</summary>
    internal static bool IsKitResultLine(string lower) =>
        lower.Contains("heal yourself")                 // "You heal yourself for ...", "You critically heal ...", "You fail to heal yourself."
        || lower.Contains("disrupted healing")          // "Your movement disrupted healing!"
        || lower.Contains("is already at full");        // full health / stamina / mana

    // ── Kit success chance (VTank's "kit chance" option) ─────────────────────
    // ACE Healer.DoSkillCheck: (Healing + kit BoostValue) x (1.5 specialized, else 1.1)
    // against missing vital x 2 x (1.0 in peace mode, else 1.1), through
    // SkillCheck.GetSkillChance's 1 - 1/(1 + e^(0.03 x (skill - difficulty))).
    private const uint PropIntBoostValue = 90;

    // BoostValue by kit name, from the live world database (2026-09-30), for kits whose
    // property the client doesn't have (not identified). Unknown kits count as 0.
    private static readonly (string Name, int Boost)[] KitBoosts =
    {
        ("Light Infused Healing Kit", 250), ("Plentiful Healing Kit", 100), ("Gifted Healing Kit", 100),
        ("Adept Healing Kit", 75), ("Handy Healing Kit", 50), ("Treated Healing Kit", 25),
        ("Peerless Healing Kit", 20), ("Excellent Healing Kit", 10), ("Plain Healing Kit", 0),
        ("Medicated Stamina Kit", 500), ("Gauntlet Stamina Kit", 225), ("Greater Stamina Kit", 200),
        ("Lesser Stamina Kit", 100), ("Eternal Stamina Kit", 100),
    };
    private DateTime _lastKitChanceLog = DateTime.MinValue;

    /// <summary>
    /// True when a kit may be used: KitMinSuccessPct is 0, or the kit's chance of working on
    /// <paramref name="missing"/> points is at least that. Below it the caller falls through
    /// to the spell, as VTank does.
    /// </summary>
    private bool KitChanceOk(int kitId, string kitName, uint missing, bool willBePeace)
    {
        int minPct = _settings.KitMinSuccessPct;
        if (minPct <= 0 || _charSkills == null) return true;

        int boost = 0;
        if (_host.HasGetObjectIntProperty && _host.TryGetObjectIntProperty(unchecked((uint)kitId), PropIntBoostValue, out int b))
            boost = b;
        else
            foreach (var (n, v) in KitBoosts)
                if (kitName.Equals(n, StringComparison.OrdinalIgnoreCase)) { boost = v; break; }

        var healing = _charSkills[AcSkillType.Healing];
        double trainedMod = healing.Training == 3 ? 1.5 : 1.1;
        double combatMod = willBePeace || CurrentCombatMode == CombatMode.NonCombat ? 1.0 : 1.1;
        int skill = (int)Math.Round((Math.Max(0, healing.Buffed) + boost) * trainedMod);
        int difficulty = (int)Math.Round(missing * 2 * combatMod);
        double chance = 1.0 - 1.0 / (1.0 + Math.Exp(0.03 * (skill - difficulty)));
        int pct = (int)Math.Floor(Math.Clamp(chance, 0, 1) * 100);
        bool ok = pct >= minPct;
        if (!ok && (DateTime.Now - _lastKitChanceLog).TotalSeconds >= 10)
        {
            _lastKitChanceLog = DateTime.Now;
            _host.Log($"[RynthAi] Vitals: {kitName} would work {pct}% of the time on {missing} missing (skill {skill} vs {difficulty}), under {minPct}%: using the spell.");
        }
        return ok;
    }

    // Greater restores the most, Lesser the least.
    private WorldObject? BestPackStaminaKit()
    {
        if (_worldObjectCache == null) return null;
        WorldObject? best = null;
        int bestRank = int.MaxValue;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
        {
            string name = wo.Name ?? "";
            if (!name.EndsWith("Stamina Kit", StringComparison.OrdinalIgnoreCase)) continue;
            int rank = name.Contains("Greater", StringComparison.OrdinalIgnoreCase) ? 0
                     : name.Contains("Lesser", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            if (rank < bestRank) { best = wo; bestRank = rank; }
        }
        return best;
    }

    // Better kits heal more per use: the best one in the pack goes first.
    private static readonly string[] KitRanks = { "Peerless", "Excellent", "Gifted", "Adept", "Treated" };

    private WorldObject? BestPackHealingKit()
    {
        if (_worldObjectCache == null) return null;
        WorldObject? best = null;
        int bestRank = int.MaxValue;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
        {
            string name = wo.Name ?? "";
            if (!name.EndsWith("Healing Kit", StringComparison.OrdinalIgnoreCase)) continue;
            int rank = Array.FindIndex(KitRanks, k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (rank < 0) rank = name.Contains("Crude", StringComparison.OrdinalIgnoreCase) ? KitRanks.Length + 1 : KitRanks.Length;
            if (rank < bestRank) { best = wo; bestRank = rank; }
        }
        return best;
    }

    /// <summary>
    /// Returns true if any self-buff in the current buff list is missing/expired,
    /// without actually casting. Used by NeedToBuff meta condition.
    /// </summary>
    public bool NeedsAnyBuff()
    {
        if (!_settings.EnableBuffing) return false;
        return _isForceRebuffing || AnyBuffBelowThreshold(BuildDynamicBuffList(), _settings.RebuffSecondsRemaining);
    }

    // Returns true if any castable buff in the list has < thresholdSec remaining.
    // Used both as the arbiter query (NeedsAnyBuff) and as the batch-rebuff trigger
    // inside CheckAndCastSelfBuffs — keeps the two in sync on when buffing is needed.
    // Families currently parked in _buffFailCooldownUntil are skipped so a parked
    // family can't keep retriggering the batch (which would otherwise loop:
    // batch starts → clears cooldown → cast again → silent no-show → re-park).
    private bool AnyBuffBelowThreshold(List<string> desiredBuffs, int thresholdSec)
    {
        DateTime now = DateTime.Now;
        foreach (string buffBaseName in desiredBuffs)
        {
            AcSkillType castSkill = SkillForBuff(buffBaseName);
            if (!IsSkillUsable(castSkill)) continue;
            int spellId = FindBestSpellId(buffBaseName, castSkill);
            if (spellId == 0) continue;
            int family = SpellTableStub.GetById(spellId)?.Family ?? 0;
            if (family != 0
                && _buffFailCooldownUntil.TryGetValue(family, out DateTime coolUntil)
                && now < coolUntil)
                continue;
            if (!IsBuffActive(spellId, thresholdSec)) return true;
        }
        return false;
    }

    private bool CheckAndCastSelfBuffs()
    {
        // Auto batch-rebuff: when any buff drops below the configured threshold,
        // trigger a full rebuff so ALL timers re-align in one session rather than
        // each buff trickling in one at a time as it individually expires.
        if (!_isForceRebuffing && AnyBuffBelowThreshold(BuildDynamicBuffList(), _settings.RebuffSecondsRemaining))
        {
            // Batch trigger — reuse FR family-tracking but do NOT clear timers.
            // ForceFullRebuff clears _ramBuffTimers, which causes an immediate
            // re-trigger: any spell that can't be resolved (spellId=0) never
            // gets a timer recorded, so the next AnyBuffBelowThreshold call sees
            // "no timer = below threshold" and loops forever.
            // Also do NOT clear _buffFailCooldownUntil here. Auto-batch must
            // respect parked families (chat hard-rejects + silent no-shows);
            // only an explicit ForceFullRebuff resets them. Without this,
            // /god kicks off auto-batch → cooldown cleared → cast → silent
            // no-show → re-park → another auto-batch → loop. Hard-reject
            // cooldowns are short-lived (120s) so they expire on their own.
            _isForceRebuffing = true;
            _isAutoBatchRebuff = true;
            _forceRebuffCastFamilies.Clear();
        }

        List<string> desiredBuffs = BuildDynamicBuffList();
        bool diagnose = _isForceRebuffing;

        foreach (string buffBaseName in desiredBuffs)
        {
            AcSkillType castSkill = SkillForBuff(buffBaseName);
            if (!IsSkillUsable(castSkill))
            {
                LastBuffSkipReason = $"skill not usable: {buffBaseName} ({castSkill})";
                if (diagnose) _host.Log($"[FR] skip '{buffBaseName}' — skill {castSkill} not usable");
                continue;
            }

            int spellId = FindBestSpellId(buffBaseName, castSkill);
            if (spellId == 0)
            {
                LastBuffSkipReason = $"not known / unresolvable: {buffBaseName}";
                if (diagnose) _host.Log($"[FR] skip '{buffBaseName}' — FindBestSpellId returned 0");
                continue;
            }

            if (IsBuffActive(spellId))
            {
                LastBuffSkipReason = $"already active: {buffBaseName} (id={spellId})";
                if (diagnose) _host.Log($"[FR] skip '{buffBaseName}' (id={spellId}) — already active");
                continue;
            }

            // Skip a family the server just hard-rejected (no components, etc.)
            // so an uncastable buff isn't re-cast every cycle (→ AC AV). Auto-
            // recovers when the cooldown expires or a cast of it later confirms.
            int buffFamily = SpellTableStub.GetById(spellId)?.Family ?? 0;
            if (buffFamily != 0
                && _buffFailCooldownUntil.TryGetValue(buffFamily, out DateTime coolUntil)
                && DateTime.Now < coolUntil)
            {
                LastBuffSkipReason = $"fail-cooldown: {buffBaseName} (fam={buffFamily}, {(coolUntil - DateTime.Now).TotalSeconds:0}s)";
                if (diagnose) _host.Log($"[FR] skip '{buffBaseName}' (id={spellId}) — hard-fail cooldown {(coolUntil - DateTime.Now).TotalSeconds:0}s");
                continue;
            }

            if (!EnsureMagicMode(forBuff: true))
            {
                if (_wandSwapExhausted)
                {
                    // FIX: the bow->wand swap provably can't succeed right now (no open
                    // pack / repeated fails). Park THIS family in the existing per-family
                    // cooldown so AnyBuffBelowThreshold + this selector skip it ->
                    // NeedsAnyBuff() goes false -> the arbiter stops picking Buffing
                    // -> combat runs. Buffing stays top priority; only the impossible
                    // family is shelved, time-boxed, and auto-retried.
                    if (buffFamily != 0)
                        _buffFailCooldownUntil[buffFamily] = DateTime.Now.AddSeconds(BuffFailCooldownSec);
                    LastBuffSkipReason = $"wand-swap exhausted: {buffBaseName} (fam={buffFamily})";
                    _host.Log($"[WieldGate] wand-swap exhausted for '{buffBaseName}' (fam={buffFamily}) — parking {BuffFailCooldownSec:0}s, fighting unbuffed");
                    continue;
                }
                LastBuffSkipReason = "magic-mode swap in progress (yielding tick)";
                return true; // normal in-progress swap — yield the tick (NeedsAnyBuff still holds Buffing)
            }

            _pendingSpellId = spellId;
            if (buffFamily != 0)
            {
                _lastCastSpellIdByFamily[buffFamily] = spellId;
                _castIssuedAtByFamily[buffFamily]    = DateTime.Now;
                _expiryAtCastByFamily[buffFamily]    = CurrentExpiryFor(buffFamily);
            }

            var spellInfo = SpellTableStub.GetById(spellId);
            if (spellInfo != null)
                _host.WriteToChat($"[RynthAi] Casting: {spellInfo.Name}", 5);
            // NOTE: timers are recorded ONLY on chat confirmation ("you cast X"),
            // never optimistically here. An optimistic item-timer at cast time
            // left a phantom 1h "active" buff whenever a cast silently failed
            // (e.g. an unknown higher tier), so the bot thought armor was
            // buffed when it wasn't and never retried the known tier.

            if (diagnose) _host.Log($"[FR] CAST '{buffBaseName}' resolvedSpellId={spellId} (pending now set)");
            bool castOk = _host.CastSpell((uint)_host.GetPlayerId(), spellId);
            _lastCastAttempt = DateTime.Now;
            if (castOk) NoteCastIssued(spellId);
            if (castOk && buffFamily != 0 && spellInfo != null)
            {
                // Highest tier cast per family this session: IsBuffActive's landed-tier cap only
                // applies once the target tier has been tried.
                int castLevel = GetSpellLevel(spellInfo);
                if (!_familyAttemptedTier.TryGetValue(buffFamily, out int prevAttempt) || castLevel > prevAttempt)
                    _familyAttemptedTier[buffFamily] = castLevel;
            }
            if (!castOk)
            {
                // Local CastSpell rejected — packet never went out, so no chat
                // will ever arrive. Clear the gate immediately so the cycle
                // doesn't hang on this spell. (No timer to undo — we no longer
                // record optimistically.)
                _pendingSpellId = 0;
                LastBuffSkipReason = $"local CastSpell rejected: {buffBaseName} (id={spellId})";
                if (diagnose) _host.Log($"[FR] cast '{buffBaseName}' returned false — pending cleared");
            }
            return true;
        }
        return false;
    }

    private int FindBestSpellId(string baseName, AcSkillType skill)
        => _spellManager.GetDynamicSelfBuffId(baseName, skill);

    // True for enchantments that live on an ITEM (armor or weapon), NOT on the
    // player. These do NOT appear in ReadPlayerEnchantments, so they must be
    // confirmed via chat, stored in _itemSpellTimers, and PRESERVED across a
    // RefreshFromLiveMemory (which rebuilds player buffs from the live registry
    // and would otherwise wipe them → perpetual recast = "buffing in circles").
    // Matches base names AND tier-7 lore / "Aura of" forms.
    internal static bool IsItemEnchantment(string name)
    {
        string[] itemSpells = {
            // Armor: Impenetrability + Brogard's, and the elemental/physical Banes
            "Impenetrability", "Brogard's Defiance", "Acid Bane", "Olthoi's Bane",
            "Blade Bane", "Swordsman's Bane", "Swordman's Bane", "Bludgeoning Bane", "Bludgeon Bane", "Tusker's Bane",
            "Flame Bane", "Inferno's Bane", "Frost Bane", "Gelidite's Bane",
            "Lightning Bane", "Astyrrian's Bane", "Piercing Bane", "Archer's Bane",
            // Weapon auras: base names cover "Aura of X Self N" / "Incantation of X Self";
            // the explicit lore names cover the tier-7 forms that drop the base word.
            "Blood Drinker", "Aura of Infected Caress",
            "Hermetic Link", "Aura of Mystic's Blessing",
            "Heart Seeker", "Aura of Elysa's Sight",
            "Spirit Drinker", "Aura of Infected Spirit Carress", "Aura of Infected Spirit Caress",
            "Swift Killer", "Aura of Atlan's Alacrity",
            "Defender", "Aura of Cragstone's Will",
        };
        foreach (string s in itemSpells)
            if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>
    /// Called immediately when CheckAndCastSelfBuffs decides to cast an item spell.
    /// Records the timer optimistically — removed on fizzle.
    /// </summary>
    private void RecordItemSpellCast(SpellInfo spellInfo)
    {
        int level = GetSpellLevel(spellInfo);
        double duration = GetCustomSpellDuration(level);
        var now = DateTime.Now;
        _itemSpellTimers[spellInfo.Family] = new ItemSpellRecord
        {
            CastAt    = now,
            ExpiresAt = now.AddSeconds(duration),
            SpellName = spellInfo.Name,
            SpellLevel = level,
        };
        // Pair with [BuffDiag] armor-recast logs so we can match record vs lookup.
        _lastArmorRecastReason.Remove(spellInfo.Family); // allow next recast to re-log
        _host.Log($"[BuffDiag] RECORD '{spellInfo.Name}' (id={spellInfo.Id}, fam={spellInfo.Family}, lvl={level}, durSec={duration:F0}, expiresAt={now.AddSeconds(duration):HH:mm:ss})");
        SaveBuffTimers();

        if (EnableCastRegistryDiagnostic)
            CapturePreCastDiagnostic(spellInfo);
    }

    private void CapturePreCastDiagnostic(SpellInfo spellInfo)
    {
        try
        {
            var diag = new CastDiagnostic
            {
                CastedAt    = DateTime.Now,
                SpellName   = spellInfo.Name,
                SpellId     = spellInfo.Id,
                SpellFamily = spellInfo.Family,
                PreSnapshots = SnapshotRegistries(),
            };
            _pendingDiagnostics.Enqueue(diag);
            _host.Log($"[BuffTest] PRE-CAST '{spellInfo.Name}' (id={spellInfo.Id}, fam={spellInfo.Family}) " +
                      $"snapshots={diag.PreSnapshots.Count} queued={_pendingDiagnostics.Count}");
        }
        catch (Exception ex)
        {
            _host.Log($"[BuffTest] Pre-cast snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private List<RegistrySnapshot> SnapshotRegistries()
    {
        var snaps = new List<RegistrySnapshot>();
        double serverNow = _host.HasGetServerTime ? _host.GetServerTime() : 0;

        // Player registry — known-safe path.
        if (_host.HasReadPlayerEnchantments)
        {
            const int Max = 256;
            var sids = new uint[Max];
            var exp  = new double[Max];
            int n = _host.ReadPlayerEnchantments(sids, exp, Max);
            snaps.Add(new RegistrySnapshot
            {
                OwnerId     = (uint)_host.GetPlayerId(),
                OwnerName   = "Player",
                IsPlayer    = true,
                SpellIds    = sids,
                ExpiryTimes = exp,
                Count       = Math.Max(0, n),
                ServerTime  = serverNow,
            });
        }

        // Equipped-item registries — opt-in via the diagnostic flag.
        // Equipped items are stable in the cache (filter avoids the freshly-arrived
        // inventory items that triggered the AV in earlier RefreshEquippedItemEnchantments runs).
        if (_worldObjectCache != null && _host.HasReadObjectEnchantments)
        {
            var equipped = new List<WorldObject>();
            foreach (var item in _worldObjectCache.GetDirectInventory())
            {
                if (item.WieldedLocation == 0) continue;
                equipped.Add(item);
            }

            const int Max = 64;
            foreach (var item in equipped)
            {
                uint oid = unchecked((uint)item.Id);
                var sids = new uint[Max];
                var exp  = new double[Max];
                int n;
                try
                {
                    n = _host.ReadObjectEnchantments(oid, sids, exp, Max);
                }
                catch
                {
                    continue;
                }
                snaps.Add(new RegistrySnapshot
                {
                    OwnerId     = oid,
                    OwnerName   = item.Name,
                    IsPlayer    = false,
                    SpellIds    = sids,
                    ExpiryTimes = exp,
                    Count       = Math.Max(0, n),
                    ServerTime  = serverNow,
                });
            }
        }

        return snaps;
    }

    private void LogPostCastDiff(CastDiagnostic diag)
    {
        try
        {
            var post = SnapshotRegistries();
            _host.Log($"[BuffTest] === POST-CAST DIFF for '{diag.SpellName}' (id={diag.SpellId}, fam={diag.SpellFamily}) ===");
            _host.WriteToChat($"[BuffTest] Post-cast diff captured for '{diag.SpellName}' — see RynthCore.log", 1);

            foreach (var pre in diag.PreSnapshots)
            {
                RegistrySnapshot? match = null;
                foreach (var p in post)
                    if (p.OwnerId == pre.OwnerId) { match = p; break; }

                if (match == null)
                {
                    _host.Log($"[BuffTest]   {pre.OwnerName} (0x{pre.OwnerId:X8}): post-snapshot missing");
                    continue;
                }

                var preIds = new HashSet<uint>();
                for (int i = 0; i < pre.Count; i++) preIds.Add(pre.SpellIds[i]);

                int newCount = 0;
                int matchedTargetSpell = -1;
                _host.Log($"[BuffTest]   {match.OwnerName} (0x{match.OwnerId:X8}): pre={pre.Count} post={match.Count} serverNow={match.ServerTime:F1}");

                for (int i = 0; i < match.Count; i++)
                {
                    uint sid = match.SpellIds[i];
                    if (preIds.Contains(sid)) continue;
                    newCount++;
                    var sp = SpellTableStub.GetById((int)sid);
                    string nm = sp?.Name ?? "?";
                    int fam = sp?.Family ?? -1;
                    double remaining = match.ExpiryTimes[i] - match.ServerTime;
                    _host.Log($"[BuffTest]     +new spell={sid} ('{nm}') fam={fam} expiry={match.ExpiryTimes[i]:F1} remaining={remaining:F1}s");
                    if (sid == (uint)diag.SpellId || fam == diag.SpellFamily) matchedTargetSpell = i;
                }

                if (newCount == 0)
                    _host.Log($"[BuffTest]     (no new entries)");
                else if (matchedTargetSpell >= 0)
                {
                    double remaining = match.ExpiryTimes[matchedTargetSpell] - match.ServerTime;
                    bool hasDuration = remaining > 0.5 && remaining < (86400 * 365);
                    _host.Log($"[BuffTest]     >>> TARGET SPELL MATCHED: remaining={remaining:F1}s hasRealDuration={hasDuration}");
                }
            }

            _host.Log("[BuffTest] === END DIFF ===");
        }
        catch (Exception ex)
        {
            _host.Log($"[BuffTest] Post-cast diff failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool IsBuffActive(int spellId, int? rebufferSecOverride = null)
    {
        var targetSpell = SpellTableStub.GetById(spellId);
        if (targetSpell == null) return false;

        // Force Rebuff: ignore live timers entirely — only spells we've already
        // cast THIS rebuff cycle count as "active". The whole point of FR is to
        // recast every spell so all timers align to the same start time.
        // The automatic batch shares FR's bookkeeping but must NOT recast
        // everything: one buff under the threshold used to refresh every buff,
        // even ones with 30+ minutes left (2026-09-27). It tops off only buffs
        // under RebuffTopOffSecondsRemaining (never less than RebuffSecondsRemaining,
        // so the buff that started the batch is always in it) and leaves the rest
        // to their own timers.
        if (_isForceRebuffing)
        {
            if (_forceRebuffCastFamilies.Contains(targetSpell.Family)) return true;
            if (!_isAutoBatchRebuff) return false;
            rebufferSecOverride = Math.Max(_settings.RebuffTopOffSecondsRemaining, _settings.RebuffSecondsRemaining);
        }

        int targetLevel = GetSpellLevel(targetSpell);
        // Recast when remaining duration drops below this many seconds.
        // User-configurable via Advanced Settings → Buffing; overridden during
        // batch-rebuff passes (CheckAndCastSelfBuffs) by RebuffTopOffSecondsRemaining.
        int rebufferSec = Math.Max(0, rebufferSecOverride ?? _settings.RebuffSecondsRemaining);

        // Tier-upgrade rule (applies to both item enchantments and player buffs):
        // if a better-tier spell is now available than what's currently active,
        // treat the buff as inactive and recast immediately — regardless of how
        // much time is left. Silent: callers like NeedsAnyBuff poll IsBuffActive
        // every tick, so logging here would spam. The actual cast announces
        // itself via the existing "Casting: X" line in CheckAndCastSelfBuffs.

        // Item spells (armor banes, Impenetrability) tracked in their own dictionary.
        if (IsItemEnchantment(targetSpell.Name))
        {
            if (_itemSpellTimers.TryGetValue(targetSpell.Family, out ItemSpellRecord? itemTimer))
            {
                if (itemTimer.SpellLevel < targetLevel)
                {
                    LogArmorRecast(targetSpell, $"tier-upgrade recordedLvl={itemTimer.SpellLevel} < targetLvl={targetLevel} (recorded='{itemTimer.SpellName}')");
                    return false;
                }
                if (DateTime.Now < itemTimer.ExpiresAt.AddSeconds(-rebufferSec))
                {
                    _lastArmorRecastReason.Remove(targetSpell.Family); // clear so next recast re-logs
                    return true;
                }
                double remainSec = (itemTimer.ExpiresAt - DateTime.Now).TotalSeconds;
                LogArmorRecast(targetSpell, $"expiry-window remainSec={remainSec:F0} threshold={rebufferSec} (recorded='{itemTimer.SpellName}' lvl={itemTimer.SpellLevel} expiresAt={itemTimer.ExpiresAt:HH:mm:ss})");
            }
            else
            {
                LogArmorRecast(targetSpell, $"NO timer entry for family={targetSpell.Family} (targetLvl={targetLevel}, _itemSpellTimers.Count={_itemSpellTimers.Count})");
            }
            return false;
        }

        // Player buffs — use RAM timers. With CastBuffsOverItemBuffs on, a permanent
        // (item-granted) entry is not ours and never satisfies the family. The live refresh
        // already keeps permanents out of the timers then; this covers the setting being
        // switched on between refreshes.
        if (_ramBuffTimers.TryGetValue(targetSpell.Family, out RamTimerInfo? timer)
            && !(timer.IsPermanent && _settings.CastBuffsOverItemBuffs))
        {
            // Tier-upgrade flap guard: many high-tier buffs are Incantations
            // (nominal tier 8) that LAND at a lower, skill-capped tier — e.g.
            // "Incantation of Sprint Self" (target 8) lands as "Sprint Self VI"
            // (6). Comparing the landed 6 against the nominal 8 makes the
            // upgrade rule fire forever → endless recast. Cap the target at the
            // highest tier we've actually observed land for this family, so once
            // the Incantation lands at its real ceiling we stop chasing 8.
            // The cap only counts once this session has actually CAST the target tier (or
            // higher) for the family and seen it land lower. A family bootstrapped at a lower
            // tier on purpose (Creature Enchantment Mastery / Focus / Willpower cast at 7 so the
            // skill reaches 8) landed at exactly what was cast; capping it there kept those
            // three at 7 while everything after them went to 8. No attempt recorded (fresh
            // login) keeps the cap, so a relog doesn't recast every capped Incantation.
            int effectiveTarget = targetLevel;
            bool triedLowerOnly = _familyAttemptedTier.TryGetValue(targetSpell.Family, out int attempted) && attempted < targetLevel;
            if (!triedLowerOnly
                && _familyAchievedTier.TryGetValue(targetSpell.Family, out int achieved) && achieved < effectiveTarget)
                effectiveTarget = achieved;

            if (timer.SpellLevel < effectiveTarget)
            {
                LogPlayerRecast(targetSpell, $"tier-upgrade storedLvl={timer.SpellLevel} < effTarget={effectiveTarget} (nominal={targetLevel}, achievedCap={(_familyAchievedTier.TryGetValue(targetSpell.Family, out int a2) ? a2 : -1)}, stored='{timer.SpellName}')");
                return false;
            }
            if (DateTime.Now < timer.Expiration.AddSeconds(-rebufferSec))
            {
                _lastArmorRecastReason.Remove(targetSpell.Family);
                return true;
            }
            // The strongest entry is running out, but another one in the family at the
            // tier we'd cast (or better) still has time: AC moves to it when the strong one
            // ends, so casting again changes nothing. Without this a buff bot's expiring 8
            // made our own fresh VI under it recast every pass until the audit parked it.
            if (CoveredByAnotherEntry(targetSpell.Family, effectiveTarget, rebufferSec))
            {
                _lastArmorRecastReason.Remove(targetSpell.Family);
                return true;
            }
            double remainSec = (timer.Expiration - DateTime.Now).TotalSeconds;
            LogPlayerRecast(targetSpell, $"expiry-window remainSec={remainSec:F0} threshold={rebufferSec} storedLvl={timer.SpellLevel} exp={timer.Expiration:HH:mm:ss} (stored='{timer.SpellName}')");
        }
        else if (timer != null)
        {
            LogPlayerRecast(targetSpell, $"only an item-granted '{timer.SpellName}' (permanent, lvl={timer.SpellLevel}) and CastBuffsOverItemBuffs is on: cast our own");
        }
        else
        {
            LogPlayerRecast(targetSpell, $"NO ram-timer entry for family={targetSpell.Family} (targetLvl={targetLevel}, _ramBuffTimers.Count={_ramBuffTimers.Count})");
        }

        if (_isForceRebuffing) return false;

        return false;
    }

    /// <summary>True when the last live read holds an entry in <paramref name="family"/> at
    /// <paramref name="minLevel"/> or above with more than <paramref name="rebufferSec"/> left
    /// (a permanent one counts unless CastBuffsOverItemBuffs is on: then only our timed
    /// entries do).</summary>
    private bool CoveredByAnotherEntry(int family, int minLevel, int rebufferSec)
    {
        if (!_familyLiveEntries.TryGetValue(family, out var entries)) return false;
        bool ignorePermanent = _settings.CastBuffsOverItemBuffs;
        DateTime due = DateTime.Now.AddSeconds(rebufferSec);
        foreach (var (level, expiration, permanent) in entries)
            if (level >= minLevel && expiration > due && !(permanent && ignorePermanent))
                return true;
        return false;
    }

    // Player-buff counterpart to LogArmorRecast — explains why a character
    // self-buff (Impregnability/Aura of Deflection, masteries, etc.) is judged
    // inactive. Throttled per-family (IsBuffActive is polled every tick).
    private void LogPlayerRecast(SpellInfo targetSpell, string reason)
    {
        if (_lastArmorRecastReason.TryGetValue(targetSpell.Family, out string? prev) && prev == reason)
            return;
        _lastArmorRecastReason[targetSpell.Family] = reason;
        _host.Log($"[BuffDiag] player-recast '{targetSpell.Name}' (id={targetSpell.Id}, fam={targetSpell.Family}): {reason}");
    }

    /// <summary>
    /// Remember the highest tier a family has actually LANDED at (read from the
    /// live registry, never from the cast name — an Incantation echoes its
    /// nominal tier in chat but lands skill-capped). Capping the upgrade target
    /// at this in IsBuffActive stops the endless "tier 6 < tier 8" recast flap.
    /// </summary>
    private void RecordAchievedTier(int family, int landedLevel)
    {
        if (landedLevel <= 0) return;
        if (!_familyAchievedTier.TryGetValue(family, out int cur) || landedLevel > cur)
            _familyAchievedTier[family] = landedLevel;
    }

    private void LogArmorRecast(SpellInfo targetSpell, string reason)
    {
        // Throttle: only log when reason changes for this family — IsBuffActive
        // is polled every tick, so unconditional logging would flood RynthCore.log.
        if (_lastArmorRecastReason.TryGetValue(targetSpell.Family, out string? prev) && prev == reason)
            return;
        _lastArmorRecastReason[targetSpell.Family] = reason;
        _host.Log($"[BuffDiag] armor-recast '{targetSpell.Name}' (id={targetSpell.Id}, fam={targetSpell.Family}): {reason}");
    }

    internal static int GetSpellLevel(SpellInfo spell)
    {
        string n = spell.Name;
        // "Aura of Incantation of Blood Drinker Self" etc. are tier 8 too (5400 s); they
        // fell to the "Aura of" rule below, got a 3600 s timer and recast 30 min early.
        if (n.StartsWith("Incantation") || n.Contains("Incantation of")) return 8;
        if (n.Contains(" VIII")) return 8;  // must precede " VII" — "X VIII".Contains(" VII") is true
        if (n.Contains(" VII")) return 7;
        if (n.Contains(" VI")) return 6;
        if (n.Contains(" V")) return 5;
        if (n.Contains(" IV")) return 4;
        if (n.Contains(" III")) return 3;
        if (n.Contains(" II")) return 2;
        if (n.EndsWith(" I") || n.Contains(" I ")) return 1;

        if (n.Contains("Mastery") || n.Contains("Blessing") || n.Contains("Aura of") ||
            n.Contains("Intervention") || n.Contains("Trance") || n.Contains("Recovery") ||
            n.Contains("Robustify") || n.Contains("Persistence") || n.Contains("Robustification") ||
            n.Contains("Might of the Lugians") || n.Contains("Preservance") || n.Contains("Perseverance") ||
            n.Contains("Honed Control") || n.Contains("Hastening") || n.Contains("Inner Calm") ||
            n.Contains("Mind Blossom") || n.Contains("Infected Caress") || n.Contains("Elysa's Sight") ||
            n.Contains("Infected Spirit") || n.Contains("Atlan's Alacrity") || n.Contains("Cragstone's Will") ||
            n.Contains("Brogard's Defiance") || n.Contains("Olthoi's Bane") || n.Contains("Swordsman's Bane") ||
            n.Contains("Swordman's Bane") || n.Contains("Tusker's Bane") || n.Contains("Inferno's Bane") ||
            n.Contains("Gelidite's Bane") || n.Contains("Astyrrian's Bane") || n.Contains("Archer's Bane"))
            return 7;

        return 1;
    }

    private int _archmageAugs = -1;      // -1 = not read yet
    private long _archmageAugsReadAt;

    /// <summary>
    /// Ranks of Archmage's Endurance (AUGMENTATION_INCREASED_SPELL_DURATION, 5x,
    /// +20% enchantment duration each). Was hardcoded to 0, so every duration on an
    /// augmented character was 20-100% short and armor banes/auras recast early
    /// (2026-06-03 audit P1#3).
    ///
    /// Fails closed to 0 — the previous behaviour — whenever the property can't be
    /// read, and clamps to the game's 5-rank cap so a bad read can't inflate a
    /// duration past 2x and leave the character silently unbuffed.
    /// </summary>
    private int GetArchmageEnduranceCount()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Augs change at most once per purchase; re-read occasionally so buying one
        // mid-session takes effect without paying for a property read per call.
        if (_archmageAugs >= 0 && now - _archmageAugsReadAt < 60000) return _archmageAugs;

        if (!_host.HasGetObjectIntProperty) return _archmageAugs > 0 ? _archmageAugs : 0;

        uint playerId = _host.GetPlayerId();
        if (playerId == 0) return _archmageAugs > 0 ? _archmageAugs : 0;

        if (!_host.TryGetObjectIntProperty(
                playerId, (uint)LongValueKey.AugmentationIncreasedSpellDuration, out int augs))
            return _archmageAugs > 0 ? _archmageAugs : 0;

        augs = Math.Clamp(augs, 0, 5);
        if (augs != _archmageAugs)
            _host.Log($"[Buff] Archmage's Endurance rank {augs}/5 — enchantment durations x{1.0 + augs * 0.20:0.00}");

        _archmageAugs       = augs;
        _archmageAugsReadAt = now;
        return augs;
    }

    internal double GetCustomSpellDuration(int spellLevel)
    {
        double baseSeconds = 1800;
        if (spellLevel == 6) baseSeconds = 2700;
        else if (spellLevel == 7) baseSeconds = 3600;
        else if (spellLevel == 8) baseSeconds = 5400;

        int augs = GetArchmageEnduranceCount();
        return baseSeconds * (1.0 + (augs * 0.20));
    }

    /// <summary>
    /// Reads active enchantments directly from client memory and refreshes _ramBuffTimers.
    /// Requires both HasReadPlayerEnchantments and HasGetServerTime to be available.
    /// Returns the number of timers updated, or -1 if the API is unavailable.
    /// </summary>
    public int RefreshFromLiveMemory()
    {
        if (!_host.HasReadPlayerEnchantments || !_host.HasGetServerTime)
            return -1;

        double serverNow = _host.GetServerTime();
        if (serverNow <= 0)
            return -1; // No time sync received yet

        const int MaxEnchantments = 512;
        uint[] spellIds    = new uint[MaxEnchantments];
        double[] expiryTimes = new double[MaxEnchantments];

        int count = _host.ReadPlayerEnchantments(spellIds, expiryTimes, MaxEnchantments);
        if (count < 0)
            return -1;

        // Item/weapon spells (Blood Drinker, Heart Seeker, banes...) are tracked from their
        // cast lines, not this registry, so a death - which wipes every enchantment at once -
        // left them "active" for up to an hour and the bot fought unbuffed (reported as
        // "buffing skips missile weapons"). The player's registry dropping to zero (a death,
        // or logging in after one) is the signal: forget them so they get recast.
        if (count == 0 && _lastLivePlayerEnchantCount != 0 && _itemSpellTimers.Count > 0)
        {
            _host.Log($"[BuffDiag] player enchantments dropped to 0 (was {_lastLivePlayerEnchantCount}) - clearing {_itemSpellTimers.Count} item-spell timer(s) so weapon/armor buffs recast");
            _itemSpellTimers.Clear();
        }
        _lastLivePlayerEnchantCount = count;

        // Preserve item enchantment timers (armor banes, Impenetrability, etc.)
        // that live on the item, not the player — ReadPlayerEnchantments won't
        // return them, so clearing would lose them every login.
        var preservedItemTimers = new Dictionary<int, RamTimerInfo>();
        foreach (var kvp in _ramBuffTimers)
        {
            if (kvp.Value.Expiration > DateTime.Now && IsItemEnchantment(kvp.Value.SpellName))
                preservedItemTimers[kvp.Key] = kvp.Value;
        }

        _ramBuffTimers.Clear();
        _familyLiveEntries.Clear();
        for (int i = 0; i < count; i++)
        {
            var spellInfo = SpellTableStub.GetById((int)spellIds[i]);
            if (spellInfo == null)
            {
                _host.Log($"[BuffDiag] refresh DROP: id={spellIds[i]} unresolved by SpellTableStub (no name) — would never re-add to RAM timers");
                continue;
            }

            double remainingSeconds = expiryTimes[i] - serverNow;
            if (remainingSeconds <= 0)
            {
                _host.Log($"[BuffDiag] refresh DROP: id={spellIds[i]} '{spellInfo.Name}' fam={spellInfo.Family} remainSec={remainingSeconds:F0} (expiry={expiryTimes[i]:F0} serverNow={serverNow:F0}) — treated as expired");
                continue;
            }

            // A permanent player enchantment: the engine reports no expiry
            // (double.MaxValue). It IS active right now — record it as a
            // presence-only entry instead of dropping it (the old code
            // skipped it here, so the family never recorded → IsBuffActive
            // false → recast every refresh forever). This is rebuilt from the
            // live registry every cycle: on death or a dispel trap the enchant
            // leaves the registry, the next read won't return it, the family
            // drops, and the bot rebuffs. IsPermanent keeps it out of
            // SaveBuffTimers so a relog can't resurrect a stripped buff.
            bool isPermanent = remainingSeconds > 86400 * 365;

            int level = GetSpellLevel(spellInfo);
            var candidate = new RamTimerInfo
            {
                Expiration  = isPermanent ? PermanentSentinel : DateTime.Now.AddSeconds(remainingSeconds),
                SpellLevel  = level,
                SpellName   = spellInfo.Name,
                IsPermanent = isPermanent,
            };

            // A family can hold MORE THAN ONE live enchantment at once. The real
            // case: a permanent item-granted "Armor Tinkering Expertise Self VI"
            // (id=707, no expiry) sitting in the same family as the tier-7
            // "Jibril's Blessing" (id=2197) we cast. AC applies the STRONGEST, so
            // that is what the timer must reflect.
            //
            // This used to assign unconditionally, so whichever entry the registry
            // happened to return LAST won. When that was the lower permanent tier,
            // the stored level (6) stayed below the target (7) forever and the
            // tier-upgrade rule in IsBuffActive fired on every poll — and because
            // the recast lands, confirms, and is then immediately clobbered again
            // by the next refresh, nothing ever broke the cycle. One family stuck
            // like this drags the whole auto-batch with it (a single buff below
            // threshold escalates to a full rebuff), which is how a ~65-spell
            // rebuff ended up running back-to-back indefinitely.
            //
            // Keep the higher tier; on a tie keep whichever lasts longer
            // (PermanentSentinel naturally sorts last, so a permanent entry wins
            // a tie against a timed one of the same tier).
            //
            // CastBuffsOverItemBuffs (default on, 2026-10-05): a permanent entry is item-granted,
            // not ours, and on a server with buff augments our own cast is stronger or longer.
            // It never takes the family timer then, so the timer tracks the highest TIMED entry
            // (ours) and the buff is cast, then recast when it runs low. Once our cast lands
            // the family reads on until it runs low, so no recast loop: the 09-27 loop came
            // from a permanent entry OWNING the timer at a tier below the target, which can't
            // happen when permanents are left out.
            bool ignoredAsItemBuff = isPermanent && _settings.CastBuffsOverItemBuffs;
            bool keepExisting =
                ignoredAsItemBuff
                || (_ramBuffTimers.TryGetValue(spellInfo.Family, out RamTimerInfo? seen)
                    && (seen.SpellLevel > level
                        || (seen.SpellLevel == level && seen.Expiration >= candidate.Expiration)));

            if (!keepExisting)
                _ramBuffTimers[spellInfo.Family] = candidate;

            if (!_familyLiveEntries.TryGetValue(spellInfo.Family, out var familyEntries))
                _familyLiveEntries[spellInfo.Family] = familyEntries = new List<(int, DateTime, bool)>();
            familyEntries.Add((level, candidate.Expiration, isPermanent));

            // Learn the real ceiling this family lands at (Incantations cap below
            // their nominal tier) so IsBuffActive stops chasing an unreachable tier.
            // Recorded for every timed entry read, including one the merge above
            // discarded — the ceiling is about what this character can land, not
            // about which entry currently wins the family slot. NOT for permanent
            // entries: those are item-granted ("Willpower Other I" on gear), not
            // something this character cast, and learning their tier as the
            // ceiling made a permanent level-1 enchant satisfy the family forever
            // (effective target 1, never expires): after a relog with the real
            // buff gone, Lucy went 17 minutes without Willpower (2026-09-27).
            if (!isPermanent)
                RecordAchievedTier(spellInfo.Family, level);

            if (isPermanent && _loggedPermanentFamilies.Add(spellInfo.Family))
                _host.Log($"[BuffDiag] permanent player enchant tracked: id={spellInfo.Id} '{spellInfo.Name}' (fam={spellInfo.Family}, lvl={level}) — presence-only, not persisted" +
                          (ignoredAsItemBuff
                              ? " — item-granted, CastBuffsOverItemBuffs is on: our own cast owns the timer"
                              : keepExisting && _ramBuffTimers.TryGetValue(spellInfo.Family, out RamTimerInfo? owner)
                                  ? $" — outranked in this family by '{owner.SpellName}' (lvl={owner.SpellLevel}), which owns the timer"
                                  : ""));
        }

        // Restore item enchantment timers that weren't covered by player enchantments
        foreach (var kvp in preservedItemTimers)
            _ramBuffTimers.TryAdd(kvp.Key, kvp.Value);

        SaveBuffTimers();
        // NOTE: do NOT set _liveBuffsRefreshed here. The login gate in OnHeartbeat
        // owns that flag and only opens it once the enchantment count STABILIZES
        // across consecutive reads. Setting it here let the first partial/empty
        // login read open the gate early → timers wiped → full rebuff every login.
        return _ramBuffTimers.Count;
    }

    /// <summary>
    /// Scans all equipped items and reads their enchantment registries.
    /// Tracks item-specific buffs (Impenetrability, Banes, etc.) separately from player buffs.
    /// </summary>
    private void RefreshEquippedItemEnchantments(double serverNow)
    {
        _itemBuffTimers.Clear();

        if (_worldObjectCache == null || !_host.HasReadObjectEnchantments)
            return;

        try
        {
            // Snapshot the inventory to avoid iterating a changing collection
            var inventorySnapshot = new List<WorldObject>();
            foreach (var item in _worldObjectCache.GetDirectInventory())
                inventorySnapshot.Add(item);

            const int MaxEnchantments = 64;
            uint[] spellIds    = new uint[MaxEnchantments];
            double[] expiryTimes = new double[MaxEnchantments];
            int equippedCount = 0;

            foreach (var item in inventorySnapshot)
            {
                int wieldedSlot = item.WieldedLocation;
                if (wieldedSlot == 0) continue; // not equipped

                equippedCount++;
                uint objectId = unchecked((uint)item.Id);
                int count;
                try
                {
                    count = _host.ReadObjectEnchantments(objectId, spellIds, expiryTimes, MaxEnchantments);
                }
                catch
                {
                    continue; // skip items whose weenie can't be read
                }
                if (count <= 0) continue;

                for (int i = 0; i < count; i++)
                {
                    var spellInfo = SpellTableStub.GetById((int)spellIds[i]);
                    if (spellInfo == null) continue;

                    double remainingSeconds = expiryTimes[i] - serverNow;
                    if (remainingSeconds <= 0) continue;
                    if (remainingSeconds > 86400 * 365) continue; // permanent

                    long key = ((long)objectId << 32) | (uint)spellInfo.Family;
                    _itemBuffTimers[key] = new ItemBuffTimerInfo
                    {
                        ObjectId = objectId,
                        ObjectName = item.Name,
                        Expiration = DateTime.Now.AddSeconds(remainingSeconds),
                        SpellLevel = GetSpellLevel(spellInfo),
                        SpellName  = spellInfo.Name,
                    };
                }
            }

            _host.Log($"[RynthAi] Item enchant scan: {equippedCount} equipped, {_itemBuffTimers.Count} timed buffs");
        }
        catch (Exception ex)
        {
            _host.Log($"[RynthAi] Item enchant scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the number of tracked item buff timers (equipped items only).
    /// </summary>
    public int ItemBuffCount => _itemBuffTimers.Count;

    /// <summary>
    /// Checks whether a specific equipped item has a given buff family active.
    /// </summary>
    public bool HasItemBuff(uint objectId, string spellBaseName)
    {
        int family = new SpellInfo(0, spellBaseName).Family;
        long key = ((long)objectId << 32) | (uint)family;
        return _itemBuffTimers.TryGetValue(key, out var info) && info.Expiration > DateTime.Now;
    }

    public void PrintBuffDebug()
    {
        int augs = GetArchmageEnduranceCount();
        _host.WriteToChat($"[RynthAi] Archmage endurance count: {augs}", 1);

        // Try a live refresh of player enchantments
        double serverNow = _host.HasGetServerTime ? _host.GetServerTime() : 0;
        if (serverNow > 0 && _host.HasReadPlayerEnchantments)
        {
            int refreshed = RefreshFromLiveMemory();
            _host.WriteToChat($"[RynthAi] Live read: {(refreshed >= 0 ? $"{refreshed} enchantments" : "unavailable (no qualities ptr)")}", 1);

            // TODO: Item enchantment scanning disabled — crashes when reading inventory item
            // memory via ReadObjectEnchantments. Needs investigation (AV in native InqInt or
            // GetWeenieObject for certain inventory items). See memory note for details.
        }
        else
        {
            _host.WriteToChat($"[RynthAi] ServerTime={Math.Round(serverNow)} (no live read: serverTime=0 or API missing)", 1);
        }

        if (_ramBuffTimers.Count == 0 && _itemSpellTimers.Count == 0)
        {
            _host.WriteToChat("[RynthAi] No buff timers active.", 1);
            return;
        }

        if (_itemSpellTimers.Count > 0)
        {
            _host.WriteToChat($"[RynthAi] -- Item spells ({_itemSpellTimers.Count}) --", 1);
            PrintItemSpellTimers();
        }

        if (_ramBuffTimers.Count > 0)
        {
            _host.WriteToChat($"[RynthAi] -- Player buffs ({_ramBuffTimers.Count}) --", 1);
            foreach (var kvp in _ramBuffTimers)
            {
                var info = kvp.Value;
                double total = GetCustomSpellDuration(info.SpellLevel);
                TimeSpan left = info.Expiration - DateTime.Now;
                double passed = total - left.TotalSeconds;
                _host.WriteToChat(
                    $"[RynthAi]   {info.SpellName} (Lvl {info.SpellLevel}): {Math.Round(passed / 60, 1)}m passed, " +
                    $"{Math.Round(left.TotalMinutes, 1)}m left. Total: {total / 60}m", 1);
            }
        }
    }

    /// <summary>
    /// Diagnostic dump of buff-tier resolution. For each desired buff, shows the
    /// skill, current buffed level, the max tier the threshold settings allow,
    /// and the spell that actually got resolved (with its tier). Lets the user
    /// see exactly why a low-tier spell is being cast — usually because the
    /// higher-tier Lore spell isn't in their spellbook yet.
    /// </summary>
    public void PrintBuffTierDebug()
    {
        List<string> desiredBuffs = BuildDynamicBuffList();
        var seen = new HashSet<AcSkillType>();

        foreach (string baseName in desiredBuffs)
        {
            AcSkillType skill = SkillForBuff(baseName);
            if (!IsSkillUsable(skill)) continue;

            // Skill summary line — print once per unique skill.
            if (seen.Add(skill))
            {
                int buffed = _charSkills != null ? _charSkills[skill].Buffed : -1;
                int maxTier = _spellManager.GetHighestBuffSpellTier(skill);
                _host.WriteToChat($"[RynthAi] -- {skill} (buffed={buffed}, max tier T{maxTier}) --", 1);
            }

            int spellId = FindBestSpellId(baseName, skill);
            if (spellId == 0)
            {
                _host.WriteToChat($"[RynthAi]   {baseName}: no learnable spell at any tier", 1);
                continue;
            }

            var info = SpellTableStub.GetById(spellId);
            int level = info != null ? GetSpellLevel(info) : 0;
            string activeTimer = "no timer";
            if (info != null && _ramBuffTimers.TryGetValue(info.Family, out RamTimerInfo? timer))
                activeTimer = $"timer T{timer.SpellLevel}, {Math.Round((timer.Expiration - DateTime.Now).TotalMinutes, 1)}m left";
            else if (info != null && _itemSpellTimers.TryGetValue(info.Family, out ItemSpellRecord? itemTimer))
                activeTimer = $"item T{itemTimer.SpellLevel}, {Math.Round((itemTimer.ExpiresAt - DateTime.Now).TotalMinutes, 1)}m left";

            _host.WriteToChat($"[RynthAi]   {baseName} → {info?.Name ?? "?"} (T{level}, {activeTimer})", 1);
        }
    }

    public void PrintItemBuffDebug()
    {
        if (_itemSpellTimers.Count == 0)
        {
            _host.WriteToChat("[RynthAi] No item spell timers recorded yet.", 1);
            return;
        }
        _host.WriteToChat($"[RynthAi] -- Item spells ({_itemSpellTimers.Count}) --", 1);
        PrintItemSpellTimers();
    }

    private void PrintItemSpellTimers()
    {
        foreach (var kvp in _itemSpellTimers)
        {
            var info = kvp.Value;
            TimeSpan left  = info.ExpiresAt - DateTime.Now;
            TimeSpan spent = DateTime.Now - info.CastAt;
            double totalMin = (info.ExpiresAt - info.CastAt).TotalMinutes;
            if (left.TotalSeconds <= 0)
                _host.WriteToChat($"[RynthAi]   {info.SpellName} (Lvl {info.SpellLevel}): EXPIRED", 1);
            else
                _host.WriteToChat(
                    $"[RynthAi]   {info.SpellName} (Lvl {info.SpellLevel}): " +
                    $"{Math.Round(spent.TotalMinutes, 1)}m elapsed, {Math.Round(left.TotalMinutes, 1)}m left / {Math.Round(totalMin, 0)}m total", 1);
        }
    }

    internal List<string> BuildDynamicBuffList()
    {
        var step1_CreatureMastery = new List<string>();
        var step2_Focus           = new List<string>();
        var step3_Willpower       = new List<string>();
        var step4_OtherCreature   = new List<string>();
        var step5_LifeAndItem     = new List<string>();
        var step6_WeaponAuras     = new List<string>();
        var step7_ArmorBanes      = new List<string>();

        foreach (string attr in BaseCreatureBuffs)
        {
            if (attr == "Focus Self") step2_Focus.Add(attr);
            else if (attr == "Willpower Self") step3_Willpower.Add(attr);
            else step4_OtherCreature.Add(attr);
        }

        foreach (var kvp in CreatureSkillBuffs)
        {
            if (IsSkillUsable(kvp.Key))
            {
                if (kvp.Value.Contains("Creature Enchantment Mastery"))
                    step1_CreatureMastery.Add(kvp.Value);
                else if (!BaseCreatureBuffs.Contains(kvp.Value))
                    step4_OtherCreature.Add(kvp.Value);
            }
        }

        step5_LifeAndItem.AddRange(new List<string> {
            "Regeneration Self", "Rejuvenation Self", "Mana Renewal Self",
            "Armor Self", "Acid Protection Self", "Fire Protection Self",
            "Cold Protection Self", "Lightning Protection Self",
            "Blade Protection Self", "Piercing Protection Self",
            "Bludgeoning Protection Self"
        });

        step6_WeaponAuras.AddRange(new List<string> {
            "Blood Drinker Self", "Hermetic Link Self", "Heart Seeker Self",
            "Spirit Drinker Self", "Swift Killer Self", "Defender Self"
        });

        step7_ArmorBanes.AddRange(new List<string> {
            "Impenetrability", "Acid Bane", "Blade Bane", "Bludgeoning Bane",
            "Flame Bane", "Frost Bane", "Lightning Bane", "Piercing Bane"
        });

        var final = new List<string>();
        final.AddRange(step1_CreatureMastery);
        final.AddRange(step2_Focus);
        final.AddRange(step3_Willpower);
        final.AddRange(step4_OtherCreature);
        final.AddRange(step5_LifeAndItem);
        final.AddRange(step6_WeaponAuras);
        final.AddRange(step7_ArmorBanes);
        return final;
    }

    /// <summary>
    /// Called when the engine's enchantment hook fires for an enchantment applied to the player
    /// (engine API v70+; older engines never call it). The engine raises it after AC applied the
    /// enchantment, so the live registry already holds it. Refreshes timers from live memory
    /// when possible; falls back to the hook-supplied duration.
    /// </summary>
    public void OnEnchantmentAdded(uint spellId, double durationSeconds)
    {
        var spellInfo = SpellTableStub.GetById((int)(spellId & 0xFFFF));
        if (spellInfo == null)
            return;

        // Only clear pending if this enchantment matches what we were casting.
        // Armor/item enchantments fire on the item, not the player — clearing
        // _pendingSpellId unconditionally would lose track of pending item casts
        // and prevent the chat handler from recording their timers.
        if (_pendingSpellId != 0)
        {
            var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
            if (pendingSpell != null && pendingSpell.Family == spellInfo.Family)
            {
                // Same bookkeeping as the registry-confirmed path in the tick: the engine
                // dispatches enchantment events BEFORE the "You cast ..." chat line of the
                // same pump tick, and that chat handler only marks the force-rebuff family
                // while _pendingSpellId is still set. Clearing pending here without marking
                // it would make an auto-batch respin this buff forever.
                if (_isForceRebuffing) _forceRebuffCastFamilies.Add(pendingSpell.Family);
                _cast.Confirmed(_pendingSpellId, "enchantment landed");
                _pendingSpellId = 0;
                NoteCastAccepted();
                _onCastResolved?.Invoke("self-buff confirmed (enchantment event)");
            }
        }

        // The family did land — clear any silent-no-show state (counter +
        // cooldown park) so future expiries trigger casts normally again.
        // Important for the /god case: if the user later learns the spell
        // the buff system should resume casting without a manual reset.
        // Also clears the chat-driven hard-reject cooldown if the family
        // somehow lands despite a recent reject (server-side fix etc.).
        // (Both, not `||`: a short-circuit left the cooldown in place whenever
        // the no-show counter was also set.)
        _silentNoShowCounts.Remove(spellInfo.Family);
        _buffFailCooldownUntil.Remove(spellInfo.Family);

        // Prefer live memory read — gives accurate remaining time for all enchantments.
        // But only trust it if the just-added buff actually shows up there yet.
        if (RefreshFromLiveMemory() >= 0 &&
            _ramBuffTimers.TryGetValue(spellInfo.Family, out RamTimerInfo? liveTimer) &&
            liveTimer.Expiration > DateTime.Now)
        {
            return;
        }

        RecordSpellTimer(spellInfo, durationSeconds);
    }

    /// <summary>
    /// Called when the engine's enchantment hook fires for an enchantment removed from the player
    /// (engine API v70+: the id is the spell id, layer stripped). The registry already reflects
    /// the removal, so the timers are rebuilt from it: a family that still holds another
    /// enchantment (a second layer, a permanent item-granted tier) stays active instead of
    /// being dropped and recast. Falls back to clearing the family's timer.
    /// </summary>
    public void OnEnchantmentRemoved(uint enchantmentId)
    {
        var spellInfo = SpellTableStub.GetById((int)(enchantmentId & 0xFFFF));
        if (spellInfo == null) return;

        if (RefreshFromLiveMemory() >= 0)
            return;   // (it saves the timers itself)

        if (_ramBuffTimers.Remove(spellInfo.Family))
            SaveBuffTimers();
    }

    /// <summary>
    /// Call from RynthAiPlugin.OnChatWindowText when chat arrives.
    /// Handles fizzle/fail → resets pending cast state.
    /// </summary>
    public void OnChatWindowText(string text, int chatType)
    {
        string lower = text.ToLowerInvariant();


        // Diagnostic: log every chat seen while waiting on a cast confirmation,
        // so we can see what AC is actually emitting and adjust our matchers.
        if (_pendingSpellId != 0)
            _host.Log($"[BuffChat] pending={_pendingSpellId} type={chatType} text='{text}'");

        // Our own incantation ('You say, "Puish Zharil"') is the server STARTING the last cast we
        // sent: its give-up and the server wait are timed from here (CastTracker). It is speech,
        // so it never reaches the result matchers below.
        if (IsOwnIncantation(text))
        {
            _cast.Incantation(UseDoneSeqNow());
            return;
        }

        // Speech and our own lines are never cast results. Every chat line reaches
        // this handler, so "Bob tells you, "did you cast banes?"" matched "ou cast ",
        // fell back to the pending spell and wrote a phantom 1-hour armor timer; a
        // quoted "fizzle" cleared the pending cast. Speech always carries ', "'
        // (says / tells you / You say, "<incantation>"); no cast result does.
        if (text.Contains(", \"", StringComparison.Ordinal)
            || lower.StartsWith("[rynthai]", StringComparison.Ordinal)
            || lower.StartsWith("rynthai]", StringComparison.Ordinal))
            return;

        // A kit on yourself finished (see KitPending). After the speech filter: "heal yourself" in a tell is not one.
        if (_kitUsedAt != DateTime.MinValue && _kitLandedAt == DateTime.MinValue && IsKitResultLine(lower))
            _kitLandedAt = DateTime.Now;

        // Real failure phrases. Note: bare "component" was removed — it false-matches
        // the "consumed the following components" line, which fires for both success
        // AND fizzle, so it's NOT a reliable signal either direction. Ignore it
        // entirely; wait for the explicit "ou cast" success or "fizzle" failure.
        // "You're too busy!" is NOT a cast failure — it's AC refusing because
        // the previous cast gesture is still animating. The old code lumped it
        // into the fizzle/fail block below, which set _lastCastAttempt =
        // DateTime.MinValue (defeating the SpellCastIntervalMs throttle) AND
        // cleared _pendingSpellId, so the same cast was re-issued on the very
        // next ~90ms tick → ~11 refusals/sec → AC's AddTextToScroll re-entry
        // AV at 0x00460D1D. Handle it separately: roll back the optimistic
        // item-spell timer (the bane genuinely didn't land) and clear pending
        // so the buff loop re-evaluates — but DO NOT zero _lastCastAttempt, so
        // the interval throttle stays active. The CanCastNow gesture gate is
        // the primary bound; the throttle is the backstop. No tight loop by
        // construction → 0x00460D1D killed regardless of gate precision.
        if (lower.Contains("you're too busy") || lower.Contains("you are too busy"))
        {
            // A kit in flight with no result yet: this is its refusal (see KitRefused). Our bot
            // sends nothing else while a kit is pending (CheckVitals returns at KitPending).
            if (_kitUsedAt != DateTime.MinValue && _kitLandedAt == DateTime.MinValue
                && (DateTime.Now - _kitUsedAt).TotalMilliseconds <= KitRefusalWindowMs)
                KitRefused("You're too busy!");
            // A refusal, never "done" (2026-10-05). When the pending cast's incantation has come,
            // the server is running it and this refused something else (a loot use, a kit): the
            // pending cast is kept and still resolves on its own result or give-up time.
            ObserveLastUseDone();
            var cur = _cast.Current;
            bool pendingRunning = _pendingSpellId != 0 && cur != null && cur.SpellId == _pendingSpellId
                                  && cur.HasIncantation && !cur.Done;
            bool urgentNudge = HeldNudgeUrgent();
            bool nudge = _cast.Refusal(UseDoneSeqNow(), urgentNudge ? CastTracker.UrgentHeldAfterIncantMs : CastTracker.HeldAfterIncantMs);
            if (pendingRunning)
            {
                _host.Log($"[BuffChat] too busy while pending={_pendingSpellId} is running on the server (incantation seen) — something else was refused; pending kept.");
            }
            else if (_pendingSpellId != 0)
            {
                var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
                if (pendingSpell != null && IsItemEnchantment(pendingSpell.Name))
                {
                    _itemSpellTimers.Remove(pendingSpell.Family);
                    SaveBuffTimers();
                }
                _host.Log($"[BuffChat] PARKED pending={_pendingSpellId} — AC too busy (cast gesture in progress); throttle kept, backing off before re-issue.");
            }
            if (!pendingRunning) _pendingSpellId = 0;

            // AC is authoritatively busy — wait, escalating, rather than retrying
            // on the 400ms tick. Cleared by NoteCastAccepted on the next success.
            _busyRefusalStreak++;
            double backoffMs = Math.Min(BusyBackoffBaseMs * Math.Pow(2, _busyRefusalStreak - 1), BusyBackoffMaxMs);
            _busyBackoffUntil = DateTime.Now.AddMilliseconds(backoffMs);
            if (_busyRefusalStreak == 1 || _busyRefusalStreak % 5 == 0)
                _host.Log($"[BuffChat] too-busy streak={_busyRefusalStreak} — backing off {backoffMs:0}ms before the next cast attempt.");

            // A run of refusals with nothing landing is a gesture that won't finish on its own;
            // moving the character cleared it (Drakkon, 2026-10-05). Do that once per streak.
            // Not while one of our casts is open on the server (incantation, no result): a stop
            // mid-windup can cancel a healthy cast, and a held one gets the nudge below instead.
            if (_busyRefusalStreak == BusyStopAtStreak && _host.HasStopCompletely && _cast.HeldSeconds == 0)
            {
                _host.StopCompletelyBy("Buff/TooBusy");
                _host.Log($"[BuffChat] too-busy streak={_busyRefusalStreak} — the cast gesture looks stuck: StopCompletely to clear it.");
            }

            // A cast the server has held open past CastTracker.HeldAfterIncantMs while it refuses
            // the next ones: one movement nudge, once per held cast (what moving him by hand did).
            if (nudge && _settings.IsMacroRunning)
                SendCastNudge(urgentNudge);

            if (!pendingRunning) _onCastResolved?.Invoke("too busy");
            return;
        }

        // HARD rejection: the server can't cast this at all and the next
        // attempt fails identically (missing components, no target, etc.).
        // Clear pending AND park the spell's family on a cooldown so the buff
        // loop stops re-picking it every cycle. Without this an uncastable buff
        // (e.g. "Battlemage's Blessing" with no components) re-casts every tick
        // → sustained AC AddTextToScroll re-entry → AC access-violation.
        // "have all the components for this spell" is matched specifically so
        // it does NOT collide with the success line "consumed the following
        // components".
        if (lower.Contains("missing some required") ||
            lower.Contains("you do not have the") ||
            lower.Contains("you must specify") ||
            lower.Contains("have all the components for this spell"))
        {
            _cast.Result(null, "refused by the server", atStart: true);
            if (_pendingSpellId != 0)
            {
                var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
                if (pendingSpell != null)
                {
                    if (IsItemEnchantment(pendingSpell.Name))
                    {
                        _itemSpellTimers.Remove(pendingSpell.Family);
                        SaveBuffTimers();
                    }
                    _buffFailCooldownUntil[pendingSpell.Family] =
                        DateTime.Now.AddSeconds(BuffFailCooldownSec);
                    // AC's refusal doesn't name the spell and vital casts print no
                    // "Casting:" line, so say which one. The server's text is not
                    // echoed: this line comes back through this handler, and it must
                    // not match the refusal phrases above.
                    bool noComps = lower.Contains("components") || lower.Contains("missing some required");
                    _host.WriteToChat($"[RynthAi] Skipping {pendingSpell.Name} for {BuffFailCooldownSec / 60:0} min " +
                                      $"({(noComps ? "no components for it" : "the server refused it")}).", 2);
                }
                _host.Log($"[BuffChat] CLEARED+COOLED pending={_pendingSpellId} ({BuffFailCooldownSec:0}s) — hard rejection in '{text}'");
            }
            _pendingSpellId = 0;
            _onCastResolved?.Invoke("hard-reject");
            return;
        }

        // OUT OF MANA: ACE refuses with "You don't have enough Mana to cast this
        // spell." It matched nothing, so the pending cast waited out the 2.5 s
        // registry give-up and counted a silent no-show; two of those parked the
        // family for 30 min, and a batch at low mana walked the list parking every
        // buff (a vital cast parked Heal Self the same way). Not a strike: clear
        // pending, rest this family briefly (a vital then falls to its potion) and
        // pause buffing for a few seconds. Vitals still run, so mana can recover.
        // A zeroed _lastCastAttempt (the soft-fail path) would re-cast at tick rate.
        if (lower.Contains("enough mana"))
        {
            _cast.Result(null, "not enough mana", atStart: true);
            if (_pendingSpellId == 0) return;
            var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
            double restSec = LowManaFamilyRestSec;
            if (pendingSpell != null)
            {
                if (IsItemEnchantment(pendingSpell.Name))
                {
                    _itemSpellTimers.Remove(pendingSpell.Family);
                    SaveBuffTimers();
                }
                // A heal rests only LowManaHealRestSec (2026-10-05): long enough for the mana step
                // after it to cast Stamina to Mana or drink, not 15 s with no Heal Self after the
                // mana was back (a mage low on mana mid-fight).
                bool healSpell = _pendingSpellId == _pendingVitalSpellId && _pendingVitalLabel == LabelHealing;
                restSec = healSpell ? LowManaHealRestSec : LowManaFamilyRestSec;
                _buffFailCooldownUntil[pendingSpell.Family] = DateTime.Now.AddSeconds(restSec);
            }
            _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId} - not enough mana; resting the family {restSec:0}s, buffs {LowManaBuffPauseMs / 1000:0}s.");
            _pendingSpellId = 0;
            _lowManaPauseUntil = DateTime.Now.AddMilliseconds(LowManaBuffPauseMs);
            _onCastResolved?.Invoke("not enough mana");
            return;
        }

        // SOFT failure: random fizzle or transient (out of mana). Clear pending
        // and allow a prompt retry — do NOT cooldown the family.
        if (lower.Contains("fizzle") ||
            lower.Contains("your spell failed") ||
            lower.Contains("lack the mana"))
        {
            _cast.Result(null, "fizzled");
            if (_pendingSpellId != 0)
            {
                var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
                if (pendingSpell != null && IsItemEnchantment(pendingSpell.Name))
                {
                    _itemSpellTimers.Remove(pendingSpell.Family);
                    SaveBuffTimers();
                }
                _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId} via soft-fail in '{text}'");
            }
            _lastCastAttempt = DateTime.MinValue;
            _pendingSpellId = 0;
            _onCastResolved?.Invoke("soft-fail");
            return;
        }

        // "You cast Incantation of Flame Bane on Gelidite Robe, refreshing ..."
        // "You cast Strength Self VIII"
        // The leading 'Y' is stripped by AC's chat-glyph layer before our handler
        // sees it — text actually arrives as "ou cast …". IndexOf works for both.
        int castIdx = lower.IndexOf("ou cast ", StringComparison.Ordinal);
        if (castIdx < 0) return;

        // Extract spell name: everything after "ou cast " up to " on " or ","
        string afterCast = text.Substring(castIdx + 8); // skip "ou cast "
        int onIdx = afterCast.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        int commaIdx = afterCast.IndexOf(',');
        int endIdx = afterCast.Length;
        if (onIdx > 0) endIdx = onIdx;
        if (commaIdx > 0 && commaIdx < endIdx) endIdx = commaIdx;
        string spellName = afterCast.Substring(0, endIdx).Trim();

        // Which of our casts this result belongs to, by the spell name it starts with (a vital's
        // line, "You cast Revitalize Self V and restore 70 points...", has no " on " or comma).
        var tracked = _cast.Result(afterCast, "result");

        // Try to find the spell by name first (authoritative — this is what was actually cast)
        int spellId = SpellDatabase.GetIdByName(spellName);
        SpellInfo? spellInfo = spellId > 0 ? SpellTableStub.GetById(spellId) : null;

        // Then the cast the line names (a late result of an earlier cast is credited to that cast,
        // not to whatever is pending now: Stamina to Mana was credited to Creature Enchantment
        // Mastery, 2026-10-05), then the pending spell.
        if (spellInfo == null && tracked != null)
            spellInfo = SpellTableStub.GetById(tracked.SpellId);
        if (spellInfo == null && _pendingSpellId != 0)
            spellInfo = SpellTableStub.GetById(_pendingSpellId);

        // A result that names another spell than the pending one is not the pending cast's
        // result: the pending cast keeps waiting for its own (or its give-up time).
        var pendingInfo = _pendingSpellId != 0 ? SpellTableStub.GetById(_pendingSpellId) : null;
        if (pendingInfo != null && spellInfo != null && spellInfo.Family != pendingInfo.Family)
        {
            _buffFailCooldownUntil.Remove(spellInfo.Family);
            _silentNoShowCounts.Remove(spellInfo.Family);
            NoteCastAccepted();
            if (IsItemEnchantment(spellInfo.Name)) RecordItemSpellCast(spellInfo);
            else RecordSpellTimer(spellInfo);
            _host.Log($"[BuffChat] result for '{spellInfo.Name}' is not pending={_pendingSpellId} ('{pendingInfo.Name}') — recorded, pending kept.");
            return;
        }

        if (spellInfo != null)
        {
            _buffFailCooldownUntil.Remove(spellInfo.Family); // it cast — clear any stale hard-fail cooldown
            // ...and its silent-no-show strikes. Only OnEnchantmentAdded cleared them,
            // which never fires for instant vitals (Heal/Revitalize/Stamina to Mana), so
            // two slow confirmations hours apart parked Heal Self for 30 minutes.
            _silentNoShowCounts.Remove(spellInfo.Family);
            NoteCastAccepted();                              // AC accepted a cast — end any too-busy backoff
            // Chat-authoritative record: only NOW (AC confirmed "you cast X").
            // Item/armor enchants live in _itemSpellTimers (what IsBuffActive
            // checks for them); player buffs in _ramBuffTimers.
            if (IsItemEnchantment(spellInfo.Name))
                RecordItemSpellCast(spellInfo);
            else
                RecordSpellTimer(spellInfo);
        }

        if (_pendingSpellId != 0)
        {
            if (_isForceRebuffing)
            {
                var ps = SpellTableStub.GetById(_pendingSpellId);
                if (ps != null) _forceRebuffCastFamilies.Add(ps.Family);
            }
            _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId} via ou-cast match name='{spellName}' resolvedId={spellId}");
        }
        _pendingSpellId = 0;
        _onCastResolved?.Invoke($"cast '{spellName}'");
    }

    /// <summary>Record that a cast just went out, so the next one waits for the
    /// server to report the action finished rather than a blind interval.</summary>
    private void NoteCastIssued(int spellId)
    {
        string name = SpellTableStub.GetById(spellId)?.Name ?? $"spell {spellId}";
        _cast.Sent(spellId, name, UseDoneSeqNow());
    }

    /// <summary>
    /// True while the server has not finished the last cast sent: until its own result line, a
    /// UseDone with no error after its incantation, or (after a refusal) a UseDone(0) showing
    /// the server free, bounded by its give-up time (CastResolutionTimeoutMs from the send,
    /// later with a late incantation). A refusal (0x1D) is never "finished" (2026-10-05).
    /// Inert on an engine without UseDone observation, as before. Internal for the host tests.
    /// </summary>
    internal bool IsAwaitingCastResolution()
    {
        if (!_host.HasUseDoneSeq) return false;
        bool hasLast = _host.TryGetLastUseDone(out int lastSeq, out uint lastErr);
        return _cast.AwaitingServer(CastResolutionTimeoutMs, _host.GetUseDoneSeq(), hasLast, lastSeq, lastErr);
    }

    private int UseDoneSeqNow() => _host.HasUseDoneSeq ? _host.GetUseDoneSeq() : 0;

    private void ObserveLastUseDone()
    {
        if (_host.TryGetLastUseDone(out int seq, out uint err))
            _cast.ObserveUseDone(seq, err);
    }

    /// <summary>Each tick: casts the server finished (UseDone(0) after the incantation) close,
    /// and a cast held open past CastTracker.HeldAfterIncantMs is logged once.</summary>
    private void PollCastTracker()
    {
        if ((_cast.Current == null || _cast.Current.Done) && (_cast.Open == null || _cast.Open.Done)) return;
        ObserveLastUseDone();
        _cast.Poll();
        // A held heal, or any held cast while health is under a heal line: nudge at
        // UrgentHeldAfterIncantMs on its own, without waiting for AC to refuse something
        // (2026-10-05: the held Heal Self was nudged 11.2 s after its incantation).
        if (_settings.IsMacroRunning && HeldNudgeUrgent() && _cast.TakeNudge(CastTracker.UrgentHeldAfterIncantMs))
            SendCastNudge(urgent: true);
    }

    /// <summary>The held cast is a heal, or health is under a heal line: nudge it early.</summary>
    private bool HeldNudgeUrgent()
    {
        var held = _cast.Held;
        if (held == null) return false;
        return IsHealCast(held.SpellId)
            || (_vitals.MaxHealth > 0 && HealthUnderAnyLine(_vitals.HealthPct, _vitals.StaminaPct));
    }

    // Spell ids this session sent as health spells (Heal Self, Stamina to Health Self at any tier:
    // the high tiers have their own names, e.g. Adja's Intervention). Set by AttemptVitalCast.
    private readonly HashSet<int> _healSpellIds = new();
    private bool IsHealCast(int spellId) => _healSpellIds.Contains(spellId);

    // Kits and potions while a cast is held open (2026-10-05): ACE (Healer.cs, Food.cs) answers a
    // use while the player is busy with "You're too busy!", and every kit Drakkon used while a Heal
    // Self was held open came back 0x1D (13:42:26, 13:42:34, 13:42:47). So while a cast is held:
    // none before its nudge has gone out (the nudge is what may free it), then one try every
    // HeldItemRetryMs (the nudge does not always free it at once: 9 s at 13:42:34).
    private const double HeldItemRetryMs = 3000;
    private DateTime _lastHeldItemTryAt = DateTime.MinValue;
    /// <summary>Kit / potion attempts skipped because a cast was held open (tests, status).</summary>
    internal int HeldItemSkips { get; private set; }

    /// <summary>True (with why) when a kit or potion would only be refused: a cast is held open.
    /// Otherwise false, and an attempt now is counted against HeldItemRetryMs.</summary>
    internal bool HeldCastBlocksItemUse(out string why)   // internal for the host tests (CastSafetyTests)
    {
        why = "";
        var held = _cast.Held;
        // Only a cast past its normal result window: before that the vital is still pending
        // and the server is simply finishing it.
        if (held == null || (Now - held.IncantAt).TotalMilliseconds <= CastTracker.IncantToResultMs) return false;
        if (!held.Nudged)
        {
            HeldItemSkips++;
            why = $"'{held.Name}' is held open by the server: ACE refuses kits and potions until it ends (nudge first)";
            return true;
        }
        double since = (Now - _lastHeldItemTryAt).TotalMilliseconds;
        if (since < HeldItemRetryMs)
        {
            HeldItemSkips++;
            why = $"'{held.Name}' still held open after the nudge: next kit/potion try in {HeldItemRetryMs - since:0} ms";
            return true;
        }
        _lastHeldItemTryAt = Now;
        return false;
    }

    /// <summary>The pending cast is past its give-up time: from its incantation when one came
    /// (CastTracker.GiveUpAt), else <paramref name="fallbackMs"/> from the send.</summary>
    private bool PendingGiveUpReached(double fallbackMs) =>
        _cast.GiveUpReached(_pendingSpellId, fallbackMs)
        ?? (DateTime.Now - _lastCastAttempt).TotalMilliseconds > fallbackMs;

    private string GiveUpWhy(double fallbackMs)
    {
        var c = _cast.Current;
        if (c == null || c.SpellId != _pendingSpellId) return $"in {fallbackMs:0} ms";
        return c.HasIncantation
            ? $"{(Now - c.IncantAt).TotalMilliseconds:0} ms after its incantation, {(Now - c.SentAt).TotalMilliseconds:0} ms after the send"
            : $"no incantation in {(Now - c.SentAt).TotalMilliseconds:0} ms";
    }

    /// <summary>Our own speech line: 'You say, "..."' (the leading Y may be stripped).</summary>
    internal static bool IsOwnIncantation(string text) =>
        text.StartsWith("You say, \"", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("ou say, \"", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One movement nudge for a cast the server holds open (CastTracker.Refusal said so, once per
    /// held cast). ACE ends a cast when the cast gesture's motion completes on the server, and a
    /// movement packet from the client is what let Drakkon's held casts finish (2026-10-05).
    /// StopCompletely from a standing character may send no movement at all, so this sends
    /// autorun on and straight off: two movement-state changes, no real step. Then casts wait
    /// NudgeHoldMs for the held cast's result.
    /// </summary>
    private void SendCastNudge(bool urgent = false)
    {
        var held = _cast.Open;
        string name = held?.Name ?? "?";
        double secs = _cast.HeldSeconds;
        if (!_host.HasSetAutoRun)
        {
            _host.Log($"[CastSafe] '{name}' held open {secs:0.0} s after its incantation, but this engine has no SetAutoRun: no nudge.");
            return;
        }
        _host.SetAutoRunBy("Buff/Nudge", true);
        _host.SetAutoRunBy("Buff/Nudge", false);
        CastNudges++;
        if (urgent) UrgentNudges++;
        DateTime hold = DateTime.Now.AddMilliseconds(NudgeHoldMs);
        if (_busyBackoffUntil < hold) _busyBackoffUntil = hold;
        string why = urgent
            ? (held != null && IsHealCast(held.SpellId) ? "it is a heal" : $"health {_vitals.HealthPct}% is under a heal line") +
              $": nudged at {CastTracker.UrgentHeldAfterIncantMs / 1000:0.0} s, not {CastTracker.HeldAfterIncantMs / 1000:0} s"
            : "AC answers 'too busy'";
        _host.Log($"[CastSafe] '{name}' held open {secs:0.0} s after its incantation and {why}: " +
                  $"one movement nudge (autorun on, off; #{CastNudges} this session). Casts wait {NudgeHoldMs:0} ms for its result.");
    }
    /// <summary>Nudges sent early because the held cast was a heal or health was under a heal line.</summary>
    internal int UrgentNudges { get; private set; }

    /// <summary>A cast was accepted by AC, so whatever it was busy with has
    /// cleared — drop the too-busy backoff immediately rather than serving out a
    /// stale delay.</summary>
    private void NoteCastAccepted()
    {
        if (_busyRefusalStreak == 0 && _busyBackoffUntil == DateTime.MinValue) return;
        if (_busyRefusalStreak > 0)
            _host.Log($"[BuffChat] cast accepted after {_busyRefusalStreak} too-busy refusal(s) — backoff cleared.");
        _busyRefusalStreak = 0;
        _busyBackoffUntil  = DateTime.MinValue;
    }

    private void RecordSpellTimer(SpellInfo spellInfo, double durationSeconds = -1)
    {
        if (durationSeconds < 0)
            durationSeconds = GetCustomSpellDuration(GetSpellLevel(spellInfo));

        if (durationSeconds <= 0)
            return;

        int level = GetSpellLevel(spellInfo);
        _ramBuffTimers[spellInfo.Family] = new RamTimerInfo
        {
            Expiration = DateTime.Now.AddSeconds(durationSeconds),
            SpellLevel = level,
            SpellName = spellInfo.Name,
        };
        SaveBuffTimers();
    }

    // TODO: Replace with real skill training lookup from AC memory
    private bool IsSkillUsable(AcSkillType s) => _charSkills != null ? _charSkills[s].Training >= 2 : true;

    internal static AcSkillType SkillForBuff(string name)
    {
        // Impregnability Self = Missile Defense (Creature Enchantment), NOT Item Enchantment.
        // Impenetrability / Bane / weapon auras = Item Enchantment.
        if (name.Contains("Blood Drinker") ||
            name.Contains("Hermetic Link") || name.Contains("Heart Seeker") ||
            name.Contains("Spirit Drinker") || name.Contains("Swift Killer") ||
            name.Contains("Defender") || name.Contains("Impenetrability") ||
            name.Contains("Bane"))
            return AcSkillType.ItemEnchantment;

        if (name.Contains("Protection") || name.Contains("Armor") ||
            name.Contains("Regeneration") || name.Contains("Rejuvenation") ||
            name.Contains("Renewal") || name == "Harlune's Blessing" ||
            name.Contains("Stamina to Mana") || name.Contains("Revitalize") ||
            name.Contains("Stamina to Health") || name == "Heal Self")
            return AcSkillType.LifeMagic;

        return AcSkillType.CreatureEnchantment;
    }

    // Stance-deadlock episode state for THIS path. ⚠ EnsureMagicMode is the
    // stance path that owns the bot whenever Buffing holds priority — and on
    // 2026-06-12 it silently retried ChangeCombatMode for 107 MINUTES (zero
    // log lines, no recovery) while the variant-2 wand/stance deadlock kept
    // the character unbuffed and standing in a spawn field. CombatManager's
    // capped recovery never ran because Buffing starves the combat tick.
    // Mirror that recovery here: visible retries, ≤2 capped re-equips per
    // episode, one-shot chat warn when wedged.
    private DateTime _buffStanceStuckSince = DateTime.MinValue;
    private DateTime _buffStanceLastRecoverAt = DateTime.MinValue;
    private int _buffStanceReEquips;
    private bool _buffStanceWedgeWarned;
    private const double BuffStanceStuckRecoverMs = 8000;
    private const int BuffStanceReEquipMax = 2;
    // No-wand branch: bare-handed flips sent without reaching Magic before it stops holding the bot.
    private const int NoWandFlipMaxFails = 4;

    // Stance settle (2026-10-05): the first hung cast went out 0.55 s after ChangeCombatMode(Magic)
    // and the wand use. EnsureMagicMode trusted the client's combat-mode field, which flips before
    // the server's stance animation has run. After a stance request or wand use from here, the
    // first cast waits until the client reports Magic for StanceSettleMs AND StanceMinAfterRequestMs
    // have passed since the request (the stance animation, about 1 s). A request older than
    // StanceWaitStaleMs is not waited on.
    private const double StanceSettleMs = 300;
    private const double StanceMinAfterRequestMs = 1000;
    private const double StanceWaitStaleMs = 10_000;
    private DateTime _stanceRequestedAt = DateTime.MinValue;
    private DateTime _magicSeenAt = DateTime.MinValue;
    private bool _stanceWaitLogged;
    // Emergency heals (2026-10-05): a heal under Emergency Heal At or Stamina To Health At, or under
    // Heal At with a monster in range, waits only for the client to read Magic plus StanceSettleMs:
    // no StanceMinAfterRequestMs floor. Drakkon's last Heal Self waited 4.1 s after the request.
    private bool _stanceUrgent;
    // What held the first cast after the stance was ready (logged with the settle; see StanceSettled).
    private string _stanceLateHolder = "";
    /// <summary>Times the first cast after a stance change used the emergency-heal settle (tests, status).</summary>
    internal int UrgentStanceSettles { get; private set; }

    private void NoteStanceRequest()
    {
        _stanceRequestedAt = Now;
        _magicSeenAt = DateTime.MinValue;
        _stanceWaitLogged = false;
        _stanceLateHolder = "";
    }

    /// <summary>
    /// Each heartbeat while a stance request is open: when the client first reads Magic, and a
    /// drop out of Magic restarts it. StanceSettled used to start the 300 ms clock only when it
    /// was first CALLED in Magic, which every other gate in front of it (cast interval, a busy
    /// count, a kit in flight) could put off; it also measured the "settled" time at that call.
    /// Root cause of the 4.1 s logged on 2026-10-05: the stance was ready 1 s after the request,
    /// but a healing kit went out 0.9 s after the request and the vitals step held the spell for
    /// the kit's result and grace (KitPending) until 3.1 s later; the log blamed the stance.
    /// </summary>
    private void ObserveStance()
    {
        if (_stanceRequestedAt == DateTime.MinValue) return;
        if (CurrentCombatMode == CombatMode.Magic)
        {
            if (_magicSeenAt == DateTime.MinValue) _magicSeenAt = Now;
        }
        else
            _magicSeenAt = DateTime.MinValue;   // not Magic (yet, or again): the streak restarts
    }

    /// <summary>When the stance we asked for counts as settled (MinValue: not reading Magic yet).</summary>
    private DateTime StanceReadyAt(bool urgent)
    {
        if (_magicSeenAt == DateTime.MinValue) return DateTime.MinValue;
        DateTime ready = _magicSeenAt.AddMilliseconds(StanceSettleMs);
        if (urgent) return ready;
        DateTime minAfterRequest = _stanceRequestedAt.AddMilliseconds(StanceMinAfterRequestMs);
        return minAfterRequest > ready ? minAfterRequest : ready;
    }

    /// <summary>OnHeartbeat's end: while the stance is ready but no cast has used it, remember
    /// what held the tick, for the settle line.</summary>
    private void NoteStanceLateHolder()
    {
        if (_stanceRequestedAt == DateTime.MinValue) return;
        DateTime ready = StanceReadyAt(_stanceUrgent);
        if (ready == DateTime.MinValue || Now < ready) return;
        string h = _healHold.Length > 0 ? _healHold : LastBuffSkipReason;
        if (h.Length > 0 && !h.StartsWith("Magic stance settling", StringComparison.Ordinal)) _stanceLateHolder = h;
    }

    /// <summary>False while the Magic stance we asked for is still settling (see the fields).
    /// <paramref name="urgentHeal"/>: an emergency heal, which skips the 1 s floor.</summary>
    private bool StanceSettled(bool urgentHeal = false)
    {
        if (_stanceRequestedAt == DateTime.MinValue) return true;
        DateTime now = Now;
        if ((now - _stanceRequestedAt).TotalMilliseconds > StanceWaitStaleMs)
        {
            _stanceRequestedAt = DateTime.MinValue;
            return true;
        }
        _stanceUrgent = urgentHeal;
        if (_magicSeenAt == DateTime.MinValue) _magicSeenAt = now;   // the client reads Magic (the caller checked)
        DateTime ready = StanceReadyAt(urgentHeal);
        if (now < ready)
        {
            LastBuffSkipReason = $"Magic stance settling ({(ready - now).TotalMilliseconds:0} ms left)";
            if (!_stanceWaitLogged)
            {
                _stanceWaitLogged = true;
                _host.Log($"[BuffStance] client reads Magic {(now - _stanceRequestedAt).TotalMilliseconds:0} ms after the stance request; first cast waits {(ready - now).TotalMilliseconds:0} ms more for the stance to settle" +
                          (urgentHeal ? " (emergency heal: no 1 s floor)." : "."));
            }
            return false;
        }
        double late = (now - ready).TotalMilliseconds;
        string lateText = late > 250
            ? $"; the stance was ready {(ready - _stanceRequestedAt).TotalMilliseconds:0} ms after the request and the cast then waited {late:0} ms" +
              (_stanceLateHolder.Length > 0 ? $" for: {_stanceLateHolder}" : "")
            : "";
        _host.Log($"[BuffStance] Magic stance settled: first cast {(now - _stanceRequestedAt).TotalMilliseconds:0} ms after the stance request ({(now - _magicSeenAt).TotalMilliseconds:0} ms after the client read Magic)" +
                  (urgentHeal ? ", emergency heal" : "") + lateText + ".");
        if (urgentHeal) UrgentStanceSettles++;
        _stanceRequestedAt = DateTime.MinValue;
        _magicSeenAt = DateTime.MinValue;
        _stanceLateHolder = "";
        return true;
    }

    /// <summary>
    /// A health spell that must not wait for the full stance settle: health under Emergency Heal
    /// At or Stamina To Health At, or under Heal At with a monster within MonsterRange.
    /// </summary>
    private bool HealIsUrgent(int hp, int stamPct)
    {
        string line = ActiveHealLine(hp, stamPct, out _, out bool inCombat);
        return line is "Emergency Heal At" or "Stamina To Health At" || (line == "Heal At" && inCombat);
    }

    private bool EnsureMagicMode(bool forBuff = false, bool urgentHeal = false)
    {
        // Per-call signal; only the buff path may set it (vitals/self-heals NEVER degrade).
        _wandSwapExhausted = false;
        _magicModeBlocked = false;
        if (CurrentCombatMode == CombatMode.Magic)
        {
            // Successfully reached magic mode — clear per-cycle teardown flag
            // and wield gate so the next switch out of magic restarts cleanly.
            _combatTeardownDoneForCurrentBuffCycle = false;
            _pendingWieldId = 0;
            _wieldCooldownUntil = DateTime.MinValue;
            // Reset the exponential-backoff counter so the next problem starts
            // at the fast 1s cadence again. See field comment for rationale.
            _buffStanceConsecutiveFails = 0;
            // Episode over — clear the deadlock-recovery state too.
            _buffStanceStuckSince = DateTime.MinValue;
            _buffStanceReEquips = 0;
            _buffStanceWedgeWarned = false;
            // FIX: reached Magic — clear the new swap-confirm / resilience state so a
            // later recoverable episode (e.g. after a slot frees from looting) starts clean.
            _bowDequipPendingId = 0;
            _bowDequipAttempts = 0;
            _wieldGateFailCount = 0;
            // In Magic, but the stance we asked for may still be settling: yield the tick.
            return StanceSettled(urgentHeal);
        }

        int wandId = FindWandInItems();
        if (wandId == 0)
        {
            // No wand available — try to switch mode anyway (bare-handed magic).
            // Same exponential backoff as the wand-wielded branch below: this used to
            // send ChangeCombatMode every SpellCastIntervalMs (2.5 Hz) forever when the
            // flip didn't take, the stance spam that crashed clients (field comment at
            // _lastBuffStanceAttempt), while buffing held the bot.
            int noWandShift = Math.Min(_buffStanceConsecutiveFails, 5);
            int noWandGateMs = Math.Min(1000 << noWandShift, 30000);
            if ((DateTime.Now - _lastBuffStanceAttempt).TotalMilliseconds > noWandGateMs)
            {
                _host.ChangeCombatMode(CombatMode.Magic);
                NoteStanceRequest();
                _lastBuffStanceAttempt = DateTime.Now;
                _lastCastAttempt = DateTime.Now;
                if (_buffStanceConsecutiveFails < 6) _buffStanceConsecutiveFails++;
                if (_buffStanceConsecutiveFails > 1)
                    _host.Log($"[BuffStance] no wand: ChangeCombatMode(Magic) retry #{_buffStanceConsecutiveFails} (next retry in {noWandGateMs / 1000}s)");
                return false;
            }
            // Several flips haven't taken: stop holding the bot. Buff families park
            // (the caller's wand-swap-exhausted path) and vitals fall through to kits
            // and potions, so combat runs; the flip is retried on the backoff.
            if (_buffStanceConsecutiveFails >= NoWandFlipMaxFails)
            {
                _magicModeBlocked = true;
                if (forBuff) _wandSwapExhausted = true;
                if (!_buffStanceWedgeWarned)
                {
                    _buffStanceWedgeWarned = true;
                    _host.WriteToChat($"[RynthAi] Can't enter Magic mode (no wand found) after {_buffStanceConsecutiveFails} tries. Buffs and spell heals wait and retry; add a wand to Items if this character should cast.", 2);
                }
            }
            return false;
        }

        // Primary check: CurrentWieldedLocation (stype=10) — works even before the
        // phys-obj offset probe fires, mirrors CombatManager.EquipWeaponAndSetStance.
        bool alreadyWielded = false;
        var wandObj = _worldObjectCache?[wandId];
        if (wandObj != null)
            alreadyWielded = wandObj.Values(LongValueKey.CurrentWieldedLocation, 0) > 0;

        // Secondary check via API if primary didn't confirm
        if (!alreadyWielded && _host.HasGetObjectWielderInfo)
        {
            uint playerId = _host.GetPlayerId();
            if (playerId != 0 &&
                _host.TryGetObjectWielderInfo((uint)wandId, out uint wielder, out _) &&
                wielder == playerId)
                alreadyWielded = true;
        }

        if (!alreadyWielded)
        {
            // Wield gate: don't spam UseObject. Hold off after the first issue
            // until either the wielded check confirms (cleared on the Magic-mode
            // branch above) or the resolve timeout elapses, then cool down
            // before retrying. Mirrors the no-chat-timeout pattern used for
            // spell casts at the top of OnHeartbeat. Checked BEFORE the shared
            // swap gate so a denied attempt never claims the cross-subsystem slot.
            DateTime now = DateTime.Now;
            if (now < _wieldCooldownUntil)
                return false;

            if (_pendingWieldId != 0)
            {
                if ((now - _pendingWieldAt).TotalMilliseconds < WieldResolveTimeoutMs)
                    return false;

                _host.Log($"[WieldGate] UseObject(0x{(uint)_pendingWieldId:X8}) not confirmed in " +
                          $"{WieldResolveTimeoutMs:0}ms — cooling down {WieldCooldownMs:0}ms");
                _pendingWieldId = 0;
                _wieldCooldownUntil = now.AddMilliseconds(WieldCooldownMs);
                // FIX: bound the previously-infinite wield loop. If the wand still won't
                // wield after a few cooldowns (e.g. no pack to free the bow, or an
                // unresolvable collision), degrade so buffing yields and combat runs.
                if (++_wieldGateFailCount >= WieldGateFailMax)
                    return SignalSwapFailure(forBuff, FindWieldedNonWandWeapon(wandId), wandId);
                return false;
            }

            // Shared weapon-swap gate: if CombatManager (or anything else) swapped
            // a weapon within the last few seconds, wait — two equips in flight
            // collide ("you can only move or use one item at a time" / AV).
            if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("buff-wand"))
                return false;

            // Tear down any in-flight physical combat BEFORE the UseObject. The
            // transition Melee/Missile → Magic-via-wand must explicitly cancel the
            // pending attack, or AC gets a UseObject while m_bAttacking is still
            // set and wedges the item-action gate. Done only after we've claimed
            // the swap slot above, so we don't cancel an attack then fail to swap.
            if (!_combatTeardownDoneForCurrentBuffCycle &&
                (CurrentCombatMode == CombatMode.Melee || CurrentCombatMode == CombatMode.Missile))
            {
                if (_host.HasCancelAttack)   _host.CancelAttack();
                if (_host.HasStopCompletely) _host.StopCompletelyBy("Buff/WandSwap");
                _combatTeardownDoneForCurrentBuffCycle = true;
                _host.Log($"[BuffPre] CancelAttack+StopCompletely before wand equip (mode was {CurrentCombatMode})");
            }

            // FIX: stock ACE will NOT auto-dequip the bow for a Held-slot wand
            // (CheckWeaponCollision refuses while mainhand != null). Stow the wielded
            // non-wand weapon into a capacity-verified open pack FIRST (AutoCram pattern,
            // AV-safe), then UseObject(wand) once the hand is free. Fully bounded.
            int bowId = FindWieldedNonWandWeapon(wandId);
            if (bowId != 0)
            {
                if (_bowDequipPendingId == bowId
                    && (now - _bowDequipAt).TotalMilliseconds < WieldResolveTimeoutMs
                    && IsWieldedLive(bowId))
                    return false; // dequip still resolving

                if (IsWieldedLive(bowId))
                {
                    int openPack = WorldObjectCache.FindPackFor(_host, _worldObjectCache, includeMainPack: true, requireFree: 1);
                    if (openPack == 0 || _bowDequipAttempts >= BowDequipMaxAttempts)
                    {
                        // No verified-open pack to receive the bow, or repeated dequip
                        // failures -> the swap provably can't complete now. Degrade.
                        _host.Log($"[WieldGate] bow 0x{(uint)bowId:X8} dequip blocked (openPack=0x{(uint)openPack:X8}, attempts={_bowDequipAttempts}/{BowDequipMaxAttempts}) — degrading");
                        if (openPack == 0)
                            WorldObjectCache.WarnPackFull(_host, "can't put the weapon away to switch to the wand, so buffs and spell heals can't cast");
                        return SignalSwapFailure(forBuff, bowId, wandId);
                    }
                    _bowDequipAttempts++;
                    if (_worldObjectCache?[bowId] is WorldObject blocker && IsInOffhandSlot(blocker))
                        OffhandStowed?.Invoke(bowId);   // a shield: combat puts it back after
                    _host.MoveItemInternal((uint)bowId, (uint)openPack, 0, 1); // amount>=1 (engine rejects 0)
                    _bowDequipPendingId = bowId;
                    _bowDequipAt = now;
                    _lastCastAttempt = now;
                    _host.Log($"[WieldGate] dequip bow 0x{(uint)bowId:X8} -> pack 0x{(uint)openPack:X8} (attempt {_bowDequipAttempts}/{BowDequipMaxAttempts}) before wand equip");
                    return false; // yield until the bow is out of hand
                }
                _bowDequipPendingId = 0; // bow confirmed unwielded — fall through to wield the wand
            }

            _host.UseFor((uint)wandId, "Buff", "buffing: wield the wand");
            NoteStanceRequest();
            _pendingWieldId = wandId;
            _pendingWieldAt = now;
            _lastCastAttempt = now;
            return false; // Yield — let the server equip the wand
        }

        // Wand is wielded — clear the gate and switch stance.
        if (_pendingWieldId == wandId)
        {
            _pendingWieldId = 0;
            _wieldCooldownUntil = DateTime.MinValue;
        }

        // Deadlock episode tracking: wand wielded but mode refuses to flip.
        if (_buffStanceStuckSince == DateTime.MinValue)
            _buffStanceStuckSince = DateTime.Now;
        double stuckMs = (DateTime.Now - _buffStanceStuckSince).TotalMilliseconds;

        // Capped re-equip recovery (mirrors CombatManager): handles the rare
        // stale "wielded reads true" by resyncing the wield once or twice.
        // HARD-CAPPED — UseObject on a genuinely wielded wand is a MOVE to AC,
        // and unbounded re-equip jams the item-action queue permanently.
        if (_buffStanceReEquips < BuffStanceReEquipMax
            && stuckMs > BuffStanceStuckRecoverMs
            && (DateTime.Now - _buffStanceLastRecoverAt).TotalMilliseconds > BuffStanceStuckRecoverMs
            && (_weaponSwapGate == null || _weaponSwapGate.TryBeginSwap("buff-stance-recovery")))
        {
            _buffStanceLastRecoverAt = DateTime.Now;
            _buffStanceReEquips++;
            _host.Log($"[BuffStance] STUCK {stuckMs:0}ms (wielded, mode={CurrentCombatMode}≠Magic) — re-equip attempt {_buffStanceReEquips}/{BuffStanceReEquipMax} 0x{(uint)wandId:X8}");
            _host.UseFor((uint)wandId, "Buff", "buffing: stance stuck, wield the wand again");
            NoteStanceRequest();
            _lastCastAttempt = DateTime.Now;
            return false;
        }
        // Past the cap and clearly wedged: warn ONCE so a multi-hour silent
        // coma can never happen again, then keep mode-change-only retries.
        if (_buffStanceReEquips >= BuffStanceReEquipMax && !_buffStanceWedgeWarned
            && stuckMs > BuffStanceStuckRecoverMs * 3)
        {
            _buffStanceWedgeWarned = true;
            _host.WriteToChat($"[RynthAi] Buff stance wedged: wand wielded but AC won't enter Magic mode after {stuckMs / 1000:0}s. Buffs cannot cast. Try /ra clearbusy, or relog if it persists.", 2);
        }

        // Exponential backoff: 1s → 2s → 4s → 8s → 16s → 30s cap. Counter
        // increments only when this branch fires AND the previous flip didn't
        // stick (we got back here with mode != Magic). Reset to 0 at the top
        // of EnsureMagicMode when mode == Magic. See field comment.
        int shift = _buffStanceConsecutiveFails;
        if (shift > 5) shift = 5;
        int gateMs = 1000 << shift;        // 1s, 2s, 4s, 8s, 16s, 32s
        if (gateMs > 30000) gateMs = 30000; // cap at 30s
        if ((DateTime.Now - _lastBuffStanceAttempt).TotalMilliseconds > gateMs)
        {
            _host.ChangeCombatMode(CombatMode.Magic);
            NoteStanceRequest();
            _lastBuffStanceAttempt = DateTime.Now;
            _lastCastAttempt = DateTime.Now;
            if (_buffStanceConsecutiveFails < 6) _buffStanceConsecutiveFails++;
            // Visible retry line (throttled by the backoff itself): the
            // 2026-06-12 coma produced ZERO log output from this path.
            _host.Log($"[BuffStance] ChangeCombatMode(Magic) retry #{_buffStanceConsecutiveFails} (stuck {stuckMs / 1000:0}s, next retry in {gateMs / 1000:0}s)");
        }
        return false; // Yield — let stance animation finish
    }

    /// <summary>Told the id of an off-hand item (a shield) buffing put into a pack for its wand,
    /// so combat puts it back on (CombatManager.NoteOffhandStowed).</summary>
    public Action<int>? OffhandStowed { get; set; }
    private bool _offhandNoneNoted;

    // The currently-wielded non-wand weapon (the bow) that blocks the Held-slot wand; else
    // what is in the off hand (a shield, or an off-hand weapon): ACE refuses a Held-slot caster
    // beside either. With the global Offhand setting None the off hand is never touched (the
    // wand then can't be wielded; logged once).
    private int FindWieldedNonWandWeapon(int wandId)
    {
        if (_worldObjectCache == null) return 0;
        int offhand = 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
        {
            if (wo.Id == wandId) continue;
            if (IsWandObject(wo)) continue;
            if (WorldObjectCache.IsAmmo(wo)) continue;   // arrows stay on; only the bow blocks the wand
            if (IsInOffhandSlot(wo)) { offhand = wo.Id; continue; }
            if ((wo.ObjectClass == AcObjectClass.MeleeWeapon
                 || wo.ObjectClass == AcObjectClass.MissileWeapon)
                && WorldObjectCache.IsWieldedByPlayer(_host, wo))
                return wo.Id;
        }
        if (offhand != 0 && OffhandRules.Parse(_settings.OffhandDefault, OffhandMode.Auto) == OffhandMode.None)
        {
            if (!_offhandNoneNoted)
            {
                _offhandNoneNoted = true;
                _host.Log($"[WieldGate] 0x{(uint)offhand:X8} '{_worldObjectCache[offhand]?.Name}' is in the off hand and Offhand is None — leaving it; the server won't wield the wand beside it");
            }
            return 0;
        }
        return offhand;
    }

    private static bool IsInOffhandSlot(WorldObject wo) =>
        (wo.Values(LongValueKey.CurrentWieldedLocation, 0) & OffhandRules.ShieldSlot) != 0;

    // Live (forceRefresh) wielded check — never trusts a stale cache snapshot.
    private bool IsWieldedLive(int id)
    {
        if (id == 0 || _worldObjectCache == null) return false;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
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

    // FindOpenPackForDequip DELETED (P2a) — replaced by the shared
    // WorldObjectCache.FindPackFor(host, cache, includeMainPack:true, requireFree:1).
    // includeMainPack:true preserves the prior main-pack (cap-102) fallback so the
    // just-shipped wand-swap dequip behaviour is unchanged; requireFree:1 because a
    // single genuinely-free slot is enough for the bow (anti-churn is for the auto-
    // loops, not the user/buff-driven swap). Call sites: EnsureMagicMode + SignalSwapFailure.

    // Bounded degrade. Re-arms the bow for combat and, for the BUFF path only, raises the
    // exhaustion signal so the buff selector parks the family and the bot fights unbuffed.
    // Vitals (forBuff=false) never park a family; they see _magicModeBlocked, fall through to
    // kits/potions, and retry the swap after VitalSwapBlockedRetrySec.
    private bool SignalSwapFailure(bool forBuff, int bowId, int wandId)
    {
        // If the wand ended up wielded but mode never flipped, dequip it so the bow can
        // re-wield (stock ACE won't auto-dequip the wand for a MissileWeapon slot). Only
        // one of these two branches fires per call — no same-tick wield/unwield race.
        if (wandId != 0 && IsWieldedLive(wandId))
        {
            int openPack = WorldObjectCache.FindPackFor(_host, _worldObjectCache, includeMainPack: true, requireFree: 1);
            if (openPack != 0) _host.MoveItemInternal((uint)wandId, (uint)openPack, 0, 1);
        }
        else if (bowId != 0 && !IsWieldedLive(bowId))
        {
            _host.UseFor((uint)bowId, "Buff", "buffing done: wield the bow again"); // re-wield the bow for missile combat
        }
        _pendingWieldId = 0;
        _bowDequipPendingId = 0;
        _bowDequipAttempts = 0;
        _wieldGateFailCount = 0;
        _magicModeBlocked = true;
        if (forBuff) _wandSwapExhausted = true;
        return false;
    }

    /// <summary>
    /// Locates a wand to equip: a listed wand, the one already in hand first (so buffing
    /// doesn't swap one listed wand for another). The pack is searched only when the Items list
    /// has no wand at all and WieldUnlistedWandWhenNoneListed is on, by object class only.
    /// Shares WeaponList.FindWand with CombatManager.FindWandInItems.
    /// </summary>
    private int FindWandInItems()
    {
        if (_worldObjectCache == null) return 0;
        var cache = _worldObjectCache;
        return WeaponList.FindWand(_settings.ItemRules, i => cache[i], wo => WorldObjectCache.IsWieldedByPlayer(_host, wo),
            cache.GetInventory(), _settings.WieldUnlistedWandWhenNoneListed, out _);
    }

    private static bool IsWandObject(WorldObject wo) => WeaponList.IsWand(wo);
}
