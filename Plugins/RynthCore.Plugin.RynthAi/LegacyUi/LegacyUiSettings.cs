using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

public static class DashWindows
{
    public static bool ShowMacroRules;
    public static bool ShowMonsters;
    public static bool ShowNavigation;
    public static bool ShowWeapons;
    public static bool ShowLua;
    public static bool ShowDungeonMap;
}

public sealed class LegacyUiSettings
{
    // ── Runtime-only state — never persisted ────────────────────────────────
    [JsonIgnore] public bool IsMacroRunning;
    /// <summary>Meta state — set only by meta actions (SetState, CallState, ReturnFromCall, UI, meta load).</summary>
    [JsonIgnore] public string CurrentState = "Default";
    /// <summary>Bot action state — what the bot is doing right now (Default, Combat, Looting, Navigating, Buffing).
    /// Separate from meta state so operational cycling doesn't corrupt meta rule matching.</summary>
    [JsonIgnore] public string BotAction = "Default";
    /// <summary>Display only: what the "Buffing" slot is doing (Healing, Restoring mana, Restoring
    /// stamina, Buffing). Written by RynthAiPlugin every tick; never read by control flow.
    /// Displays use ActivityArbiter.DisplayLabel, never this field alone.</summary>
    [JsonIgnore] public string BuffingLabel = "Buffing";
    [JsonIgnore] public bool IsRecordingNav;

    public bool EnableBuffing = true;
    public bool EnableCombat;
    public bool EnableNavigation;
    public bool EnableLooting;
    public bool EnableMeta;
    public bool EnableRaycasting = true;
    public bool MetaDebug;

    public int MovementMode;
    public float NavStopTurnAngle = 20f;
    public float NavResumeTurnAngle = 10f;
    public float NavDeadZone = 4f;
    public float NavSweepMult = 2.5f;
    public float NavLookaheadYards = 4.0f;       // distance to start blending the aim point toward the next waypoint (0 = off)
    public float NavShortcutYards = 1.0f;        // on arrival, skip waypoints only while the straight run to a later one stays within this of every skipped point (0 = visit every point)
    public float NavTurnRateDegPerSec = 270f;    // mode 0 (heading servo) max turn rate
    public float NavTier1TurnSpeed = 3.0f;       // mode 1 (CM_Movement) DoMovement turn-command speed (magnitude of CMotionInterp turn_speed; 1.0 = native keyboard rate)
    public float PostPortalDelaySec = 4.0f;
    // Route recovery (NavigationEngine detours): when stuck after the jump fails, or when nav
    // itself wanders off the route, plan a way back with the dungeon map / RynthNav navmesh.
    public bool  NavRecoveryEnabled = true;
    public float NavOffTrackYards = 170f;        // off track beyond this (never below 160: metas pull the bot up to ~159 yd away)
    public int   NavMaxDetourAttempts = 3;       // detours per waypoint before falling back to the escape ladder alone
    public float T2Speed = 1.0f;
    public float T2DistanceTo = 0.5f;
    public float T2ReissueMs = 2000f;
    public float T2MaxRangeYd = 500f;
    public int T2MaxLandblocks = 3;
    public float T2WalkWithinYd = 5f;

    public string CurrentNavPath = string.Empty;
    public string CurrentLootPath = string.Empty;
    public string CurrentMetaPath = string.Empty;

    public int MacroSettingsIdx = 1;
    public int NavProfileIdx = 1;
    public int LootProfileIdx;
    public int MetaProfileIdx = 1;

    public bool EnableAutostack = true;
    public bool EnableAutocram = true;
    // VTank's ReadUnknownScrolls (default on): loot scrolls of spells the character doesn't
    // know and can learn (school trained or specialized, ACE's skill rule), then read them
    // at a safe moment; scrolls already in the pack too. Needs looting on for corpses.
    public bool ReadUnknownScrolls = true;
    public bool EnableCombineSalvage = true;
    public bool CombineBagsDuringSalvage = true;

    public bool ShowTargetStaminaMana;

    public bool EnableMissileCrafting = true;
    public int MissileCraftAmmoThreshold = 1000;

    public int LootInterItemDelayMs = 50;
    public int LootContentSettleMs = 100;
    public int LootEmptyCorpseMs = 300;
    public int LootClosingDelayMs = 200;
    public int LootAssessWindowMs = 200;
    public int LootRetryTimeoutMs = 500;
    public int LootOpenRetryMs = 1500;
    public int LootCorpseTimeoutMs = 12000;
    public bool LootJumpEnabled = false;
    public int LootJumpHeight = 10; // 0–100; maps to 0.0–1.0 JumpNonAutonomous extent

    public int SalvageOpenDelayFirstMs = 400;
    public int SalvageOpenDelayFastMs = 50;
    public int SalvageAddDelayFirstMs = 600;
    public int SalvageAddDelayFastMs = 50;
    public int SalvageSalvageDelayMs = 50;
    public int SalvageResultDelayFirstMs = 1000;
    public int SalvageResultDelayFastMs = 250;
    public bool UseDispelItems;
    public bool CastDispelSelf;
    public bool AutoFellowMgmt;
    public bool MChargesWhenOff;

