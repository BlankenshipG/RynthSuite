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
    public bool WindowVisible;
    /// <summary>Tab order: 0 Character, 1 Pet, 2 Banking, 3 Gear, 4 Games.</summary>
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

    /// <summary>Quest Tracker search box contents.</summary>
    public string QuestFilter = string.Empty;
    public string QbFilter = string.Empty;
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
