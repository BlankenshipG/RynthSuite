// IltHubState.cs — Persisted per-character state for the ILT Hub (Infinite Leaftide tools).
//
// Lives in <charFolder>\ilt-hub.json, deliberately separate from the combat settings
// profile (Default.json / /ra settings) so ILT data never leaks into combat profiles and
// non-ILT characters stay untouched. Serialized with the NativeAOT source-generated
// RynthAiJsonContext (IncludeFields = true), so plain public fields are used throughout.
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.IltHub;

/// <summary>Root of everything the Hub remembers between sessions.</summary>
public sealed class IltHubState
{
    /// <summary>Bumped when the shape changes in a way that needs a migration.</summary>
    public int SchemaVersion = 1;

    // ── Window / shell ──────────────────────────────────────────────────────
    /// <summary>
    /// Hub UI on screen: the Mini Remote or any ILT section window (IltHubController.Tick keeps it
    /// current). Bank auto-refresh and the gear / split-arrow scans run only while it is true.
    /// </summary>
    public bool WindowVisible;
    /// <summary>Unused since the tabbed Hub window was retired; kept so old files still load.</summary>
    public int SelectedTab;
    public bool GamesHudVisible = true;

    /// <summary>
    /// "This world is an ACECustom/ILT shard even though its name isn't InfiniteLeaftide."
    /// World-identity override ONLY: it makes the Hub ask the server for its options, it
    /// never turns a feature on by itself.
    /// </summary>
    public bool ForceLeaftideFeatures;

    /// <summary>True once the one-time UtilityBelt sidecar import has run for this character.</summary>
    public bool UbSidecarsImported;

    /// <summary>Last-seen server feature bits. Cache only — overwritten by every login refresh.</summary>
    public IltServerOptionsSnapshot ServerOptions = new();

    public IltBankState Bank = new();
    public IltPetState Pet = new();
    public IltCharacterState Character = new();
    public IltGearState Gear = new();
    public IltGamesState Games = new();
    public IltGuardianState Guardian = new();
    public IltBountyState Bounty = new();
}

/// <summary>Cached result of the server-options check (see IltServerOptions).</summary>
public sealed class IltServerOptionsSnapshot
{
    public string WorldName = string.Empty;
    /// <summary>"ilt-features" (structured dump), "probe" (command probing) or "" (never refreshed).</summary>
    public string Source = string.Empty;
    public DateTime LastRefreshUtc = DateTime.MinValue;
    /// <summary>Feature key → -1 unknown, 0 off, 1 on.</summary>
    public Dictionary<string, int> Bits = new(StringComparer.OrdinalIgnoreCase);
}

// ── Banking ─────────────────────────────────────────────────────────────────

public sealed class IltBankState
{
    public long Pyreals;
    public long Luminance;
    public long LegendaryKeys;
    public long MythicalKeys;
    public long EnlightenedCoins;
    public long WeaklyEnlightenedCoins;
    public DateTime LastUpdated = DateTime.MinValue;

    /// <summary>Last transfer recipient typed into the transfer form.</summary>
    public string TransferTarget = string.Empty;
    public int TransferCurrencyIndex;

    /// <summary>Re-issue /bank every N seconds while the Hub is open (0 = manual only).</summary>
    public int AutoRefreshSeconds = 120;
}

/// <summary>One "Transferred …" / "Received …" bank line seen in chat.</summary>
public sealed class IltBankTransaction
{
    public DateTime Timestamp;
    /// <summary>"Sent" or "Received".</summary>
    public string Type = string.Empty;
    public string Currency = string.Empty;
    public long Amount;
    public string OtherPlayer = string.Empty;
    public string Description = string.Empty;
}

// ── Pet ─────────────────────────────────────────────────────────────────────