    public int HealAt = 60;
    // Vitals consumables and safety stops (2026-09-28). Stops are options: most
    // metas handle a death themselves (run back), so the macro keeps going unless asked.
    public bool UsePotions = true;
    // Unlimited buff gems (Asheron's Benediction, Blackmoor's Favor) and field rations (2026-09-29).
    public bool UseBuffItems = true;
    public int MakeRationsBelow = 5;   // 0 = don't cook field rations
    public bool UseKitsInMagicMode = true;
    public bool PeaceModeForKits = false;
    // Use a healing/stamina kit only when its success chance (ACE's own skill check) is at
    // least this %, else cast the spell instead, as VTank does. 0 = always use kits.
    public int KitMinSuccessPct = 70;
    // Emergency heal: at or under this health %, a healing kit (out of Magic mode, ahead of Heal
    // At's chain) and, while a cast is pending, a kit or potion. 0 = off.
    public int EmergencyHealAt = 30;
    // Stamina to Health Self: cast at or under this health % ahead of Heal At's chain, when
    // stamina is over StaminaToHealthMinStamina %. 0 = never cast it. Both default to the
    // values that were hard-wired to Emergency Heal At before (30 / 20).
    public int StaminaToHealthAt = 30;
    public int StaminaToHealthMinStamina = 20;
    public bool StopMacroOnDeath = false;
    public bool StopMacroOnNoComponents = false;
    public bool StopLootingWhenPackFull = true;
    public bool StopMacroWhenPackFull = false;
    public int RestamAt = 30;
    public int GetManaAt = 40;
    public int TopOffHP = 95;
    public int TopOffStam = 95;
    public int TopOffMana = 95;
    public int HealOthersAt = 50;
    public int RestamOthersAt = 10;
    public int InfuseOthersAt = 10;

    public int MonsterRange = 50;
    // Disengage distance for an already-locked target (hysteresis deadband).
    // 0 = auto (MonsterRange + 3). Must exceed MonsterRange or it's ignored.
    // Without the deadband, the small step-back AC applies on combat-mode entry
    // can push a mob just past MonsterRange, dropping it and flipping to peace,
    // then re-approaching — an endless engage/peace oscillation at the edge.
    public int MonsterDisengageRange = 0;
    public int RingRange = 5;
    public int ApproachRange = 4;
    public int MinRingTargets = 4;
    // Blast like a ring: with BlastRange > 0, a rule with Blast on casts a blast only when
    // MinBlastTargets monsters are within BlastRange in the blast's fan (3 projectiles over 90
    // degrees) toward the target; otherwise its other shape. 0 = off: Blast is just a shape.
    public int BlastRange = 0;
    public int MinBlastTargets = 3;
    // Nav point reach (VTank "Follow/Nav Min Distance"), yards: how close nav gets to each
    // nav point before moving on to the next, and how close Follow gets to its leader.
    // Metas set it as FollowNavMin (yards) or VTank's NavCloseStopRange (landblock units,
    // x240 = yards; the same VTank setting). Settings ▸ Navigation and the Nav panel show it.
    public float FollowNavMin = 1.5f;
    public const float FollowNavMinLowest = 0.5f, FollowNavMinHighest = 20f;
    public static float ClampFollowNavMin(float yards) =>
        float.IsNaN(yards) ? 1.5f : Math.Clamp(yards, FollowNavMinLowest, FollowNavMinHighest);
    public float NavRingThickness = 6.0f;
    public float NavLineThickness = 6.0f;
    public float NavHeightOffset = 0.05f;
    public float NavSlopeSink = 1.5f;
    public bool  ShowTerrainPassability = true;
    public double MaxMonRange = 12.0;
    public bool SummonPets;
    public int CustomPetRange = 5;
    public int PetMinMonsters = 1;
    public bool PetAutoRefill = true;   // auto-consume Encapsulated Spirits to recharge empty essences
    public bool AdvancedOptions;
    public bool MineOnly = true;
    public bool ShowEditor;
    public double CorpseApproachRangeMax = 10.0;
    public double CorpseApproachRangeMin = 2.0;

    public bool BoostNavPriority;
    public bool BoostLootPriority;
    public bool OpenDoors;
    public float OpenDoorRange = 5.0f;
    public bool AutoUnlockDoors;
    public int LootOwnership;
    /// <summary>
    /// Recover your own death corpse ("Corpse of &lt;you&gt;", identified by its "Killed by" line):
    /// when it is within loot range the bot opens it and takes every item on it, whatever the
    /// loot profile, Loot From, value or salvage rules say. On by default (owner, 2026-10-05);
    /// a profile saved before this setting loads it as on.
    /// </summary>
    public bool LootOwnCorpse = true;
    /// <summary>
    /// After a death and respawn, travel back to where you died (RynthNav, outdoors only), fight
    /// on the way as usual, recover the corpse, then carry on. Off by default.
    /// </summary>
    public bool TravelToOwnCorpse;
    public bool LootOnlyRareCorpses;
    public bool PeaceModeWhenIdle = true;
    public bool RebuffWhenIdle;
    /// <summary>
    /// Re-cast a self buff when its remaining duration drops below this many seconds.
    /// Default 300 (5 minutes). Lower values rebuff more eagerly; very low values
    /// (under ~60s) risk recasting between an old buff dropping and the new one
    /// landing.
    /// </summary>
    public int RebuffSecondsRemaining = 300;
    /// <summary>
    /// When a buff falls under RebuffSecondsRemaining, also refresh every other buff
    /// with less than this many seconds left, so they land together instead of each
    /// interrupting the fight on its own. Buffs with more time are left alone. At or
    /// below RebuffSecondsRemaining only the expiring buff is recast.
    /// </summary>
    public int RebuffTopOffSecondsRemaining = 1200;
    /// <summary>
    /// Cast our own buff even when an item already gives that buff (a permanent, item-granted
    /// enchantment at the same tier or higher). For servers with buff augments, where a player's
    /// own cast is stronger or lasts longer than the item's. On by default: on retail the cost
    /// is a few extra casts, while missing the buff is worse. A profile saved before this
    /// setting existed loads it as on. Off = an item-granted buff at the tier we'd cast counts
    /// as on for good (the behaviour before 2026-10-05).
    /// </summary>
    public bool CastBuffsOverItemBuffs = true;
    public bool StartMacroOnLogin;
    public bool PatrolOnLogin;
    /// <summary>
    /// Decal bridge only: when VTank's macro starts, RynthAi's macro stops, and it won't start
    /// while VTank runs (one bot per client). On by default; a profile saved before this
    /// setting existed loads it as on. Without Decal it does nothing.
    /// </summary>
    public bool YieldToVTank = true;

