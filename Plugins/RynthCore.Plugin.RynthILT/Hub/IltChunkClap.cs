// IltChunkClap.cs — Lugian-hammer chunking and /clap automation (Gear tab).
// Originally SilentKelpie's ILT Hub (RynthAi IltHub/); moved to RynthILT 2026-10-13.
//
// Chunk: uses the Lugian Aetheria Hammer on eligible items, one at a time. The rules follow ub-IT
// (Leaftide AetheriaChunkGuard / IsValidCorruptedWep / CorruptedChunkGuard):
//   Aetheria  — "Aetheria"/"Coalesced Aetheria" gems, not equipped, appraised, not flagged
//               never-chunk (bool 9030), not tinkered (171) or imbued (179), never levelled
//               (ItemTotalXp quad 4 == 0), no kept C / CD / CDR rating (gear ints 372 / 374 / 375;
//               on by default; only a rating at or above the "rating level" keeps, a lower one
//               chunks), and max level (int 319, up to 10 on ILT) under the configured cap.
//               A levelled Coalesced piece gets the cap too; an unlevelled one (319 = 0 after
//               appraisal, normal for Coalesced) is chunkable, as in ub-IT. RynthILT used to keep
//               every unlevelled Coalesced piece forever.
//   Corrupted — "Corrupted" melee/missile weapons, not equipped, not never-chunk, no tinks (171),
//               no imbue (179), loot-gen workmanship (105) > 0, and cleave (292) <= 1, or <= 2 on a
//               two-handed weapon that is not a thrust weapon (RynthILT used to cap every weapon
//               at 1, so no two-handed corrupted weapon was ever chunked). Optional keep rules
//               (Crushing Blow, Biting Strike, Armor Cleaving, slayer, multi-strike, elemental
//               bonus, any spell, max damage, spell ids) are all off by default.
//   Peace mode only unless ChunkInCombat. With ChunkInCombat on, a busy client in combat mode no
//   longer blocks the hammer (a swinging bot is always busy); peace mode still waits for idle.
//   Waits for the result line (10 s max); a target that leaves the pack first counts as chunked
//   (ILT does not always send a known line). Each item's skip reason is logged when it changes
//   and tallied in the status line.
//   The tool is the "Lugian Aetheria Hammer" only. The Lugian Hammer weapons (Flaming, Acid, ...)
//   are loot and chunk nothing: the server answers with no result line, so every item timed out
//   into the skip list and the status sat on "nothing eligible" (SilentKelpie, 2026-10-15).
// Clap: "/clap all" converts aetheria + trinkets into coins server-side: red (Coalesced level 1-3,
//   chunks, powder) with Empyrean Trinkets, blue with Falatacot Trinkets. Auto-clap sends it only
//   when a pair is carried. Needs the AutoCraftingEnabled stamp; 30 min ± 2 min interval, first
//   attempt ~1 min after enable.
using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using RynthCore.Loot;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.Hub;

internal sealed class IltChunkClap : IIltFeature
{
    private const long ChunkResultWaitMs = 10_000;
    private const long ClapFirstDelayMs = 60_000;
    private const long ClapJitterMs = 120_000;

    private static readonly string[] ChunkDoneLines =
    {
        "You smash the aetheria with the hammer.", "The two artifacts link together effortlessly.",
        "You successfully chunk the aetheria.", "You fail to chunk the aetheria.",
        "The corrupted weapon is crushed into a fine powder.", "You smash the corrupted weapon with the hammer.",
        "The corrupted weapon shatters into fragments.", "You fail to chunk the corrupted weapon.",
    };
    private static readonly string[] ChunkRefusedLines =
    {
        "The aetheria is too powerful to chunk.", "You cannot chunk this aetheria.",
        "The corrupted weapon is too powerful to chunk.", "You cannot chunk this corrupted weapon.",
    };

    private readonly IltHubContext _ctx;
    private readonly Random _rng = new();

    // Chunk state
    private long _lastChunkAt;
    private int _waitingOn;
    private long _waitStartedAt;
    private readonly HashSet<int> _skip = new();
    private bool _manualPass;
    private volatile string _chunkStatus = "idle";
    private int _chunked;
    // Last skip reason per item, so each reason is logged once instead of every pass.
    private readonly Dictionary<int, string> _lastSkip = new();
    // Skip reasons of the current pass (reason -> items), shown in the status line.
    private readonly Dictionary<string, int> _passSkips = new();

    // Clap state
    private long _nextClapAt;
    private volatile string _clapStatus = "idle";
    private bool _clapWasEnabled;

    public IltChunkClap(IltHubContext ctx) => _ctx = ctx;

    private IltGearState G => _ctx.State.Gear;

