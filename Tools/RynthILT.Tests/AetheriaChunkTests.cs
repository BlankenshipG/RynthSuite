using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// The Lugian-hammer aetheria rules (Hub/IltChunkClap.cs): Infinite Leaftide aetheria goes up to
// level 10, and aetheria carrying a C, CD or CDR rating is kept unless its keep switch is off.
// Only the Lugian Aetheria Hammer chunks, and ILT's result lines name the item and the tool.
// The cap covers Coalesced Aetheria too, and /clap needs a colour with its own trinket.
internal static class AetheriaChunkTests
{
    public static void Register(Runner r)
    {
        r.Add("aetheria: the chunk cap runs to one past ILT's level 10", Cap);
        r.Add("aetheria: C / CD / CDR ratings are kept by default, each with its own switch", Ratings);
        r.Add("chunk: only the Lugian Aetheria Hammer is the tool, never a Lugian Hammer weapon", HammerOnly);
        r.Add("chunk: ILT's result lines (item and tool named) end the wait; combat lines don't", ResultLines);
        r.Add("aetheria: the level cap applies to Coalesced Aetheria too; no level read = never chunked", CoalescedCap);
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
        Check.False(IltChunkClap.UnderChunkCap(0, 11), "no level read: never chunked");
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

    /// <summary>
    /// Silentkelpie 2026-10-15: the Flaming Lugian Hammer (a loot weapon) matched " Lugian Hammer",
    /// chunked nothing, and all 9 Coalesced Aetheria timed out into the skip list.
    /// </summary>
    private static void HammerOnly()
    {
        Check.True(IltChunkClap.IsChunkHammer("Lugian Aetheria Hammer"), "the ILT tool");
        Check.True(IltChunkClap.IsChunkHammer("lugian aetheria hammer"), "any case");
        Check.False(IltChunkClap.IsChunkHammer("Flaming Lugian Hammer"), "a loot weapon is not the tool");
        Check.False(IltChunkClap.IsChunkHammer("Lugian Hammer"), "nor the plain one");
        Check.False(IltChunkClap.IsChunkHammer("Acid Lugian Hammer"), "nor any variant");
    }

    private static void ResultLines()
    {
        Check.Eq(IltChunkClap.ClassifyChat("You smash the Corrupted Sword with the Lugian Aetheria Hammer."), IltChunkClap.ChunkResult.Done, "ILT corrupted line");
        Check.Eq(IltChunkClap.ClassifyChat("You smash the aetheria with the hammer."), IltChunkClap.ChunkResult.Done, "aetheria line");
        Check.Eq(IltChunkClap.ClassifyChat("The aetheria is too powerful to chunk."), IltChunkClap.ChunkResult.Refused, "refused");
        Check.Eq(IltChunkClap.ClassifyChat("You're too busy!"), IltChunkClap.ChunkResult.Busy, "busy");
        Check.Eq(IltChunkClap.ClassifyChat("You smash Corrupted Shadow for 147 points of bludgeoning damage!"), IltChunkClap.ChunkResult.None,
            "a combat line naming a corrupted monster is no result");
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
}