    public int BlacklistAttempts = 3;
    public int BlacklistTimeoutSec = 30;
    /// <summary>
    /// Grace after an offensive cast before it's judged a "miss". A cast only
    /// counts toward BlacklistAttempts if no damage landed on the target within
    /// this window — so blacklisting is driven by confirmed no-damage casts,
    /// not wall-clock time / damage-packet latency. Default 1500ms.
    /// </summary>
    public int BlacklistCastSettleMs = 1500;
    /// <summary>
    /// Blacklist a target after being engaged with it for this many seconds without
    /// dealing any damage. 0 = disabled. Default 60s.
    /// </summary>
    public int TargetNoProgressTimeoutSec = 0; // 0 = disabled; set to e.g. 120 to blacklist stuck targets

    /// <summary>
    /// User-configured "never attack" list. Any monster whose name CONTAINS one
    /// of these entries (case-insensitive) is excluded from combat target
    /// acquisition entirely — never scanned, faced, or attacked. This is a manual
    /// do-not-attack list, separate from the automatic no-damage blacklist above.
    /// Edited in Advanced → Combat → Monster Blacklist.
    /// </summary>
    public List<string> MonsterNameBlacklist { get; set; } = new();

    public int GiveQueueIntervalMs = 150;

    public int SpellCastIntervalMs = 400;

    /// <summary>
    /// Minimum delay between offensive (war/void) COMBAT spell casts, in ms.
    /// Combat magic uses this instead of <see cref="SpellCastIntervalMs"/> so
    /// attack pacing can be slowed (~1-2s) without slowing buff chains. Default
    /// 1500ms ("a second or two") — spacing offensive casts stops back-to-back
    /// "You're too busy!" refusals that drop casts and cost kills. ≤0 ⇒ 1500.
    /// </summary>
    public int AttackSpellIntervalMs = 1500;

    public int MeleeAttackPower = -1;
    public int MissileAttackPower = -1;
    /// <summary>
    /// Physical-attack path. FALSE (default since 2026-09-04) = the direct
    /// explicit-target path: Event_TargetedMelee/MissileAttack take the target
    /// id as an argument and never touch AC's single selection global
    /// (0x00871E54), so combat no longer clobbers the inventory item you have
    /// selected. TRUE = AC's native ClientCombatSystem pipeline, which has no
    /// target argument and therefore must be preceded by SelectItem(targetId) —
    /// that SelectItem was the last selection clobber in the combat hot path.
    ///
    /// This defaulted to true until now for one reason: the direct path does not
    /// turn the character to face the monster, and swings/arrows at the wrong
    /// heading go nowhere (ExplicitTarget_Combat_Findings_2026-06-03.md §5). The
    /// facing servo in CombatManager.OnHeartbeat now covers both melee and
    /// missile on the non-native path, which is exactly what that blocker asked
    /// for, so the default flips.
    ///
    /// Kept as a setting rather than hard-gated: the servo has not been soaked
    /// against a moving target across a long session, and re-ticking this box in
    /// the Advanced settings UI restores the old behaviour live, without a
    /// rebuild, if the swings look wrong.
    /// </summary>
    public bool UseNativeAttack = false;
    public bool UseRecklessness;
    /// <summary>
    /// Combat and buffing only ever wield weapons from the Items list. With this on (the
    /// default), a character whose Items list has no wand at all may still buff and cast with a
    /// caster from its pack (by object class, never by name). Any listed wand turns it off.
    /// </summary>
    public bool WieldUnlistedWandWhenNoneListed = true;
    /// <summary>
    /// The off hand (the Shield slot) when the monster rule doesn't choose one: "Auto" (a listed
    /// shield with a one-handed melee weapon), "Shield", "Weapon" (dual wield a second listed
    /// one-handed weapon; needs the Dual Wield skill) or "None" (never touched). A rule chooses
    /// with MonsterRule.OffhandId (OffhandRules.RuleAuto..RuleNone). See OffhandPlanner.
    /// </summary>
    public string OffhandDefault = "Auto";
    /// <summary>Auto: dual wield rather than a shield when the Dual Wield skill is trained and a
    /// second one-handed melee weapon is listed. Off by default (the shield).</summary>
    public bool PreferDualWield;
    public int MeleeAttackHeight = 1;
    public int MissileAttackHeight = 1;

    // Projectile arc velocities for missile-weapon LoS checks.
    // Lower velocity = higher arc. Must approximate the AC client trajectory
    // so dungeon-ceiling hits are detected.
    public bool  UseArcs              = true;
    public float BowArcVelocity       = 25.0f;
    public float CrossbowArcVelocity  = 40.0f;
    public float AtlatlArcVelocity    = 22.0f;
    /// <summary>
    /// Arc spells' HORIZONTAL speed (m/s). ACE flies every player arc at a fixed 40 m/s across
    /// the ground under gravity 9.8 (MissileBallistics.SolveLateral). Combat casts an Arc only
    /// when that path reaches the target, else the rule's other shape (or Bolt). Until
    /// 2026-10-04 this was a launch speed for a flat-ground model, default 25:
    /// <see cref="MigrateMagicArcVelocity"/> moves that old default to 40.
    /// </summary>
    public float MagicArcVelocity     = Raycasting.MissileBallistics.AceArcSpellSpeed;