    // ── Chunk ───────────────────────────────────────────────────────────────

    /// <summary>Infinite Leaftide's chunking tool.</summary>
    internal const string HammerName = "Lugian Aetheria Hammer";

    /// <summary>True for the chunking tool only, never a Lugian Hammer weapon. Pure: the tests drive it.</summary>
    internal static bool IsChunkHammer(string name) => name.Equals(HammerName, StringComparison.OrdinalIgnoreCase);

    private WorldObject? FindHammer()
        => _ctx.Inventory.Items().FirstOrDefault(wo => IsChunkHammer(wo.Name) && !_ctx.Inventory.IsEquipped(wo));

    /// <summary>Item kinds the hammer works on (the name and class gate, before any appraisal).</summary>
    internal enum ChunkKind { None, Aetheria, Corrupted }

    /// <summary>
    /// Which chunk rules an item falls under, by class and name only. Aetheria chunks and powder
    /// are clap material, not hammer targets. Pure: the tests drive it.
    /// </summary>
    internal static ChunkKind KindOf(AcObjectClass cls, string name)
    {
        if (cls == AcObjectClass.Gem)
        {
            if (name.Equals("Coalesced Aetheria", StringComparison.OrdinalIgnoreCase)) return ChunkKind.Aetheria;
            if (name.Contains("Aetheria", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Chunk", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Powder", StringComparison.OrdinalIgnoreCase))
                return ChunkKind.Aetheria;
            return ChunkKind.None;
        }
        if (cls is AcObjectClass.MeleeWeapon or AcObjectClass.MissileWeapon
            && name.Contains("Corrupted", StringComparison.OrdinalIgnoreCase))
            return ChunkKind.Corrupted;
        return ChunkKind.None;
    }

    /// <summary>An aetheria piece's chunk facts, read after appraisal.</summary>
    internal readonly record struct AetheriaFacts(
        bool Coalesced, int MaxLevel, bool NeverChunk, long ItemXp, int Tinkers, int Imbue,
        int Crit, int CritDamage, int CritDamageResist, string? Unreadable = null);

    /// <summary>
    /// Why an aetheria piece is kept, or null when it may be chunked (ub-IT AetheriaChunkGuard plus
    /// RynthILT's C / CD / CDR keep switches). Pure: the tests drive it.
    /// <para>
    /// <see cref="AetheriaFacts.Unreadable"/> is set when the item's properties could not be read (the
    /// host lacks a reader or the appraisal never arrived). A property that failed to read looks
    /// exactly like one the item lacks (level 0, no XP, no tinks), so such a piece is kept: an
    /// "unlevelled" Coalesced piece is only chunked when its level was truly read as none.
    /// </para>
    /// </summary>
    internal static string? AetheriaSkip(IltGearState g, in AetheriaFacts f)
    {
        if (f.Unreadable != null) return $"item data unreadable ({f.Unreadable})";
        if (f.NeverChunk) return "never-chunk flag";
        if (f.Tinkers != 0 || f.Imbue != 0) return "tinkered or imbued";
        if (f.ItemXp != 0) return "has item XP";
        if (KeptForRatings(g, f.Crit, f.CritDamage, f.CritDamageResist)) return "kept for its C / CD / CDR rating";
        if (!UnderChunkCap(f.MaxLevel, g.ChunkAetheriaUnderLevel, f.Coalesced))
            return f.MaxLevel > 0 ? "level at or above the cap" : "no level read";
        return null;
    }

    /// <summary>Why an aetheria item is kept (null = chunk it); "appraising" while its data is missing.</summary>
    private string? AetheriaSkip(WorldObject wo)
    {
        var inv = _ctx.Inventory;
        if (inv.IsEquipped(wo)) return "equipped";
        if (!inv.HasAppraisal(wo)) { inv.RequestAppraisal(wo); return Appraising; }
        string? gap = inv.ReadGap(wo, quads: true);
        var f = new AetheriaFacts(
            Coalesced: wo.Name.Equals("Coalesced Aetheria", StringComparison.OrdinalIgnoreCase),
            MaxLevel: inv.Int(wo, IltInventory.IntItemMaxLevel),
            NeverChunk: inv.Bool(wo, IltInventory.BoolNeverChunk),
            ItemXp: inv.Quad(wo, IltInventory.QuadItemTotalXp),
            Tinkers: inv.Int(wo, IltInventory.IntNumTimesTinkered),
            Imbue: inv.Int(wo, IltInventory.IntImbuedEffect),
            Crit: inv.Int(wo, IltInventory.IntGearCrit),
            CritDamage: inv.Int(wo, IltInventory.IntGearCritDamage),
            CritDamageResist: inv.Int(wo, IltInventory.IntGearCritDamageResist),
            Unreadable: gap);
        return AetheriaSkip(G, f);
    }

