using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// The Lugian-hammer aetheria rules (Hub/IltChunkClap.cs): Infinite Leaftide aetheria goes up to
// level 10, and aetheria carrying a C, CD or CDR rating is kept unless its keep switch is off.
internal static class AetheriaChunkTests
{
    public static void Register(Runner r)
    {
        r.Add("aetheria: the chunk cap runs to one past ILT's level 10", Cap);
        r.Add("aetheria: C / CD / CDR ratings are kept by default, each with its own switch", Ratings);
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
