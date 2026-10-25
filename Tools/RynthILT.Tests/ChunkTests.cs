using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// Lugian-hammer chunking (Hub/IltChunkClap.cs): only the Lugian Aetheria Hammer is the tool, and
// ILT's result lines name the item and the tool. Ported from SilentKelpie's fix (2026-10-15).
internal static class ChunkTests
{
    public static void Register(Runner r)
    {
        r.Add("chunk: only the Lugian Aetheria Hammer is the tool, never a Lugian Hammer weapon", HammerOnly);
        r.Add("chunk: ILT's result lines (item and tool named) end the wait; combat lines don't", ResultLines);
    }

    /// <summary>The Flaming Lugian Hammer (a loot weapon) matched " Lugian Hammer", chunked nothing,
    /// and all 9 Coalesced Aetheria timed out into the skip list.</summary>
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
}
