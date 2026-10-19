using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// The Lugian-hammer aetheria rules (Hub/IltChunkClap.cs): Infinite Leaftide aetheria goes up to
// level 10, and aetheria carrying a C, CD or CDR rating is kept unless its keep switch is off.
// The cap covers levelled Coalesced Aetheria; an unlevelled one chunks, as in ub-IT. Corrupted
// weapons follow ub-IT's hard rules and its optional keep rules, and /clap needs a colour with its
// own trinket.
internal static class AetheriaChunkTests
{
    public static void Register(Runner r)
    {
        r.Add("aetheria: the chunk cap runs to one past ILT's level 10", Cap);
        r.Add("aetheria: C / CD / CDR ratings are kept by default, each with its own switch", Ratings);
        r.Add("aetheria: rating level - at or above keeps, below chunks (Coalesced too)", RatingLevel);
        r.Add("aetheria: the level cap applies to levelled Coalesced Aetheria; unlevelled Coalesced chunks (ub-IT)", CoalescedCap);
        r.Add("aetheria: a failed read is not 'unlevelled': unreadable pieces are kept, levelled Coalesced keeps its cap", UnreadableAetheria);
        r.Add("aetheria: never-chunk, tinker / imbue, item XP, ratings and the cap each keep a piece", AetheriaRules);
        r.Add("chunk: kind by class and name; chunks and powder are clap material", Kinds);
        r.Add("corrupted: ub-IT hard rules (workmanship, cleave 1, or 2 on two-handed non-thrust)", CorruptedHardRules);
        r.Add("corrupted: ub-IT keep rules, all off by default", CorruptedKeepRules);
        r.Add("corrupted: a failed read is kept; two-handed cleave 2 chunks and other cleave 2 does not", UnreadableCorrupted);
        r.Add("chunk: busy blocks peace mode, not combat with chunk-in-combat on (ub-IT v2.0.1059)", BusyGate);
        r.Add("chunk: a target that leaves the pack counts as chunked; still carried after 10 s times out", WaitOutcome);
        r.Add("clap: the server's WCIDs, each colour paired with its trinket, Coalesced only unrevealed level 1-3", ClapPairs);
    }

    /// <summary>
    /// Coalesced Aetheria has its level from loot (ACECustom 1-10, ratings at 6+); it used to skip
    /// the cap, so a cap of 9 still chunked a level 10 Coalesced piece.
    /// </summary>
    private static void CoalescedCap()
    {
        Check.True(IltChunkClap.UnderChunkCap(8, 9), "level 8 under a cap of 9");
        Check.False(IltChunkClap.UnderChunkCap(9, 9), "level 9 is not below 9");
        Check.False(IltChunkClap.UnderChunkCap(10, 9), "a level 10 Coalesced piece is kept");
        Check.True(IltChunkClap.UnderChunkCap(10, 11), "11 chunks every level");
        Check.False(IltChunkClap.UnderChunkCap(0, 11), "revealed aetheria with no level read: never chunked");
        Check.False(IltChunkClap.UnderChunkCap(9, 9, coalesced: true), "a levelled Coalesced piece keeps the cap");
        Check.True(IltChunkClap.UnderChunkCap(0, 4, coalesced: true), "an unlevelled Coalesced piece chunks (it used to be kept forever)");
        Check.True(IltChunkClap.UnderChunkCap(0, 1, coalesced: true), "even with the lowest cap");
    }

    private static readonly IltChunkClap.AetheriaFacts PlainCoalesced = new(
        Coalesced: true, MaxLevel: 0, NeverChunk: false, ItemXp: 0, Tinkers: 0, Imbue: 0, Crit: 0, CritDamage: 0, CritDamageResist: 0);