    /// <summary>The chunk cap in range: 1 (chunk nothing by level) to one past the ILT maximum (every level).</summary>
    internal static int ClampUnderLevel(int under) => Math.Clamp(under, 1, IltInventory.AetheriaMaxLevel + 1);

    /// <summary>
    /// True when an aetheria's max level (int 319) is under the cap. A levelled Coalesced piece
    /// (ACECustom rolls 1-10; level 6+ can carry a rating) gets the same cap. Level 0 after
    /// appraisal: an unlevelled Coalesced piece is chunkable (ub-IT: "ItemMaxLevel 0 after assess
    /// is normal"); any other aetheria with no level is never chunked. Pure: the tests drive it.
    /// </summary>
    internal static bool UnderChunkCap(int maxLevel, int under, bool coalesced = false)
        => maxLevel > 0 ? maxLevel < ClampUnderLevel(under) : coalesced && maxLevel == 0;

    /// <summary>Highest rating level the keep threshold accepts.</summary>
    internal const int MaxRatingLevel = 50;

    /// <summary>The rating keep threshold in range: 1 (any rating keeps) to <see cref="MaxRatingLevel"/>.</summary>
    internal static int ClampRatingLevel(int level) => Math.Clamp(level, 1, MaxRatingLevel);

    /// <summary>
    /// True when an aetheria's C / CD / CDR rating is one the settings keep: its keep switch is on and
    /// the rating is at or above the rating level (<see cref="IltGearState.KeepAetheriaRatingAtLeast"/>).
    /// A rating below that level does not protect the piece, so e.g. a C 1 Coalesced Aetheria chunks
    /// with the level at 2 while a C 2 one is kept. Pure: the tests drive it.
    /// </summary>
    internal static bool KeptForRatings(IltGearState g, int crit, int critDamage, int critDamageResist)
    {
        int min = ClampRatingLevel(g.KeepAetheriaRatingAtLeast);
        return (g.KeepAetheriaWithCrit && crit >= min)
               || (g.KeepAetheriaWithCritDamage && critDamage >= min)
               || (g.KeepAetheriaWithCritDamageResist && critDamageResist >= min);
    }

    private const string Appraising = "appraising";
    private const int SkillTwoHandedCombat = 41;
    private const int WeaponTypeTwoHanded = 11;
    private const int AttackTypeThrust = 2;

    /// <summary>A corrupted weapon's chunk facts, read after appraisal.</summary>
    internal sealed record CorruptedFacts
    {
        public bool NeverChunk { get; init; }
        public int Tinkers { get; init; }
        public int Imbue { get; init; }
        public int Workmanship { get; init; }
        public int AttackType { get; init; }
        public int Cleaving { get; init; }
        public int WieldSkill { get; init; }
        public int WeaponType { get; init; }
        public double CritFrequency { get; init; }
        public double CritMultiplier { get; init; }
        public double IgnoreArmor { get; init; }
        public int Slayer { get; init; }
        public int ElementalBonus { get; init; }
        public int MaxDamage { get; init; }
        public IReadOnlyList<int> Spells { get; init; } = Array.Empty<int>();
        /// <summary>What could not be read (null = everything the rules need was); see AetheriaFacts.Unreadable.</summary>
        public string? Unreadable { get; init; }
    }

    /// <summary>
    /// The most cleave a chunkable corrupted weapon may have (ub-IT IsValidCorruptedWep): 2 on a
    /// two-handed weapon that is not a thrust weapon, 1 on everything else. Pure: the tests drive it.
    /// </summary>
    internal static int MaxChunkCleave(int wieldSkill, int weaponType, int attackType)
        => (wieldSkill == SkillTwoHandedCombat || weaponType == WeaponTypeTwoHanded) && attackType != AttackTypeThrust ? 2 : 1;