    /// <summary>A saved MagicArcVelocity under today's model: the old default (25, a launch
    /// speed for the flat-ground model, which as a horizontal speed would make arcs 2.6x higher
    /// than ACE flies them) and nonsense values become 40.</summary>
    public static float MigrateMagicArcVelocity(float saved)
        => float.IsFinite(saved) && saved > 0f && saved != 25.0f ? saved : Raycasting.MissileBallistics.AceArcSpellSpeed;
    /// <summary>
    /// Extra headroom (meters) a missile's arc must have at mid-flight for LoS to pass,
    /// on top of the modelled flight path (launch speed above, AC gravity 9.8). Covers
    /// what the model can't know exactly: launch height, the real launch speed, the
    /// projectile's size. Raise it if shots still hit ceilings, lower it if targets
    /// under a ceiling the arrows clear are skipped. Only used with UseArcs.
    /// </summary>
    public float MissileArcClearance  = 0.5f;
    /// <summary>
    /// Log each in-range target's LoS verdict (straight line / arc, how high the arc
    /// rises, where it hits) and the missile weapon's known launch speed. For tuning
    /// the arc settings; off by default (a few lines a second in a crowd).
    /// </summary>
    public bool  LosDebugLog;

    public bool EnableFPSLimit = true;
    public int TargetFPSFocused = 60;
    public int TargetFPSBackground = 30;

    // Minimum buffed skill level required to cast spells of each tier — for COMBAT casts.
    // Defaults are tuned above AC's hard minimums (1/50/100/150/200/250/300/350) to avoid fizzles.
    public int MinSkillLevelTier1 = 35;
    public int MinSkillLevelTier2 = 85;
    public int MinSkillLevelTier3 = 135;
    public int MinSkillLevelTier4 = 185;
    public int MinSkillLevelTier5 = 235;
    public int MinSkillLevelTier6 = 285;
    public int MinSkillLevelTier7 = 335;
    public int MinSkillLevelTier8 = 435;

    // Minimum buffed skill level required to cast self-buffs of each tier.
    // Defaults match the spell combat thresholds so new installs work consistently.
    public int BuffMinSkillLevelTier1 = 35;
    public int BuffMinSkillLevelTier2 = 85;
    public int BuffMinSkillLevelTier3 = 135;
    public int BuffMinSkillLevelTier4 = 185;
    public int BuffMinSkillLevelTier5 = 235;
    public int BuffMinSkillLevelTier6 = 285;
    public int BuffMinSkillLevelTier7 = 335;
    public int BuffMinSkillLevelTier8 = 435;

    public bool EnableManaTapping   = false;
    public int  ManaTapMinMana      = 2500;
    public int  ManaStoneKeepCount  = 5;

    // ── AutoVendor (UtilityBelt AutoVendor; /ub autovendor, /ub vendor) ────────
    /// <summary>Run AutoVendor when a vendor opens (and allow /ub autovendor). UB defaults this on;
    /// RynthAi defaults it off because the plugin auto-updates and selling can't be undone.</summary>
    public bool AutoVendorEnabled = false;
    public bool AutoVendorEnableBuying = true;
    public bool AutoVendorEnableSelling = true;
    /// <summary>Only print what would be bought and sold. On by default in RynthAi (UB: off).</summary>
    public bool AutoVendorTestMode = true;
    /// <summary>Send "AutoVendor finished: ..." (and fatal/failed lines) as a /tell to yourself, for metas.</summary>
    public bool AutoVendorThink = false;
    public bool AutoVendorShowMerchantInfo = true;
    public bool AutoVendorOnlyFromMainPack = false;
    /// <summary>Attempts to open a vendor on /ub vendor open[p].</summary>
    public int AutoVendorTries = 4;
    /// <summary>Milliseconds between /ub vendor open attempts.</summary>
    public int AutoVendorTriesTime = 5000;

    // ── AutoTrade (UtilityBelt AutoTrade; /ub autotrade, /ra autotrade) ────────
    /// <summary>Fill the trade window from a loot profile when a trade opens (UB default: off).</summary>
    public bool AutoTradeEnabled = false;
    /// <summary>Only print what would be added to the trade window.</summary>
    public bool AutoTradeTestMode = false;
    /// <summary>Send "AutoTrade finished: ..." and "Trade accepted: ..." as a /tell to yourself, for metas.</summary>
    public bool AutoTradeThink = false;
    public bool AutoTradeOnlyFromMainPack = false;
    /// <summary>Accept the trade once every item AutoTrade added is in the window.</summary>
    public bool AutoTradeAutoAccept = false;
    /// <summary>Name patterns (.NET regex) whose accepted trades this character accepts too.
    /// The autoAcceptList.json files (/ub autotrade autoaccept ...) are read as well.</summary>
    public List<string> AutoTradeAutoAcceptChars { get; set; } = new();

    public List<MonsterRule> MonsterRules { get; set; } = new();
    public List<ItemRule> ItemRules { get; set; } = new();
    public List<ConsumableRule> ConsumableRules { get; set; } = new();
    public List<BuffRule> BuffRules { get; set; } = new();
    public List<MetaRule> MetaRules { get; set; } = new();

    /// <summary>
    /// Guards every read/enumerate/mutate of <see cref="MetaRules"/>. The list
    /// is touched from the plugin-tick thread (MetaManager.Think) AND the
    /// Avalonia dispatcher thread (MetaPanel poll → BuildMetaJson /
    /// HandleMetaCommand). List&lt;T&gt; is not thread-safe; concurrent
    /// enumerate+mutate corrupts the heap. Hold this lock for the full duration
    /// of any MetaRules access.
    /// </summary>
    [JsonIgnore]
    public object MetaRulesLock { get; } = new();

    /// <summary>
    /// Bumped on an *in-place* rule edit (a slot replaced, possibly changing a
    /// rule's State) so MetaManager's per-state index rebuilds. Add/delete/load
    /// change the list ref or count and are detected without this.
    /// </summary>
    [JsonIgnore]
    public int MetaRulesStructuralVersion;