public sealed class IltPetState
{
    // Healing-pet automation (UB "HealingBuddy" subset).
    public bool HealingEnabled;
    /// <summary>Exact name of the heal-pet essence to use; empty = auto-detect (Healing Buddy / Dule box).</summary>
    public string HealPetName = string.Empty;
    public int HealthThresholdPercent = 80;
    /// <summary>Do not summon when current HP is below this (summoning mid-death wastes the use). 0 = off.</summary>
    public int MinHealthPoints = 100;
    public bool AutoDespawnAfterHeal = true;
    public int RespawnDelaySeconds = 2;
    public int PetTimeoutSeconds = 30;

    /// <summary>
    /// Allow a combat summon from an EMPTY essence when the Summon Essence Refill Charm is
    /// active (server refills from banked pyreals). Still requires the server flag to be on.
    /// </summary>
    public bool UsePyrealRefillPath;

    /// <summary>Last captured /pets and /shinies rosters (display only).</summary>
    public List<string> PetLog = new();
    public List<string> ShinyLog = new();

    // ── Pet roster (UB Pets-tab style) ──
    /// <summary>Roster type shown in the Pet tab (IltPetKind: 0 Combat, 1 Healing, 2 Cosmetic).</summary>
    public int RosterKind;
    /// <summary>Roster sort (IltPetSort: 0 Priority, 1 Bond, 2 Potency, 3 Breed ready, 4 Level, 5 Uses, 6 Name).</summary>
    public int RosterSort;
    /// <summary>User type overrides by essence name; essences not listed are auto-classified.</summary>
    public List<IltPetAssignment> Assignments = new();

    /// <summary>Essence name of the chosen cosmetic (display) pet; empty = none.</summary>
    public string CosmeticPetName = string.Empty;
    /// <summary>Keep the cosmetic pet summoned while in peace mode; it is dismissed when a combat pet is needed.</summary>
    public bool KeepCosmeticOut;
    /// <summary>Seconds to wait before re-summoning the cosmetic pet after it despawns.</summary>
    public int CosmeticRespawnSeconds = 15;
}

/// <summary>A user's choice of summon type for one essence (matched by name, case-insensitive).</summary>
public sealed class IltPetAssignment
{
    public string Name = string.Empty;
    /// <summary>IltPetKind as int (0 Combat, 1 Healing, 2 Cosmetic).</summary>
    public int Kind;
}

// ── Character ───────────────────────────────────────────────────────────────

public sealed class IltCharacterState
{
    /// <summary>Item → coin conversion table used by the session-rates strip.</summary>
    public List<IltItemConversion> ItemConversions = new()
    {
        new IltItemConversion { ItemName = "Strawberry Jam Jar", ItemsPerConversion = 100, CoinsPerConversion = 10 },
        new IltItemConversion { ItemName = "Ancient Pyreal",     ItemsPerConversion = 100, CoinsPerConversion = 37 },
    };

