// IltSalvage.cs - Infinite Leaftide's own tinkering salvage, for the Mini Remote's Crafting tab.
//
// Served through RynthILT's "RynthServer.Profile" JSON (ProfileInterface.cs, the "salvage" list),
// which RynthAi's Crafting tab shows under "Server tinkering" on ILT; RynthAi itself knows no
// server's recipes. Taken from UB-IT's LeaftideImbueHelper via SilentKelpie (his RynthAi Crafting
// tab, rynth/RynthSuite PR 12, moved here 2026-10-15): the weapon crit lines (Yellow Garnet,
// Turquoise, White Quartz, Abyssal-Touched Gems, Tourmaline, Wool), Tiger Eye on missile weapons,
// Leather as the post-cap tink, the jewelry rating salvages (Silver, Copper, Green Jade,
// Tourmaline, and Serpentine + Steel sharing a cap of 10) and the trinket tiers (Diamond, Ruby,
// Gromnie Hide, Pyreal, prepared on Ammunition). ILT's recipes live in its database, so their
// chances are the server's: the Crafting runner reads the confirmation's percent.
using RynthCore.Plugin.ServerPack;

namespace RynthCore.Plugin.RynthILT.Hub;

internal static class IltSalvage
{
    // MaterialType (131) ids.
    private const int Wool = 8, BlackGarnet = 15, Diamond = 20, GreenJade = 24, Ruby = 38, TigerEye = 42, Tourmaline = 43,
        Turquoise = 44, WhiteQuartz = 46, YellowGarnet = 48, Leather = 52, GromnieHide = 54, Copper = 59, Pyreal = 62,
        Silver = 63, Steel = 64, Serpentine = 71;

    // ILT's flag ints on the item: 2 = that salvage is on.
    public const int IntJewelCopper = 370, IntJewelGreenJade = 371, IntJewelSilver = 374, IntJewelTourmaline = 375;
    public const int IntTrinketDiamond = 384, IntTrinketRuby = 385, IntTrinketGromnie = 386, IntTrinketPyreal = 387;

    /// <summary>Steel and Serpentine stack on ILT jewelry, at most this many combined.</summary>
    public const string JewelryStackGroup = "jewelry-stack";
    public const int JewelryStackCap = 10;

    private const string Once = "once", Tink = "tink";
    private const string PostCap = "100% tink once the 10 ordinary tinks are on";

    public static readonly ServerSalvage[] Rows =
    {
        new("melee", YellowGarnet, "Biting Strike (no tink slot)", Once, UsesTinkSlot: false, Order: 70),
        new("melee", Turquoise, "Crushing Blow (no tink slot)", Once, UsesTinkSlot: false, Order: 80),
        new("melee", WhiteQuartz, "Cleaving (no tink slot)", Once, UsesTinkSlot: false, Order: 90),
        new("melee", BlackGarnet, "Crit line (bag of Abyssal-Touched Gems)", Once, NameMatch: "Abyssal", Order: 100),
        new("melee", Tourmaline, "Crit line (no tink slot)", Once, UsesTinkSlot: false, Order: 104),
        new("melee", Wool, "Biting Strike (no tink slot)", Once, UsesTinkSlot: false, Order: 108),
        new("melee", Leather, PostCap, Once, UsesTinkSlot: false, Order: 995, AfterCap: true),

        new("missile", YellowGarnet, "Biting Strike (no tink slot)", Once, UsesTinkSlot: false, Order: 70),
        new("missile", Turquoise, "Crushing Blow (no tink slot)", Once, UsesTinkSlot: false, Order: 80),
        new("missile", BlackGarnet, "Crit line (bag of Abyssal-Touched Gems)", Once, NameMatch: "Abyssal", Order: 100),
        new("missile", Tourmaline, "Crit line (no tink slot)", Once, UsesTinkSlot: false, Order: 104),
        new("missile", Wool, "Biting Strike (no tink slot)", Once, UsesTinkSlot: false, Order: 108),
        new("missile", TigerEye, "+missile defense bonus", Tink, Order: 230),
        new("missile", Leather, PostCap, Once, UsesTinkSlot: false, Order: 995, AfterCap: true),

        new("caster", YellowGarnet, "Biting Strike (no tink slot)", Once, UsesTinkSlot: false, Order: 70),
        new("caster", Turquoise, "Crushing Blow (no tink slot)", Once, UsesTinkSlot: false, Order: 80),
        new("caster", Leather, PostCap, Once, UsesTinkSlot: false, Order: 995, AfterCap: true),

        // ILT keeps these in their own ints (370-376) with their own cap, not the 10 tinker slots.
        new("jewelry", Silver, "+1 critical damage rating", Once, UsesTinkSlot: false, AppliedInt: IntJewelSilver, Order: 10),
        new("jewelry", Copper, "+1 damage rating", Once, UsesTinkSlot: false, AppliedInt: IntJewelCopper, Order: 20),
        new("jewelry", GreenJade, "+1 damage resist rating", Once, UsesTinkSlot: false, AppliedInt: IntJewelGreenJade, Order: 30),
        new("jewelry", Tourmaline, "+1 critical damage resist rating", Once, UsesTinkSlot: false, AppliedInt: IntJewelTourmaline, Order: 40),
        new("jewelry", Serpentine, "+1 critical rating (stacks with Steel, 10 max)", Tink, UsesTinkSlot: false, Order: 50,
            StackGroup: JewelryStackGroup, StackCap: JewelryStackCap),
        new("jewelry", Steel, "Steel stack tier (stacks with Serpentine, 10 max)", Tink, UsesTinkSlot: false, Order: 60,
            StackGroup: JewelryStackGroup, StackCap: JewelryStackCap),

        // UB-IT's trinket imbues, in its order: Diamond, Ruby, Gromnie Hide, Pyreal.
        new("trinkets", Diamond, "Trinket tier: Diamond", Once, UsesTinkSlot: false, AppliedInt: IntTrinketDiamond, Prepared: true, Order: 10),
        new("trinkets", Ruby, "Trinket tier: Ruby", Once, UsesTinkSlot: false, AppliedInt: IntTrinketRuby, Prepared: true, Order: 20),
        new("trinkets", GromnieHide, "Trinket tier: Gromnie Hide", Once, UsesTinkSlot: false, AppliedInt: IntTrinketGromnie, Prepared: true, Order: 30),
        new("trinkets", Pyreal, "Trinket tier: Pyreal", Once, UsesTinkSlot: false, AppliedInt: IntTrinketPyreal, Prepared: true, Order: 40),
    };
}