    private Dictionary<string, List<string>> _embeddedNavs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// NAV routes embedded inside the currently-loaded .af/.met macro,
    /// keyed by name. Persisted so meta EmbedNav actions still resolve
    /// after a client restart without reloading the source file.
    /// Setter re-wraps incoming dict with OrdinalIgnoreCase comparer so
    /// JSON round-trip keeps case-insensitive lookups working.
    /// </summary>
    public Dictionary<string, List<string>> EmbeddedNavs
    {
        get => _embeddedNavs;
        set => _embeddedNavs = value != null
            ? new Dictionary<string, List<string>>(value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }
    /// <summary>
    /// Set once this profile's weapons and consumables have been checked against
    /// the character's own pack (new profiles are filled from it; older ones had
    /// entries copied from another character removed). See TickPackSetup.
    /// </summary>
    public bool ProfileItemsChecked;

    public string SelectedProfile = "Default";
    [JsonIgnore] public NavRouteParser CurrentRoute { get; set; } = new();
    public int ActiveNavIndex;

    public bool ShowAdvancedWindow;
    public int SelectedAdvancedTab;

    // ── Persisted window/UI state ───────────────────────────────────────────
    public float WindowPosX = -1f;
    public float WindowPosY = -1f;
    public float WindowSizeX = 430f;
    public float WindowSizeY = 452f;
    public bool WindowLocked;
    public bool DashboardVisible = true;
    public bool DashboardMinimized;
    public float BgOpacity = 0.95f;
    public bool DashShowWeapons;
    public bool DashShowLua;
    public bool DashShowNavigation;
    public bool DashShowMacroRules;
    public bool DashShowMonsters;
    public bool DashShowDungeonMap;
    public bool  MapShowDoors         = true;
    public bool  MapShowCreatures     = true;
    public bool  MapShowToolbar       = true;
    public float MapBgOpacity         = 1.0f;
    public bool  MapRotateWithPlayer  = false;

    // Radar dungeon-wall overlay
    public bool  ShowRadarWalls        = false; // WIP — renders as black box, disabled by default
    public float RadarWallWorldRange   = 35f; // world units shown edge-to-edge on the radar

    // When true, the engine suppresses the vanilla retail radar entirely.
    public bool  SuppressRetailRadar   = false;

    // When true, the engine no-ops the gmPowerbarUI notices so the vanilla
    // attack/magic power bar never appears on screen.
    public bool  SuppressRetailPowerbar = false;

    // Custom chat viewer
    public bool  ShowRynthChat         = false;
    public float ChatOpacity           = 0.15f;
    public int   ChatMaxLines          = 500;
    public bool  ChatShowTimestamps    = false;
    public bool  ChatClickThrough      = false;   // mouse passes through to game; gear stays clickable

    // When true, the custom RynthRadar widget is rendered.
    public bool  ShowRynthRadar        = false;
    public bool  RadarRotateWithPlayer = false;   // false = north-up; true = player always faces up
    public float RadarOpacity          = 0.85f;
    public float RadarZoom             = 3.5f;    // pixels per world unit
    public bool  RadarShowMonsters     = true;
    public bool  RadarShowNpcs         = true;
    public bool  RadarShowPortals      = true;
    public bool  RadarShowDoors        = true;
    // Grid-cell radius (0.5m per cell) of the "visited" paint stamp placed each
    // frame around the player. Higher = walls light up from further away.
    public int   RadarWallPaintRadius  = 3;
    public bool  RadarCircular         = false;   // false = square frame, true = circular
    public bool  RadarClickThrough     = false;   // mouse passes through to the game
    // Persisted radar window placement. -1 = never saved; the first session
    // applies the default size and captures the position once it's shown.
    public float RadarPosX             = -1f;
    public float RadarPosY             = -1f;
    public float RadarSizeX            = 260f;
    public float RadarSizeY            = 284f;

    [JsonIgnore]
    public readonly string[] AdvancedTabs =
    {
        "Display", "UI", "Misc", "Recharge", "Melee Combat", "Spell Combat",
        "Ranges", "Navigation", "Buffing", "Crafting", "Looting", "Vendoring"
    };

    [JsonIgnore]
    public bool ForceStateReset { get; set; }

    /// <summary>Set when a meta file is loaded: the next meta tick empties the
    /// CallState/Return stack.</summary>
    [JsonIgnore]
    public bool MetaCallStackReset { get; set; }

    [JsonIgnore]
    public string NavStatusLine = string.Empty;

    [JsonIgnore]
    public bool NavIsStuck = false;

    // Runtime-only: the object id of the portal/NPC the nav engine is actively
    // using (resolved by FirePortalNpcUse), or 0 when not using one. Lets the
    // marker renderer draw a ring + line to the portal's REAL position, since
    // the PortalNPC waypoint's stored coordinate is an unreliable placeholder.
    [JsonIgnore]
    public uint ActivePortalObjId = 0;

    // Fellowship-follow: when on, nav steers toward the live target instead of a
    // route. Runtime-only (off each session; enable via /ra follow on).
    [JsonIgnore]
    public bool FollowMode = false;

    // Resolved object id of the follow target (the fellowship leader), published
    // by the plugin each tick; 0 when not in a fellowship / target not loaded.
    [JsonIgnore]
    public uint FollowTargetId = 0;
    /// <summary>A character picked by name (/ra follow Name, /ub follow[p]); wins over the fellowship leader. Session only.</summary>
    [JsonIgnore] public uint FollowNamedTargetId;

    public LegacyUiSettings()
    {
    }

    public void EnsureDefaultRule()
    {
        List<MonsterRule> existingDefaults = MonsterRules.Where(m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)).ToList();
        MonsterRule trueDefault;

        if (existingDefaults.Count > 0)
        {
            trueDefault = existingDefaults[0];
            MonsterRules.RemoveAll(m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            trueDefault = new MonsterRule { Name = "Default", Priority = 1, DamageType = "Auto", WeaponId = 0 };
        }

        MonsterRules.Insert(0, trueDefault);
    }
}

public sealed class MonsterRule
{
    public string Name { get; set; } = "New Monster";
    public string Category { get; set; } = "";
    public string MatchExpression { get; set; } = "";
    public int Priority { get; set; } = 1;
    public string DamageType { get; set; } = "Auto";
    public int WeaponId { get; set; }
    public bool Fester { get; set; }
    public bool Broadside { get; set; }
    public bool GravityWell { get; set; }
    public bool Imperil { get; set; }
    public bool Yield { get; set; }
    public bool Vuln { get; set; }
    public bool UseArc { get; set; }
    public bool UseRing { get; set; }
    public bool UseStreak { get; set; }
    public bool UseBolt { get; set; } = true;
    /// <summary>Blast spells (Flame Blast, Frost Blast...: a spread of projectiles). Off in files
    /// from before 2026-10-03. Wins over Bolt when both are on; see CombatManager.PickBaseShape.</summary>
    public bool UseBlast { get; set; }
    public string ExVuln { get; set; } = "None";
    /// <summary>The off hand for this rule: 0 = the global setting (OffhandDefault), 1 Auto,
    /// 2 Shield, 3 Offhand weapon, 4 None (OffhandRules.Rule*); any other value is a listed
    /// item to wield there (the old per-rule picker).</summary>
    public int OffhandId { get; set; }
    public string PetDamage { get; set; } = "PAuto";
    /// <summary>Debuffs the player typed in (Damage panel), comma separated: a spell's base
    /// name ("Corrosion Vulnerability Other") casts its best known tier; a full name casts as is.</summary>
    public string CustomDebuffs { get; set; } = "";
}

public sealed class BuffRule
{
    public string SpellName { get; set; } = string.Empty;
    public int SpellId { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class ItemRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Action { get; set; } = "Loot";
    public string Element { get; set; } = "Slash";
    /// <summary>Where <see cref="Element"/> came from: "properties" (DamageType after an identify),
    /// "icon" (UiEffects), "rending" (an imbue), "name", "set" (picked in the Items panel), or ""
    /// (not known, or an entry from before sources were kept).</summary>
    public string ElementSource { get; set; } = "";
    public bool KeepBuffed { get; set; } = true;
}

/// <summary>JSON wire-format types used by the engine-side Avalonia MonstersPanel.</summary>
public sealed class MonstersBridgePayload
{
    public List<MonsterRule> Rules { get; set; } = new();
    public List<MonsterBridgeItem> Items { get; set; } = new();
    public string CurrentTargetName { get; set; } = string.Empty;
    /// <summary>Captured creature data per rule name (uppercased), filled by the plugin.</summary>
    public Dictionary<string, MonsterCapturedInfo> Captured { get; set; } = new();
}

public sealed class MonsterBridgeItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class MonsterCapturedInfo
{
    public uint MaxHealth { get; set; }
    public int  ArmorLevel { get; set; }
    public string WeakestType { get; set; } = string.Empty;
    public double WeakestValue { get; set; } = 1.0;
    public int Samples { get; set; }
}

public sealed class ConsumableRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "General";
}

/// <summary>Bridge payload for the engine-side Avalonia ItemsPanel.</summary>
public sealed class ItemsBridgePayload
{
    public List<ItemRule>       Weapons           { get; set; } = new();
    public List<ConsumableRule> Consumables       { get; set; } = new();
    public bool                 EnableManaTapping { get; set; }
    public int                  ManaTapMinMana    { get; set; }
    public int                  ManaStoneKeepCount { get; set; }
    public string               CurrentTargetName { get; set; } = string.Empty;
}

// ── Nav bridge types ─────────────────────────────────────────────────────────

public sealed class NavBridgePoint
{
    public int    Idx    { get; set; }
    public string Type   { get; set; } = string.Empty;  // "Point", "Recall", "Pause", "Chat", "PortalNPC"
    public string Desc   { get; set; } = string.Empty;  // NavPoint.ToString()
    public string Text   { get; set; } = string.Empty;  // Chat points: the command, for editing
    public double NS     { get; set; }
    public double EW     { get; set; }
    public double Z      { get; set; }
}

public sealed class NavBridgePayload
{
    public string            ActiveNavName    { get; set; } = string.Empty;
    public string            NavStatusLine    { get; set; } = string.Empty;
    public bool              NavIsStuck       { get; set; }
    public bool              MacroRunning     { get; set; }
    public bool              NavigationEnabled{ get; set; }
    public int               RouteType        { get; set; }  // 1=Circular,2=Linear,3=Follow,4=Once
    public int               ActiveNavIndex   { get; set; }
    public List<string>      NavFiles         { get; set; } = new();
    public List<NavBridgePoint> Points        { get; set; } = new();
}

/// <summary>One-shot command sent from the Avalonia NavPanel to the plugin.</summary>
public sealed class NavCommand
{
    public string Cmd       { get; set; } = string.Empty;
    public int    SpellId   { get; set; }
    public int    Index     { get; set; }
    public int    RouteType { get; set; }
    public int    AddMode   { get; set; }   // 0=End, 1=Above, 2=Below
    public int    InsertAt  { get; set; } = -1;
    public string NavName   { get; set; } = string.Empty;
    public string Text      { get; set; } = string.Empty;   // addChat: the command or text
}

/// <summary>Bridge payload for the engine-side Avalonia SettingsPanel.</summary>
public sealed class SettingsBridgePayload
{
    // Display
    public bool ShowTargetStaminaMana { get; set; }