    /// <summary>
    /// Why a corrupted weapon is kept, or null when it may be chunked: ub-IT's hard rules, then its
    /// IdleActs keep rules from the settings. Pure: the tests drive it.
    /// </summary>
    internal static string? CorruptedSkip(IltGearState g, CorruptedFacts f)
    {
        // A property that failed to read looks like one the weapon lacks (0), which would chunk it.
        if (f.Unreadable != null) return $"item data unreadable ({f.Unreadable})";

        // Hard rules (always on).
        if (f.NeverChunk) return "never-chunk flag";
        if (f.Tinkers != 0 || f.Imbue != 0) return "tinkered or imbued";
        if (f.Workmanship <= 0) return "no workmanship";
        if (f.Cleaving > MaxChunkCleave(f.WieldSkill, f.WeaponType, f.AttackType)) return "cleave over the limit";

        // Keep rules (each off by default).
        if (g.KeepCorruptedCrushingBlow && f.CritFrequency > 0) return "kept: Crushing Blow";
        if (g.KeepCorruptedBitingStrike && f.CritMultiplier > 0) return "kept: Biting Strike";
        if (g.KeepCorruptedArmorCleaving && f.IgnoreArmor > 0) return "kept: Armor Cleaving";
        if (g.KeepCorruptedAnySlayer && f.Slayer > 0) return "kept: slayer";
        if (g.KeepCorruptedMultiStrike && f.AttackType is 160 or 166 or 486) return "kept: multi-strike";
        if (g.KeepCorruptedElementalBonus && f.ElementalBonus > 0) return "kept: elemental bonus";
        if (g.KeepCorruptedMaxDamageAtLeast > 0 && f.MaxDamage >= g.KeepCorruptedMaxDamageAtLeast) return "kept: max damage";
        if (f.Spells.Count > 0)
        {
            if (g.KeepCorruptedAnySpell) return "kept: has a spell";
            var deny = ParseSpellIds(g.KeepCorruptedSpellIds);
            if (deny.Count > 0 && f.Spells.Any(deny.Contains)) return "kept: listed spell";
        }
        return null;
    }

