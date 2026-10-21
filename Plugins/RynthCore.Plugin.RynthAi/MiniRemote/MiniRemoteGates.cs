// MiniRemoteGates.cs - Which Infinite Leaftide pieces of RynthAi's own Mini Remote show on this world.
//
// The Pets and Crafting tabs are on every world (summoning essences and retail tinkering are), but
// parts of them are Infinite Leaftide's and must not show anywhere else (owner, 2026-10-20):
//   Pets > Breeding        RynthILT's pet breeding tracker
//   Crafting > Rings       ILT's ring inscription chain (ring spells)
//   Crafting > Trinkets    ILT trinket salvage (retail AC has none; a server plugin or custom rows may add some)
//   Remote Bank / Guardian RynthILT's sections, and their Options checkboxes
// "ILT world" is RynthILT's verdict (IltClient.IsIltWorld: detected, or forced in RynthCore Options >
// Diagnostics); without RynthILT it is never ILT. RynthILT's own tabs (Gear, Games, Vaulted Dungeons)
// and RynthCore's ILT panels (Bounties, Guardian, Progression) are gated by those plugins themselves.
// Pure: the host tests drive it.
namespace RynthCore.Plugin.RynthAi.MiniRemote;

internal static class MiniRemoteGates
{
    /// <summary>
    /// Pets > Breeding: RynthILT is loaded, the world is ILT, and the server hasn't reported pet
    /// breeding off (<paramref name="breedingFeature"/>: -1 unknown, 0 off, 1 on).
    /// </summary>
    public static bool ShowBreeding(bool iltLoaded, bool iltWorld, int breedingFeature)
        => iltLoaded && iltWorld && breedingFeature != 0;

    /// <summary>Crafting > Rings (ring inscription spells): ILT worlds only.</summary>
    public static bool ShowRings(bool iltWorld) => iltWorld;

    /// <summary>Crafting > Trinkets: on ILT, or when this world's server plugin or the player's custom rows give it something.</summary>
    public static bool ShowTrinkets(bool iltWorld, int trinketRows) => iltWorld || trinketRows > 0;

    /// <summary>The remote's Bank and Guardian sections (and their Options checkboxes): ILT worlds only.</summary>
    public static bool ShowIltSection(bool iltWorld, bool enabled) => iltWorld && enabled;
}