    // UI
    public bool SuppressRetailRadar { get; set; }
    public bool ShowRynthRadar { get; set; }
    public bool RadarClickThrough { get; set; }
    public bool ShowRynthChat { get; set; }
    public bool ChatClickThrough { get; set; }
    public bool SuppressRetailPowerbar { get; set; }

    // Misc
    public bool EnableFPSLimit { get; set; }
    public int TargetFPSFocused { get; set; }
    public int TargetFPSBackground { get; set; }
    public bool EnableAutocram { get; set; }
    public bool PeaceModeWhenIdle { get; set; }
    public bool StartMacroOnLogin { get; set; }
    public bool PatrolOnLogin { get; set; }
    // Absent from an older engine's payload: must not read as off.
    public bool YieldToVTank { get; set; } = true;
    public bool EnableRaycasting { get; set; }
    public bool UseArcs { get; set; }
    public float BowArcVelocity { get; set; }
    public float CrossbowArcVelocity { get; set; }
    public float AtlatlArcVelocity { get; set; }
    public float MagicArcVelocity { get; set; }
    public float MissileArcClearance { get; set; } = 0.5f;   // absent in older payloads
    public bool LosDebugLog { get; set; }
    public int BlacklistAttempts { get; set; }
    public int BlacklistTimeoutSec { get; set; }
    // -1 = absent from the payload: the overlay's Settings panel didn't send it, and
    // applying the missing 0 made every panel click zero the setting (2026-09-27).
    public int BlacklistCastSettleMs { get; set; } = -1;
    public int TargetNoProgressTimeoutSec { get; set; }
    public int GiveQueueIntervalMs { get; set; }