    /// <summary>Augmentation planner targets, keyed by aug type (lowercase).</summary>
    public Dictionary<string, int> AugTargets = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Luminance price of one Enlightened Coin (planner math only; 0 = don't price coins).</summary>
    public long LumPerEnlightenedCoin;

    /// <summary>Enlightenment planner target level (0 = current + 5).</summary>
    public int EnlTargetLevel;
    public int EnlightenedCoinsPerToken = 20;
    /// <summary>
    /// Auto-enlighten preference. Even when true it stays DISARMED each session until the
    /// player confirms in the Hub, and the server's own Yes/No dialog is never auto-clicked.
    /// </summary>
    public bool AutoEnlightenEnabled;
    public int AutoEnlightenCheckSeconds = 30;
    /// <summary>Spend unassigned XP with /attr before sending /enl (enlightening wipes unspent XP).</summary>
    public bool AutoEnlightenSpendXpFirst;

    // ── Attribute raiser (IltAttributeRaiser; Skills panel > Progression) ──
    /// <summary>Spend unassigned XP on the ticked attributes/vitals every AttrAutoRaiseMinutes.</summary>
    public bool AttrAutoRaise;
    public int AttrAutoRaiseMinutes = 5;
    /// <summary>IltAttributeRaiser.Mode: 0 priority (top of the order first), 1 round robin, 2 cheapest first.</summary>
    public int AttrRaiseMode;
    /// <summary>Unassigned XP the raiser never spends.</summary>
    public long AttrKeepXp;
    /// <summary>/attr abbreviations in raise-priority order (all nine; repaired on load).</summary>
    public List<string> AttrOrder = new(IltAttributeRaiser.DefaultOrder);
    /// <summary>/attr abbreviations the raiser may spend on ("Raise now" and auto-raise).</summary>
    public List<string> AttrRaiseStats = new();

    /// <summary>Quest Tracker search box contents.</summary>
    public string QuestFilter = string.Empty;
    public string QbFilter = string.Empty;

    /// <summary>Starred quest flag keys (lowercase), shown on the floating favorites HUD.</summary>
    public List<string> QuestFavorites = new();
    /// <summary>Quest tracker lists starred quests only.</summary>
    public bool QuestFavoritesOnly;
    /// <summary>Floating quest favorites HUD is shown.</summary>
    public bool ShowQuestFavoritesHud;
    public bool QuestFavoritesHudLocked;
    // ILT section windows, opened from the Mini Remote's Options (IltSections).
    /// <summary>The "Quests" window (tracker + quest bonus) is open.</summary>
    public bool QuestTrackerPoppedOut;
    /// <summary>The "Pets" window is open.</summary>
    public bool PetsWindowOpen;
    /// <summary>The "ILT Character" window (world status, session rates) is open.</summary>
    public bool CharacterWindowOpen;
    /// <summary>The "Banking" window is open.</summary>
    public bool BankingWindowOpen;
    /// <summary>The "Gear" window is open.</summary>
    public bool GearWindowOpen;
    /// <summary>The "Games" window is open.</summary>
    public bool GamesWindowOpen;
    /// <summary>The "Guardian" window (riddle translator, attribute tracker) is open.</summary>
    public bool GuardianWindowOpen;
    /// <summary>The "Bounties" window (every ACECustom bounty and this character's progress) is open.</summary>
    public bool BountiesWindowOpen;

    /// <summary>
    /// Registry charms this character has carried, keyed by charm name. Lets the Charms Tracking
    /// tab report "acquired" for charms that are now in storage (the client only sees carried items).
    /// </summary>
    public Dictionary<string, IltCharmSeen> CharmsSeen = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Last time a charm was seen in the character's possession, with its best tier.</summary>
public sealed class IltCharmSeen
{
    public int Tier;
    public int MaxTier;
    public DateTime LastSeenUtc;
}

public sealed class IltItemConversion
{
    public string ItemName = string.Empty;
    public int ItemsPerConversion = 100;
    public int CoinsPerConversion;
}

// ── Gear ────────────────────────────────────────────────────────────────────

public sealed class IltGearState
{
    // Dispel (Rune of Dispel on harmful debuffs)
    public bool AutoDispel;
    public int DispelCooldownSeconds = 10;
    /// <summary>Spell-name substrings that are ALWAYS dispelled.</summary>
    public List<string> DispelInclusions = new();
    /// <summary>Spell-name substrings that are NEVER dispelled.</summary>
    public List<string> DispelExclusions = new();

    // Surging Strength (Empowered Volcanic Ember keeper)
    public bool SurgingStrength;
    public int SurgingCooldownSeconds = 15;

    // Aetheria / corrupted-weapon chunking
    public bool AutoChunkAetheria;
    public bool AutoChunkCorrupted;
    public bool ChunkInCombat;
    public int ChunkIntervalMs = 2000;
    /// <summary>Only chunk aetheria whose max level is below this (Coalesced always chunks).</summary>
    public int ChunkAetheriaUnderLevel = 4;

    // Auto-clap (/clap all)
    public bool AutoClap;
    public int ClapIntervalMinutes = 30;

    // Guardian Hand charm (WCID unpublished — name match until set)
    public uint GuardianHandWcid;
    public string GuardianHandName = "Guardian Hand";

    // Split arrows (White Quartz imbue on missile weapons)
    public int SplitArrowTarget = 3;
    public bool SplitIgnoreImbued = true;

    // Equipment profiles (.utl suits) and VTank .usd import
    public string LastEquipProfile = string.Empty;
    public string LastUsdPath = string.Empty;
}

// ── Games ───────────────────────────────────────────────────────────────────

public sealed class IltGamesState
{
    public long BlackjackBet = 1_000_000;
    public long FellowshipBlackjackBet = 1_000_000;
    public int PowerballQuantity = 1;
}

// ── Guardian (Temple of Enlightenment) ──────────────────────────────────────

public sealed class IltGuardianState
{
    /// <summary>Translate incoming guardian tells and show the answer (Guardian window + Mini Remote).</summary>
    public bool AutoDetectFromChat = true;
    /// <summary>After a chat answer, give the item to the guardian automatically.</summary>
    public bool AutoHandIn;
    /// <summary>Temple guardian only: buy one of the item from the nearest vendor when none is carried.</summary>
    public bool BuyMissingFromVendor = true;

    /// <summary>Assumed attribute turn-in cooldown when only /qb wait stamps are known (hours).</summary>
    public int CooldownHoursAssumed = 20;
    /// <summary>Turn-ins counted from /qb wait-stamp transitions, keyed by attribute (fallback when /myquests lacks the flag).</summary>
    public Dictionary<string, int> TurnInCounts = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Estimated end of each attribute's cooldown (UTC), keyed by attribute (same fallback).</summary>
    public Dictionary<string, DateTime> CooldownUntilUtc = new(StringComparer.OrdinalIgnoreCase);
}

// ── Bounties (ACECustom /bounty list) ───────────────────────────────────────

public sealed class IltBountyState
{
    /// <summary>Re-read "/bounty list" after this character's kills while the Bounties window is open.</summary>
    public bool RefreshOnKills = true;
    /// <summary>Fewest seconds between two automatic "/bounty list" reads (kills arrive faster than that).</summary>
    public int MinRefreshSeconds = 10;
    /// <summary>Re-read every this many minutes while the window is open, even without kills (0 = off).</summary>
    public int PeriodicRefreshMinutes = 5;
    /// <summary>Hide rewards this character's QB is too low for.</summary>
    public bool HideQbLocked;
    /// <summary>List the closest-to-complete bounties first instead of the server's order.</summary>
    public bool SortByProgress;
    /// <summary>Area / reward filter text.</summary>
    public string Filter = string.Empty;

    /// <summary>Last "/bounty list" result (server order), kept so the window fills right after login.</summary>
    public List<IltBountyEntry> Entries = new();
    /// <summary>When Entries was last read from the server (UTC; MinValue = never).</summary>
    public DateTime ListedUtc;
    /// <summary>True when the last full read said no bounty is active anywhere.</summary>
    public bool NoneAnywhere;
    /// <summary>Latest bounty chat event ("Bounty complete! +1 ... gained.") and when it arrived.</summary>
    public string LastEvent = string.Empty;
    public DateTime LastEventUtc;
}

/// <summary>One reward of one bounty area, as "/bounty list" last reported it.</summary>
public sealed class IltBountyEntry
{
    /// <summary>Dungeon (optionally "(layer N)"), "All zones", a zone name or a region name.</summary>
    public string Where = string.Empty;
    public long Amount;
    public string Item = string.Empty;
    /// <summary>Kills per award.</summary>
    public int KillsRequired;
    /// <summary>Cooldown after an award before kills count again (0 = none).</summary>
    public int CooldownSeconds;
    /// <summary>IltBountyStatus as an int (Counting, Cooldown, NeedsQb).</summary>
    public int Status;
    /// <summary>Kills counted so far (Counting only).</summary>
    public int Kills;
    /// <summary>End of the cooldown (Cooldown only, UTC).</summary>
    public DateTime UnlockAtUtc;
    public long QbRequired;
    public long QbHave;
    /// <summary>Last time Kills went up or the bounty was awarded (UTC), for the window's highlight.</summary>
    public DateTime ChangedUtc;
}