    private static void AetheriaRules()
    {
        var g = new IltGearState();
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced), null, "a plain unlevelled Coalesced piece chunks");
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced with { MaxLevel = 3 }), null, "level 3 under the default cap of 4");
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced with { MaxLevel = 4 }), "level at or above the cap", "level 4 at the default cap");
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced with { NeverChunk = true }), "never-chunk flag", "bool 9030");
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced with { Tinkers = 1 }), "tinkered or imbued", "tinkered");
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced with { Imbue = 4 }), "tinkered or imbued", "imbued");
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced with { ItemXp = 100 }), "has item XP", "levelled by use");
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced with { Crit = 2 }), "kept for its C / CD / CDR rating", "a C 2 piece is kept by default");
        g.KeepAetheriaWithCrit = false;
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced with { Crit = 2 }), null, "C switch off: a C 2 piece chunks");
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced with { Coalesced = false }), "no level read", "revealed aetheria needs a level");
    }

    /// <summary>
    /// The rules read 0 for "the item has none" and for "the host could not read it". An unlevelled
    /// Coalesced piece is chunked (ub-IT), but only when its level was truly read as none; a piece whose
    /// properties could not be read is kept, whatever the numbers say.
    /// </summary>
    private static void UnreadableAetheria()
    {
        var g = new IltGearState();
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced), null, "read as unlevelled: chunks");
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced with { Unreadable = "no appraisal" }),
            "item data unreadable (no appraisal)", "the same piece with failed reads: kept");
        Check.Eq(IltChunkClap.AetheriaSkip(g, PlainCoalesced with { MaxLevel = 2, Unreadable = "no item XP" }),
            "item data unreadable (no item XP)", "a levelled piece with failed reads: kept");
        Check.Eq(IltChunkClap.AetheriaSkip(new IltGearState { ChunkAetheriaUnderLevel = 1 }, PlainCoalesced), null, "unlevelled chunks at the lowest cap too");

        // Levelled Coalesced pieces keep the cap.
        var cap9 = new IltGearState { ChunkAetheriaUnderLevel = 9 };
        Check.Eq(IltChunkClap.AetheriaSkip(cap9, PlainCoalesced with { MaxLevel = 8 }), null, "levelled 8 under a cap of 9");
        Check.Eq(IltChunkClap.AetheriaSkip(cap9, PlainCoalesced with { MaxLevel = 9 }), "level at or above the cap", "levelled 9 at a cap of 9");
        Check.Eq(IltChunkClap.AetheriaSkip(cap9, PlainCoalesced with { MaxLevel = 10 }), "level at or above the cap", "levelled 10 over a cap of 9");
        Check.Eq(IltChunkClap.AetheriaSkip(new IltGearState { ChunkAetheriaUnderLevel = 11 }, PlainCoalesced with { MaxLevel = 10 }), null, "cap 11 chunks every level");
    }

    private static void UnreadableCorrupted()
    {
        var g = new IltGearState();
        Check.Eq(IltChunkClap.CorruptedSkip(g, PlainSword with { Unreadable = "no appraisal" }),
            "item data unreadable (no appraisal)", "failed reads: kept (they would read as a plain weapon)");
        var keepCb = new IltGearState { KeepCorruptedCrushingBlow = true };
        Check.Eq(IltChunkClap.CorruptedSkip(keepCb, PlainSword with { Unreadable = "float properties" }),
            "item data unreadable (float properties)", "a keep rule whose float cannot be read keeps the weapon");
        var twoHanded = PlainSword with { WieldSkill = 41, Cleaving = 2 };
        Check.Eq(IltChunkClap.CorruptedSkip(g, twoHanded), null, "two-handed cleave 2 chunks");
        Check.Eq(IltChunkClap.CorruptedSkip(g, PlainSword with { Cleaving = 2 }), "cleave over the limit", "other cleave 2 is kept");
        Check.Eq(IltChunkClap.CorruptedSkip(g, twoHanded with { WieldSkill = 0, WeaponType = 11 }), null, "WeaponType two-handed counts");
        Check.Eq(IltChunkClap.CorruptedSkip(g, twoHanded with { AttackType = 2 }), "cleave over the limit", "two-handed thrust keeps the 1 limit");
    }

    private static void Kinds()
    {
        var gem = RynthCore.Loot.AcObjectClass.Gem;
        Check.Eq(IltChunkClap.KindOf(gem, "Coalesced Aetheria"), IltChunkClap.ChunkKind.Aetheria, "Coalesced");
        Check.Eq(IltChunkClap.KindOf(gem, "Blue Aetheria"), IltChunkClap.ChunkKind.Aetheria, "revealed sigil");
        Check.Eq(IltChunkClap.KindOf(gem, "Red Aetheria Chunk"), IltChunkClap.ChunkKind.None, "chunk");
        Check.Eq(IltChunkClap.KindOf(gem, "Aetheria Powder"), IltChunkClap.ChunkKind.None, "powder");
        Check.Eq(IltChunkClap.KindOf(RynthCore.Loot.AcObjectClass.MeleeWeapon, "Corrupted Greatsword"), IltChunkClap.ChunkKind.Corrupted, "melee");
        Check.Eq(IltChunkClap.KindOf(RynthCore.Loot.AcObjectClass.MissileWeapon, "Corrupted Bow"), IltChunkClap.ChunkKind.Corrupted, "missile");
        Check.Eq(IltChunkClap.KindOf(RynthCore.Loot.AcObjectClass.WandStaffOrb, "Corrupted Orb"), IltChunkClap.ChunkKind.None, "casters are not chunked (ub-IT)");
        Check.Eq(IltChunkClap.KindOf(RynthCore.Loot.AcObjectClass.MeleeWeapon, "Greatsword"), IltChunkClap.ChunkKind.None, "not corrupted");
    }

    private static readonly IltChunkClap.CorruptedFacts PlainSword = new() { Workmanship = 6, AttackType = 4, WieldSkill = 45 };

    private static void CorruptedHardRules()
    {
        var g = new IltGearState();
        Check.Eq(IltChunkClap.CorruptedSkip(g, PlainSword), null, "a plain corrupted sword chunks");
        Check.Eq(IltChunkClap.CorruptedSkip(g, PlainSword with { Workmanship = 0 }), "no workmanship", "workmanship 0");
        Check.Eq(IltChunkClap.CorruptedSkip(g, PlainSword with { Tinkers = 1 }), "tinkered or imbued", "tinkered");
        Check.Eq(IltChunkClap.CorruptedSkip(g, PlainSword with { Imbue = 1 }), "tinkered or imbued", "imbued");
        Check.Eq(IltChunkClap.CorruptedSkip(g, PlainSword with { NeverChunk = true }), "never-chunk flag", "bool 9030");
        Check.Eq(IltChunkClap.CorruptedSkip(g, PlainSword with { Cleaving = 1 }), null, "one-handed cleave 1");
        Check.Eq(IltChunkClap.CorruptedSkip(g, PlainSword with { Cleaving = 2 }), "cleave over the limit", "one-handed cleave 2");

        var twoHanded = PlainSword with { WieldSkill = 41, Cleaving = 2 };
        Check.Eq(IltChunkClap.CorruptedSkip(g, twoHanded), null, "two-handed cleave 2 chunks (it never did before)");
        Check.Eq(IltChunkClap.CorruptedSkip(g, twoHanded with { Cleaving = 3 }), "cleave over the limit", "two-handed cleave 3");
        Check.Eq(IltChunkClap.CorruptedSkip(g, twoHanded with { AttackType = 2 }), "cleave over the limit", "a two-handed thrust weapon keeps the 1 limit");
        Check.Eq(IltChunkClap.MaxChunkCleave(0, 11, 4), 2, "WeaponType TwoHanded counts too");
        Check.Eq(IltChunkClap.MaxChunkCleave(45, 0, 4), 1, "one-handed");
    }

    private static void CorruptedKeepRules()
    {
        var g = new IltGearState();
        var loaded = PlainSword with
        {
            CritFrequency = 0.1, CritMultiplier = 1.5, IgnoreArmor = 0.2, Slayer = 5, AttackType = 160,
            ElementalBonus = 10, MaxDamage = 400, Spells = new[] { 2072 },
        };
        Check.Eq(IltChunkClap.CorruptedSkip(g, loaded), null, "every keep rule is off by default");

        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedCrushingBlow = true }, loaded), "kept: Crushing Blow", "Crushing Blow");
        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedBitingStrike = true }, loaded), "kept: Biting Strike", "Biting Strike");
        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedArmorCleaving = true }, loaded), "kept: Armor Cleaving", "Armor Cleaving");
        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedAnySlayer = true }, loaded), "kept: slayer", "slayer");
        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedMultiStrike = true }, loaded), "kept: multi-strike", "multi-strike 160");
        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedMultiStrike = true }, PlainSword), null, "a single slash is not multi-strike");
        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedElementalBonus = true }, loaded), "kept: elemental bonus", "elemental");
        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedMaxDamageAtLeast = 400 }, loaded), "kept: max damage", "max damage at the limit");
        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedMaxDamageAtLeast = 401 }, loaded), null, "max damage under the limit");
        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedAnySpell = true }, loaded), "kept: has a spell", "any spell");
        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedSpellIds = "100, 2072" }, loaded), "kept: listed spell", "listed spell id");
        Check.Eq(IltChunkClap.CorruptedSkip(new IltGearState { KeepCorruptedSpellIds = "100 200" }, loaded), null, "other spell ids");

        Check.Eq(IltChunkClap.ParseSpellIds("1, 2;3 x -4 0").Count, 3, "positive ids only, any separator");
        Check.Eq(IltChunkClap.ParseSpellIds(null).Count, 0, "no list");
    }

    /// <summary>
    /// Silentkelpie 2026-10-18: a Coalesced piece was chunked (Red Aetheria Chunk arrived) with no
    /// result line, and it was filed as skipped after 10 s instead of counted.
    /// </summary>
    private static void WaitOutcome()
    {
        Check.Eq(IltChunkClap.WaitOutcome(500, targetStillCarried: true), IltChunkClap.ChunkWait.Waiting, "still carried, within the wait");
        Check.Eq(IltChunkClap.WaitOutcome(1_100, targetStillCarried: false), IltChunkClap.ChunkWait.Gone, "gone from the pack: chunked");
        Check.Eq(IltChunkClap.WaitOutcome(12_000, targetStillCarried: false), IltChunkClap.ChunkWait.Gone, "gone beats the timeout");
        Check.Eq(IltChunkClap.WaitOutcome(10_000, targetStillCarried: true), IltChunkClap.ChunkWait.TimedOut, "still carried at 10 s: timed out");
    }

    private static void BusyGate()
    {
        Check.True(IltChunkClap.AllowsChunkWhileBusy(isBusy: false, isPeace: true, chunkInCombat: false), "idle in peace");
        Check.False(IltChunkClap.AllowsChunkWhileBusy(isBusy: true, isPeace: true, chunkInCombat: true), "busy in peace waits");
        Check.True(IltChunkClap.AllowsChunkWhileBusy(isBusy: true, isPeace: false, chunkInCombat: true), "busy swinging with chunk-in-combat");
        Check.False(IltChunkClap.AllowsChunkWhileBusy(isBusy: true, isPeace: false, chunkInCombat: false), "busy in combat without it");
    }

    /// <summary>
    /// ACECustom /clap: red (Coalesced 42636 at 1-3, chunk 310147, powder 42644) with Empyrean
    /// Trinkets (34276); blue (42635, 310149, 300019) with Falatacot (34277). 42635/42636 were taken
    /// for chunks, and one side alone counted, so auto-clap sent /clap with nothing to pair.
    /// </summary>
    private static void ClapPairs()
    {
        const string Co = "Coalesced Aetheria";
        Check.Eq(IltChunkClap.ClapPartOf(42636, Co, 2), IltChunkClap.ClapPart.Red, "red Coalesced level 2");
        Check.Eq(IltChunkClap.ClapPartOf(42635, Co, 3), IltChunkClap.ClapPart.Blue, "blue Coalesced level 3");
        Check.Eq(IltChunkClap.ClapPartOf(42636, Co, 0), IltChunkClap.ClapPart.Red, "level not read yet: the server decides");
        Check.Eq(IltChunkClap.ClapPartOf(42636, Co, 4), IltChunkClap.ClapPart.None, "level 4 Coalesced is not clapped");
        Check.Eq(IltChunkClap.ClapPartOf(42636, "Aetheria", 1), IltChunkClap.ClapPart.None, "revealed aetheria is not clapped");
        Check.Eq(IltChunkClap.ClapPartOf(42637, Co, 1), IltChunkClap.ClapPart.None, "yellow has no clap use");
        Check.Eq(IltChunkClap.ClapPartOf(310147, "Red Aetheria Chunk", 0), IltChunkClap.ClapPart.Red, "red chunk");
        Check.Eq(IltChunkClap.ClapPartOf(310149, "Blue Aetheria Chunk", 0), IltChunkClap.ClapPart.Blue, "blue chunk");
        Check.Eq(IltChunkClap.ClapPartOf(310150, "Yellow Aetheria Chunk", 0), IltChunkClap.ClapPart.None, "yellow chunk");
        Check.Eq(IltChunkClap.ClapPartOf(42644, "Aetheria Powder", 0), IltChunkClap.ClapPart.Red, "red powder");
        Check.Eq(IltChunkClap.ClapPartOf(300019, "Aetheria Powder", 0), IltChunkClap.ClapPart.Blue, "blue powder");
        Check.Eq(IltChunkClap.ClapPartOf(34276, "Ancient Empyrean Trinket", 0), IltChunkClap.ClapPart.Empyrean, "Empyrean trinket");
        Check.Eq(IltChunkClap.ClapPartOf(34277, "Ancient Falatacot Trinket", 0), IltChunkClap.ClapPart.Falatacot, "Falatacot trinket");
        Check.Eq(IltChunkClap.ClapPartOf(0, "Red Aetheria Chunk", 0), IltChunkClap.ClapPart.Red, "no WCID yet: the chunk name");
        Check.Eq(IltChunkClap.ClapPartOf(0, Co, 1), IltChunkClap.ClapPart.None, "no WCID yet: a Coalesced piece's colour is unknown");
        Check.Eq(IltChunkClap.ClapPartOf(273, "Pyreal", 0), IltChunkClap.ClapPart.None, "anything else");

        var R = IltChunkClap.ClapPart.Red; var B = IltChunkClap.ClapPart.Blue;
        var E = IltChunkClap.ClapPart.Empyrean; var F = IltChunkClap.ClapPart.Falatacot;
        Check.True(IltChunkClap.CanClap(new[] { R, E }), "red + Empyrean");
        Check.True(IltChunkClap.CanClap(new[] { B, F }), "blue + Falatacot");
        Check.False(IltChunkClap.CanClap(new[] { R, F }), "red needs Empyrean, not Falatacot");
        Check.False(IltChunkClap.CanClap(new[] { B, E }), "blue needs Falatacot, not Empyrean");
        Check.False(IltChunkClap.CanClap(new[] { E, F }), "trinkets alone");
        Check.False(IltChunkClap.CanClap(new[] { R, B }), "aetheria alone");
        Check.False(IltChunkClap.CanClap(System.Array.Empty<IltChunkClap.ClapPart>()), "nothing carried");
    }

    private static void Cap()
    {
        Check.Eq(IltInventory.AetheriaMaxLevel, 10, "ILT max aetheria level");
        Check.Eq(IltChunkClap.ClampUnderLevel(0), 1, "below 1 clamps to 1");
        Check.Eq(IltChunkClap.ClampUnderLevel(7), 7, "7 (retail clamp was 6) is allowed");
        Check.Eq(IltChunkClap.ClampUnderLevel(11), 11, "11 chunks every level");
        Check.Eq(IltChunkClap.ClampUnderLevel(50), 11, "above 11 clamps to 11");
        Check.Eq(new IltGearState().ChunkAetheriaUnderLevel, 4, "the default cap is unchanged");
    }

    private static void Ratings()
    {
        var g = new IltGearState();
        Check.True(g.KeepAetheriaWithCrit && g.KeepAetheriaWithCritDamage && g.KeepAetheriaWithCritDamageResist, "all keep switches on by default");
        Check.False(IltChunkClap.KeptForRatings(g, 0, 0, 0), "no rating: chunkable");
        Check.True(IltChunkClap.KeptForRatings(g, 2, 0, 0), "C kept");
        Check.True(IltChunkClap.KeptForRatings(g, 0, 1, 0), "CD kept");
        Check.True(IltChunkClap.KeptForRatings(g, 0, 0, 3), "CDR kept");

        g.KeepAetheriaWithCrit = false;
        Check.False(IltChunkClap.KeptForRatings(g, 2, 0, 0), "C chunkable with its switch off");
        Check.True(IltChunkClap.KeptForRatings(g, 2, 0, 3), "CDR still keeps a C+CDR sigil");
        g.KeepAetheriaWithCritDamage = false;
        g.KeepAetheriaWithCritDamageResist = false;
        Check.False(IltChunkClap.KeptForRatings(g, 3, 3, 3), "every switch off: ratings don't protect");
    }

    /// <summary>
    /// The rating level: a C / CD / CDR rating at or above it keeps the piece, a lower one chunks
    /// (if the level cap allows). The default of 1 keeps any rating, as before.
    /// </summary>
    private static void RatingLevel()
    {
        var g = new IltGearState();
        Check.Eq(g.KeepAetheriaRatingAtLeast, 1, "default: any rating keeps");
        Check.True(IltChunkClap.KeptForRatings(g, 1, 0, 0), "C 1 kept at the default");

        g.KeepAetheriaRatingAtLeast = 2;
        Check.False(IltChunkClap.KeptForRatings(g, 1, 0, 0), "C 1 below level 2: chunks");
        Check.True(IltChunkClap.KeptForRatings(g, 2, 0, 0), "C 2 at level 2: kept");
        Check.True(IltChunkClap.KeptForRatings(g, 3, 0, 0), "C 3 above level 2: kept");
        Check.True(IltChunkClap.KeptForRatings(g, 1, 0, 2), "CDR 2 keeps a C 1 + CDR 2 piece");
        Check.False(IltChunkClap.KeptForRatings(g, 1, 1, 1), "every rating below the level: chunks");

        g.KeepAetheriaWithCrit = false;
        Check.False(IltChunkClap.KeptForRatings(g, 5, 0, 0), "a switch that is off ignores the level");

        Check.Eq(IltChunkClap.ClampRatingLevel(0), 1, "below 1 clamps to 1");
        Check.Eq(IltChunkClap.ClampRatingLevel(99), IltChunkClap.MaxRatingLevel, "clamped to the maximum");

        var coalescedC1 = PlainCoalesced with { Crit = 1 };
        var level2 = new IltGearState { KeepAetheriaRatingAtLeast = 2 };
        Check.Eq(IltChunkClap.AetheriaSkip(level2, coalescedC1), null, "an unlevelled C 1 Coalesced piece chunks at rating level 2");
        Check.Eq(IltChunkClap.AetheriaSkip(level2, PlainCoalesced with { Crit = 2 }), "kept for its C / CD / CDR rating", "a C 2 one is kept");
        Check.Eq(IltChunkClap.AetheriaSkip(level2, coalescedC1 with { MaxLevel = 6 }), "level at or above the cap",
            "a low rating does not override the level cap");
    }
}