    /// <summary>Positive spell ids from a comma / semicolon / whitespace separated list. Pure: the tests drive it.</summary>
    internal static HashSet<int> ParseSpellIds(string? csv)
    {
        var ids = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(csv)) return ids;
        foreach (var part in csv.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int id) && id > 0)
                ids.Add(id);
        return ids;
    }

    /// <summary>Why a corrupted weapon is kept (null = chunk it); "appraising" while its data is missing.</summary>
    private string? CorruptedSkip(WorldObject wo)
    {
        var inv = _ctx.Inventory;
        if (inv.IsEquipped(wo)) return "equipped";
        if (!inv.HasAppraisal(wo)) { inv.RequestAppraisal(wo); return Appraising; }
        bool needDoubles = G.KeepCorruptedCrushingBlow || G.KeepCorruptedBitingStrike || G.KeepCorruptedArmorCleaving;
        bool needSpells = G.KeepCorruptedAnySpell || !string.IsNullOrWhiteSpace(G.KeepCorruptedSpellIds);
        var f = new CorruptedFacts
        {
            Unreadable = inv.ReadGap(wo, doubles: needDoubles, spells: needSpells),
            NeverChunk = inv.Bool(wo, IltInventory.BoolNeverChunk),
            Tinkers = inv.Int(wo, IltInventory.IntNumTimesTinkered),
            Imbue = inv.Int(wo, IltInventory.IntImbuedEffect),
            Workmanship = inv.Int(wo, IltInventory.IntItemWorkmanship),
            AttackType = inv.Int(wo, IltInventory.IntAttackType),
            Cleaving = inv.Int(wo, IltInventory.IntCleaving),
            WieldSkill = inv.Int(wo, IltInventory.IntWieldSkillType),
            WeaponType = inv.Int(wo, IltInventory.IntWeaponType),
            CritFrequency = inv.Double(wo, IltInventory.FloatCriticalFrequency),
            CritMultiplier = inv.Double(wo, IltInventory.FloatCriticalMultiplier),
            IgnoreArmor = inv.Double(wo, IltInventory.FloatIgnoreArmor),
            Slayer = inv.Int(wo, IltInventory.IntSlayerCreatureType),
            ElementalBonus = inv.Int(wo, IltInventory.IntElementalDamageBonus),
            MaxDamage = inv.Int(wo, IltInventory.IntDamage),
            // Spells are only read when a spell rule is on (the read copies the item's spell list).
            Spells = needSpells ? inv.SpellIds(wo) : Array.Empty<int>(),
        };
        return CorruptedSkip(G, f);
    }

    /// <summary>
    /// Chunk is allowed while the client is busy only in combat mode with ChunkInCombat on (ub-IT
    /// AutoChunkCombatBusyPolicy v2.0.1059: a swinging bot is always busy). Peace mode still waits
    /// for idle so the hammer never interrupts another use. Pure: the tests drive it.
    /// </summary>
    internal static bool AllowsChunkWhileBusy(bool isBusy, bool isPeace, bool chunkInCombat)
        => !isBusy || (!isPeace && chunkInCombat);

    private WorldObject? NextChunkTarget()
    {
        _passSkips.Clear();
        foreach (var wo in _ctx.Inventory.Items())
        {
            if (_skip.Contains(wo.Id)) continue;
            string? reason;
            switch (KindOf(wo.ObjectClass, wo.Name))
            {
                case ChunkKind.Aetheria when G.AutoChunkAetheria || _manualPass:
                    reason = AetheriaSkip(wo);
                    break;
                case ChunkKind.Corrupted when G.AutoChunkCorrupted || _manualPass:
                    reason = CorruptedSkip(wo);
                    break;
                default:
                    continue;
            }
            if (reason == null) { _lastSkip.Remove(wo.Id); return wo; }
            NoteSkip(wo, reason);
        }
        return null;
    }

    /// <summary>Tallies a skip for the status line and logs it once per item and reason.</summary>
    private void NoteSkip(WorldObject wo, string reason)
    {
        _passSkips[reason] = _passSkips.TryGetValue(reason, out int n) ? n + 1 : 1;
        if (reason == Appraising) return;
        if (_lastSkip.TryGetValue(wo.Id, out string? last) && last == reason) return;
        _lastSkip[wo.Id] = reason;
        RynthLog.Write(LogCat.IltGear, $"chunk: keep 0x{(uint)wo.Id:X8} '{wo.Name}': {reason}");
    }

    /// <summary>"3 kept for its C / CD / CDR rating, 1 appraising" — the current pass's skips.</summary>
    private string PassSkipSummary()
        => string.Join(", ", _passSkips.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"));

    /// <summary>Runs one chunk pass now (both item kinds) regardless of the auto toggles.</summary>
    public void ChunkNow()
    {
        RynthLog.Trace(LogCat.IltGear, $"ChunkNow()");
        _manualPass = true;
        _lastChunkAt = 0;
        _skip.Clear();   // a manual pass gives the items that got no answer another try
        _chunkStatus = "manual pass started";
    }

    private void TickChunk(long now)
    {
        if (!G.AutoChunkAetheria && !G.AutoChunkCorrupted && !_manualPass) return;

        if (_waitingOn != 0)
        {
            int id = _waitingOn;
            switch (WaitOutcome(now - _waitStartedAt, _ctx.Inventory.Items().Any(wo => wo.Id == id)))
            {
                case ChunkWait.Waiting:
                    return;
                case ChunkWait.Gone:
                    _chunked++;
                    RynthLog.Write(LogCat.IltGear, $"chunk: 0x{(uint)id:X8} left the pack with no result line; counted as chunked");
                    break;
                case ChunkWait.TimedOut:
                    _skip.Add(id); // still carried and no result line: don't hammer the same item forever
                    RynthLog.Write(LogCat.IltGear, $"chunk: no result line for 0x{(uint)id:X8} in {ChunkResultWaitMs / 1000} s, skipped until a manual pass");
                    break;
            }
            _waitingOn = 0;
        }
        if (now - _lastChunkAt < Math.Max(500, G.ChunkIntervalMs)) return;
        _lastChunkAt = now;

        bool peace = _ctx.Inventory.InPeaceMode;
        if (!G.ChunkInCombat && !peace) { _chunkStatus = "waiting for peace mode"; return; }
        if (!AllowsChunkWhileBusy(_ctx.Inventory.IsBusy, peace, G.ChunkInCombat)) { _chunkStatus = "waiting for idle"; return; }

        var hammer = FindHammer();
        if (hammer == null) { _chunkStatus = $"no {HammerName} carried"; _manualPass = false; return; }
        var target = NextChunkTarget();
        if (target == null)
        {
            string kept = PassSkipSummary();
            _chunkStatus = (_chunked > 0 ? $"nothing left to chunk ({_chunked} this session)" : "nothing eligible")
                         + (kept.Length > 0 ? $"; not chunked: {kept}" : "")
                         + (_skip.Count > 0 ? $"; {_skip.Count} skipped with no answer (Chunk now retries them)" : "");
            _manualPass = false;
            return;
        }
        if (_ctx.Inventory.UseOn(hammer, unchecked((uint)target.Id)))
        {
            _waitingOn = target.Id;
            _waitStartedAt = now;
            _chunkStatus = "chunking " + target.Name;
        }
    }

    // ── Clap ────────────────────────────────────────────────────────────────

    internal enum ClapPart { None, Red, Blue, Empyrean, Falatacot }

    /// <summary>
    /// Which side of /clap an item feeds (ACECustom PlayerCommands.HandleClap): red material (Red
    /// Coalesced Aetheria, chunks, powder) pairs with Ancient Empyrean Trinkets, blue with Ancient
    /// Falatacot Trinkets. Coalesced counts only unrevealed and at level 1-3; level 0 (not read yet)
    /// counts, and the server has the last word. With no WCID yet, the chunk and trinket names
    /// decide. Pure: the tests drive it.
    /// </summary>
    internal static ClapPart ClapPartOf(uint wcid, string name, int maxLevel)
    {
        bool lowCoalesced = name.Equals("Coalesced Aetheria", StringComparison.OrdinalIgnoreCase) && maxLevel <= 3;
        switch (wcid)
        {
            case IltInventory.WcidRedCoalescedAetheria:  return lowCoalesced ? ClapPart.Red : ClapPart.None;
            case IltInventory.WcidBlueCoalescedAetheria: return lowCoalesced ? ClapPart.Blue : ClapPart.None;
            case IltInventory.WcidRedAetheriaChunk:
            case IltInventory.WcidRedAetheriaPowder:     return ClapPart.Red;
            case IltInventory.WcidBlueAetheriaChunk:
            case IltInventory.WcidBlueAetheriaPowder:    return ClapPart.Blue;
            case IltInventory.WcidEmpyreanTrinket:       return ClapPart.Empyrean;
            case IltInventory.WcidFalatacotTrinket:      return ClapPart.Falatacot;
            case 0: break;
            default: return ClapPart.None;
        }
        if (name.Equals("Red Aetheria Chunk", StringComparison.OrdinalIgnoreCase)) return ClapPart.Red;
        if (name.Equals("Blue Aetheria Chunk", StringComparison.OrdinalIgnoreCase)) return ClapPart.Blue;
        if (name.Equals("Ancient Empyrean Trinket", StringComparison.OrdinalIgnoreCase)) return ClapPart.Empyrean;
        if (name.Equals("Ancient Falatacot Trinket", StringComparison.OrdinalIgnoreCase)) return ClapPart.Falatacot;
        return ClapPart.None;
    }

    /// <summary>
    /// True when /clap has a pair to work with: red material and an Empyrean Trinket, or blue
    /// material and a Falatacot Trinket. One side alone gets "You don't have any aetheria materials".
    /// Pure: the tests drive it.
    /// </summary>
    internal static bool CanClap(IEnumerable<ClapPart> parts)
    {
        bool red = false, blue = false, empyrean = false, falatacot = false;
        foreach (var part in parts)
        {
            switch (part)
            {
                case ClapPart.Red: red = true; break;
                case ClapPart.Blue: blue = true; break;
                case ClapPart.Empyrean: empyrean = true; break;
                case ClapPart.Falatacot: falatacot = true; break;
            }
        }
        return (red && empyrean) || (blue && falatacot);
    }

    private bool HasClapMaterials()
    {
        var inv = _ctx.Inventory;
        return CanClap(inv.Items().Where(wo => !inv.IsEquipped(wo))
                                  .Select(wo => ClapPartOf(inv.Wcid(wo), wo.Name, inv.Int(wo, IltInventory.IntItemMaxLevel))));
    }

    /// <summary>Sends "/clap all" through the capture queue (reply stays visible).</summary>
    public void ClapNow(bool manual)
    {
        RynthLog.Trace(LogCat.IltGear, $"ClapNow(manual={manual})");
        if (_ctx.Options.IsOff(IltFeature.Clap)) { _clapStatus = "/clap is not available (server or AutoCraftingEnabled stamp)"; return; }
        if (_ctx.Capture.IsPending("/clap all")) return;
        if (!manual && !HasClapMaterials()) { _clapStatus = "no aetheria and trinket pair carried (red + Empyrean, blue + Falatacot)"; return; }
        _clapStatus = "sending /clap all...";
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = "/clap all",
            IsResponseLine = t => t.StartsWith("Deposited ", StringComparison.Ordinal)
                               || t.StartsWith("[Clap]", StringComparison.OrdinalIgnoreCase)
                               || t.Contains("aetheria materials", StringComparison.OrdinalIgnoreCase)
                               || t.Contains("AutoCraftingEnabled", StringComparison.OrdinalIgnoreCase),
            IdleEndMs = 800,
            FirstLineTimeoutMs = 10_000,
            Eat = false,
            OnComplete = r =>
            {
                if (r.UnknownCommand) { _ctx.Options.Set(IltFeature.Clap, IltTri.Off); _clapStatus = "/clap not available"; return; }
                _clapStatus = r.Lines.Count > 0 ? r.Lines[^1] : "no reply";
                if (r.Lines.Any(l => l.StartsWith("Deposited ", StringComparison.Ordinal))) _ctx.Options.Set(IltFeature.Clap, IltTri.On);
            },
        });
    }

    private void TickClap(long now)
    {
        // Automation only runs once the server has positively confirmed /clap (unknown = wait).
        if (!G.AutoClap || !_ctx.Options.IsOn(IltFeature.Clap)) { _clapWasEnabled = false; return; }
        if (!_clapWasEnabled)
        {
            _clapWasEnabled = true;
            _nextClapAt = now + ClapFirstDelayMs;
            _clapStatus = "first auto-clap in ~1 min";
            return;
        }
        if (now < _nextClapAt) return;
        long interval = Math.Max(5, G.ClapIntervalMinutes) * 60_000L;
        _nextClapAt = now + interval + (long)((_rng.NextDouble() * 2 - 1) * ClapJitterMs);
        ClapNow(manual: false);
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        TickChunk(nowMs);
        TickClap(nowMs);
    }

    internal enum ChunkResult { None, Busy, Done, Refused }

    /// <summary>Where a chunk in flight stands when no chat line has ended the wait.</summary>
    internal enum ChunkWait { Waiting, Gone, TimedOut }

    /// <summary>
    /// The wait for a chunk with no result line yet. The hammer destroys the target, so a target
    /// that has left the pack was chunked: ILT does not always send a line the classifier knows
    /// (Silentkelpie 2026-10-18: a Red Aetheria Chunk arrived and UseDone had no error, yet the
    /// item was still filed as skipped after 10 s). Still carried after the wait = timed out.
    /// Pure: the tests drive it.
    /// </summary>
    internal static ChunkWait WaitOutcome(long elapsedMs, bool targetStillCarried)
        => !targetStillCarried ? ChunkWait.Gone
         : elapsedMs >= ChunkResultWaitMs ? ChunkWait.TimedOut
         : ChunkWait.Waiting;

    /// <summary>
    /// What a chat line says about the chunk in flight. ILT names the item and the tool ("You
    /// smash the Corrupted Sword with the Lugian Aetheria Hammer."), so a line naming a corrupted
    /// item with the hammer, powder or shatter is a result too (combat's "You smash Foo for 147"
    /// names neither). Pure: the tests drive it.
    /// </summary>
    internal static ChunkResult ClassifyChat(string text)
    {
        if (text.StartsWith("You're too busy", StringComparison.OrdinalIgnoreCase) || text.StartsWith("You are too busy", StringComparison.OrdinalIgnoreCase))
            return ChunkResult.Busy;
        if (ChunkRefusedLines.Any(l => text.Contains(l, StringComparison.OrdinalIgnoreCase))) return ChunkResult.Refused;
        if (ChunkDoneLines.Any(l => text.Contains(l, StringComparison.OrdinalIgnoreCase))) return ChunkResult.Done;
        if (text.Contains("corrupted", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("hammer", StringComparison.OrdinalIgnoreCase) || text.Contains("powder", StringComparison.OrdinalIgnoreCase)
                || text.Contains("shatter", StringComparison.OrdinalIgnoreCase)))
            return ChunkResult.Done;
        return ChunkResult.None;
    }

    public bool OnChat(string text)
    {
        if (_waitingOn == 0) return false;
        switch (ClassifyChat(text))
        {
            case ChunkResult.Busy:
                _waitingOn = 0; // retry the same item next interval
                break;
            case ChunkResult.Done:
                _chunked++;
                _waitingOn = 0;
                break;
            case ChunkResult.Refused:
                _skip.Add(_waitingOn);
                _waitingOn = 0;
                break;
        }
        return false;
    }

    public void OnLogout()
    {
        _waitingOn = 0;
        _skip.Clear();
        _lastSkip.Clear();
        _manualPass = false;
        _clapWasEnabled = false;
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        SafeImGui.TextColored(IltStyle.ColTeal, $"Chunking ({HammerName})");
        bool a = G.AutoChunkAetheria, c = G.AutoChunkCorrupted, comb = G.ChunkInCombat;
        if (ImGui.Checkbox("Auto-chunk low aetheria", ref a)) G.AutoChunkAetheria = a;
        ImGui.SameLine();
        if (ImGui.Checkbox("Auto-chunk corrupted weapons", ref c)) G.AutoChunkCorrupted = c;
        if (ImGui.Checkbox("Allow while in combat mode", ref comb)) G.ChunkInCombat = comb;
        int under = G.ChunkAetheriaUnderLevel;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Chunk aetheria with max level below", ref under)) G.ChunkAetheriaUnderLevel = ClampUnderLevel(under);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip($"Infinite Leaftide aetheria goes up to level {IltInventory.AetheriaMaxLevel}; {IltInventory.AetheriaMaxLevel + 1} chunks every level.\nA levelled Coalesced Aetheria gets the cap too; an unlevelled one (level 0 after appraisal) is chunked, as in ub-IT.");
        bool kc = G.KeepAetheriaWithCrit, kcd = G.KeepAetheriaWithCritDamage, kcdr = G.KeepAetheriaWithCritDamageResist;
        ImGui.TextUnformatted("Never chunk aetheria with:");
        ImGui.SameLine();
        if (ImGui.Checkbox("C##keepc", ref kc)) G.KeepAetheriaWithCrit = kc;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A Crit rating (GearCrit).");
        ImGui.SameLine();
        if (ImGui.Checkbox("CD##keepcd", ref kcd)) G.KeepAetheriaWithCritDamage = kcd;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A Crit Damage rating (GearCritDamage).");
        ImGui.SameLine();
        if (ImGui.Checkbox("CDR##keepcdr", ref kcdr)) G.KeepAetheriaWithCritDamageResist = kcdr;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A Crit Damage Resist rating (GearCritDamageResist).");
        int ratingAt = G.KeepAetheriaRatingAtLeast;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Keep when the rating is at least##keeprating", ref ratingAt)) G.KeepAetheriaRatingAtLeast = ClampRatingLevel(ratingAt);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rating level for the C / CD / CDR switches above (Coalesced Aetheria and revealed aetheria).\n"
                           + "At or above this level: kept. Below it: the rating does not protect the piece, so it chunks\n"
                           + "if the level cap and the other rules allow. 1 = any rating keeps.");
        if (ImGui.TreeNode("Corrupted weapon keep rules##corrkeep"))
        {
            // ub-IT IdleActs > Corr Chunk: each matching rule keeps the weapon.
            bool cb = G.KeepCorruptedCrushingBlow, bs = G.KeepCorruptedBitingStrike, acl = G.KeepCorruptedArmorCleaving;
            bool sl = G.KeepCorruptedAnySlayer, ms = G.KeepCorruptedMultiStrike, el = G.KeepCorruptedElementalBonus, sp = G.KeepCorruptedAnySpell;
            if (ImGui.Checkbox("Crushing Blow##ck", ref cb)) G.KeepCorruptedCrushingBlow = cb;
            ImGui.SameLine();
            if (ImGui.Checkbox("Biting Strike##ck", ref bs)) G.KeepCorruptedBitingStrike = bs;
            ImGui.SameLine();
            if (ImGui.Checkbox("Armor Cleaving##ck", ref acl)) G.KeepCorruptedArmorCleaving = acl;
            if (ImGui.Checkbox("Any slayer##ck", ref sl)) G.KeepCorruptedAnySlayer = sl;
            ImGui.SameLine();
            if (ImGui.Checkbox("Multi-strike##ck", ref ms)) G.KeepCorruptedMultiStrike = ms;
            ImGui.SameLine();
            if (ImGui.Checkbox("Elemental bonus##ck", ref el)) G.KeepCorruptedElementalBonus = el;
            if (ImGui.Checkbox("Any spell##ck", ref sp)) G.KeepCorruptedAnySpell = sp;
            int maxDmg = G.KeepCorruptedMaxDamageAtLeast;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Keep max damage at least (0 = off)##ck", ref maxDmg)) G.KeepCorruptedMaxDamageAtLeast = Math.Max(0, maxDmg);
            string ids = G.KeepCorruptedSpellIds ?? "";
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputText("Keep spell ids##ck", ref ids, 512)) G.KeepCorruptedSpellIds = ids;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Comma or space separated spell ids: a corrupted weapon carrying any of them is kept.");
            SafeImGui.TextDisabled("Always kept: tinkered, imbued, no workmanship, cleave over 1 (2 on two-handed, non-thrust).");
            ImGui.TreePop();
        }
        if (ImGui.Button("Chunk eligible items now...")) _ctx.Confirm("Chunk items",
            "Run one chunk pass now on every eligible aetheria and corrupted weapon?\nChunked items are destroyed.",
            ChunkNow, "Chunk");
        SafeImGui.TextDisabled("Status: " + _chunkStatus);

        ImGui.Separator();
        ImGui.TextColored(IltStyle.ColTeal, "Clap (/clap all)");
        if (_ctx.Options.IsOff(IltFeature.Clap))
        {
            ImGui.TextColored(IltStyle.ColTextMute, "Not available (server off or AutoCraftingEnabled stamp missing).");
            return;
        }
        bool ac = G.AutoClap;
        if (ImGui.Checkbox("Auto-clap periodically", ref ac)) G.AutoClap = ac;
        int mins = G.ClapIntervalMinutes;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Interval (min)", ref mins)) G.ClapIntervalMinutes = Math.Max(5, mins);
        if (ImGui.SmallButton("Clap now")) _ctx.Post(() => ClapNow(manual: true));
        ImGui.SameLine();
        SafeImGui.TextDisabled(_clapStatus);
    }
}