    // Recharge
    public int HealAt { get; set; }
    public bool UsePotions { get; set; }
    public bool UseBuffItems { get; set; } = true;
    public int MakeRationsBelow { get; set; } = 5;
    public bool UseKitsInMagicMode { get; set; }
    public bool PeaceModeForKits { get; set; }
    public int KitMinSuccessPct { get; set; } = 70;
    public int EmergencyHealAt { get; set; } = 30;
    public int StaminaToHealthAt { get; set; } = 30;
    public int StaminaToHealthMinStamina { get; set; } = 20;
    public bool StopMacroOnDeath { get; set; }
    public bool StopMacroOnNoComponents { get; set; }
    public bool StopLootingWhenPackFull { get; set; }
    public bool StopMacroWhenPackFull { get; set; }
    public int RestamAt { get; set; }
    public int GetManaAt { get; set; }
    public int TopOffHP { get; set; }
    public int TopOffStam { get; set; }
    public int TopOffMana { get; set; }
    public int HealOthersAt { get; set; }
    public int RestamOthersAt { get; set; }
    public int InfuseOthersAt { get; set; }

    // Melee Combat
    public bool UseRecklessness { get; set; }
    public int MeleeAttackPower { get; set; }
    public int MeleeAttackHeight { get; set; }
    public int MissileAttackPower { get; set; }
    public int MissileAttackHeight { get; set; }
    public bool UseNativeAttack { get; set; }
    public bool SummonPets { get; set; }
    public int PetMinMonsters { get; set; }

    // Spell Combat
    public int SpellCastIntervalMs { get; set; }
    public int AttackSpellIntervalMs { get; set; }
    public bool CastDispelSelf { get; set; }
    public int MinRingTargets { get; set; }
    public int BlastRange { get; set; }
    public int MinBlastTargets { get; set; } = 3;   // absent from an older engine: keep the default
    public int MinSkillLevelTier1 { get; set; }
    public int MinSkillLevelTier2 { get; set; }
    public int MinSkillLevelTier3 { get; set; }
    public int MinSkillLevelTier4 { get; set; }
    public int MinSkillLevelTier5 { get; set; }
    public int MinSkillLevelTier6 { get; set; }
    public int MinSkillLevelTier7 { get; set; }
    public int MinSkillLevelTier8 { get; set; }

    // Ranges
    public int MonsterRange { get; set; }
    public int MonsterDisengageRange { get; set; } = -1;   // -1 = absent, see BlacklistCastSettleMs
    public int RingRange { get; set; }
    public int ApproachRange { get; set; }
    public double CorpseApproachRangeMax { get; set; }
    public double CorpseApproachRangeMin { get; set; }

    // Navigation
    public bool BoostNavPriority { get; set; }
    public float FollowNavMin { get; set; }
    public float NavRingThickness { get; set; }
    public float NavLineThickness { get; set; }
    public float NavHeightOffset { get; set; }
    public float NavSlopeSink { get; set; }
    public bool ShowTerrainPassability { get; set; }
    public bool OpenDoors { get; set; }
    public float OpenDoorRange { get; set; }
    public bool AutoUnlockDoors { get; set; }
    public int MovementMode { get; set; }
    public float NavStopTurnAngle { get; set; }
    public float NavResumeTurnAngle { get; set; }
    public float NavDeadZone { get; set; }
    public float NavSweepMult { get; set; }
    public float NavLookaheadYards { get; set; }
    public float NavShortcutYards { get; set; }
    public float NavTurnRateDegPerSec { get; set; }
    public float NavTier1TurnSpeed { get; set; }
    public float PostPortalDelaySec { get; set; }
    public float T2Speed { get; set; }
    public float T2WalkWithinYd { get; set; }
    public float T2DistanceTo { get; set; }
    public float T2ReissueMs { get; set; }
    public float T2MaxRangeYd { get; set; }
    public int T2MaxLandblocks { get; set; }

    // Buffing
    public bool EnableBuffing { get; set; }
    public bool RebuffWhenIdle { get; set; }
    public int RebuffSecondsRemaining { get; set; }
    public int RebuffTopOffSecondsRemaining { get; set; } = 1200;   // absent in older files
    // Nullable: an engine that predates the setting doesn't send it, and a save from its
    // Settings face must leave the setting alone rather than force it either way.
    public bool? CastBuffsOverItemBuffs { get; set; }
    public int BuffMinSkillLevelTier1 { get; set; }
    public int BuffMinSkillLevelTier2 { get; set; }
    public int BuffMinSkillLevelTier3 { get; set; }
    public int BuffMinSkillLevelTier4 { get; set; }
    public int BuffMinSkillLevelTier5 { get; set; }
    public int BuffMinSkillLevelTier6 { get; set; }
    public int BuffMinSkillLevelTier7 { get; set; }
    public int BuffMinSkillLevelTier8 { get; set; }

    // Crafting (read-only state)
    public bool EnableMissileCrafting { get; set; }
    public string MissileCraftingState { get; set; } = string.Empty;
    public bool MissileCraftingActive { get; set; }
    public string MissileCraftingStatus { get; set; } = string.Empty;

    // Looting
    public bool EnableLooting { get; set; }
    public bool BoostLootPriority { get; set; }
    public bool LootOnlyRareCorpses { get; set; }
    public bool LootJumpEnabled { get; set; }
    public int LootJumpHeight { get; set; }
    public int LootOwnership { get; set; }
    // Nullable, as CastBuffsOverItemBuffs: an engine that predates them doesn't send them,
    // and its save must leave them alone.
    public bool? LootOwnCorpse { get; set; }
    public bool? TravelToOwnCorpse { get; set; }
    public bool EnableAutostack { get; set; }
    public bool ReadUnknownScrolls { get; set; } = true;   // absent from an older engine: keep the default
    public bool EnableCombineSalvage { get; set; }
    public bool CombineBagsDuringSalvage { get; set; }
    public int LootInterItemDelayMs { get; set; }
    public int LootContentSettleMs { get; set; }
    public int LootEmptyCorpseMs { get; set; }
    public int LootClosingDelayMs { get; set; }
    public int LootAssessWindowMs { get; set; }
    public int LootRetryTimeoutMs { get; set; }
    public int LootOpenRetryMs { get; set; }
    public int LootCorpseTimeoutMs { get; set; }
    public int SalvageOpenDelayFirstMs { get; set; }
    public int SalvageOpenDelayFastMs { get; set; }
    public int SalvageAddDelayFirstMs { get; set; }
    public int SalvageAddDelayFastMs { get; set; }
    public int SalvageSalvageDelayMs { get; set; }
    public int SalvageResultDelayFirstMs { get; set; }
    public int SalvageResultDelayFastMs { get; set; }
    // AutoVendor. Nullable so a settings panel that doesn't know these fields yet
    // leaves the character's values alone instead of resetting them.
    public bool? AutoVendorEnabled { get; set; }
    public bool? AutoVendorEnableBuying { get; set; }
    public bool? AutoVendorEnableSelling { get; set; }
    public bool? AutoVendorTestMode { get; set; }
    public bool? AutoVendorThink { get; set; }
    public bool? AutoVendorShowMerchantInfo { get; set; }
    public bool? AutoVendorOnlyFromMainPack { get; set; }
    public int? AutoVendorTries { get; set; }
    public int? AutoVendorTriesTime { get; set; }
    // The off hand (only when the sender includes them; today's engine Settings face doesn't).
    public string? OffhandDefault { get; set; }
    public bool? PreferDualWield { get; set; }
}

public enum MetaConditionType
{
    Never,
    Always,
    All,
    Any,
    ChatMessage,
    PackSlots_LE,
    SecondsInState_GE,
    CharacterDeath,
    AnyVendorOpen,
    VendorClosed,
    InventoryItemCount_LE,
    InventoryItemCount_GE,
    MonsterNameCountWithinDistance,
    MonsterPriorityCountWithinDistance,
    NeedToBuff,
    NoMonstersWithinDistance,
    Landblock_EQ,
    Landcell_EQ,
    PortalspaceEntered,
    PortalspaceExited,
    Not,
    SecondsInStateP_GE,
    TimeLeftOnSpell_GE,
    TimeLeftOnSpell_LE,
    BurdenPercentage_GE,
    DistAnyRoutePT_GE,
    Expression,
    ChatMessageCapture,
    NavrouteEmpty,
    MainHealthLE,
    MainHealthPHE,
    MainManaLE,
    MainManaPHE,
    MainStamLE,
    VitaePHE
}

public enum MetaActionType
{
    None,
    ChatCommand,
    SetMetaState,
    EmbeddedNavRoute,
    All,
    CallMetaState,
    ReturnFromCall,
    ExpressionAction,
    ChatExpression,
    SetWatchdog,
    ClearWatchdog,
    GetRAOption,
    SetRAOption,
    CreateView,
    DestroyView,
    DestroyAllViews
}

public sealed class MetaRule
{
    public string State { get; set; } = "Default";
    public MetaConditionType Condition { get; set; }
    public string ConditionData { get; set; } = string.Empty;
    public MetaActionType Action { get; set; }
    public string ActionData { get; set; } = string.Empty;
    public List<MetaRule> Children { get; set; } = new();
    public List<MetaRule> ActionChildren { get; set; } = new();

    /// <summary>CallMetaState only: the state pushed on the call stack (VTank's
    /// "ret"). Empty means the calling rule's current state, as before.</summary>
    public string CallReturnState { get; set; } = string.Empty;

    /// <summary>ChatMessageCapture only: VTank's colour (chat type) id list,
    /// semicolon-separated ("2;4"). Empty matches every chat type.</summary>
    public string ChatColors { get; set; } = string.Empty;

    /// <summary>Disabled rules are excluded from the per-state index so they
    /// never evaluate. Default true; missing in old JSON/.af → stays true
    /// (the property initializer runs, so backward-compatible).</summary>
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public bool HasFired { get; set; }

    /// <summary>Last time this rule's action executed — drives the red-flash UI.</summary>
    [JsonIgnore]
    public DateTime LastFiredAt { get; set; } = DateTime.MinValue;
}
