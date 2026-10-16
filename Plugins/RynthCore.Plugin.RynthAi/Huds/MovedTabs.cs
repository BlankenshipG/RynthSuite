// MovedTabs.cs - Mini Remote tabs that moved out of the Mini Remote (2026-10-13), and where to.
//
// RynthILT's Character, Charms and Banking tabs left the Mini Remote:
//   Character, Charms -> RynthOracle's Character tab ("/ro character"): the character, its
//                        session and the ILT charms list (fed by "RynthILT.Data");
//   Banking           -> RynthInventory's Global Inventory "Banking" tab ("/ginv bank"): every
//                        character's bank on this server.
// RynthCore's Mini Remote "Char" section is "Session Stats" now (RynthAi's session stats), and a
// server is forced only in RynthCore Options > Diagnostics ("/rc diagnostics").
// Old saved tab ids, links and "/ra hub open|tab <name>" land on the new home through CommandFor.
//
// Pure: the host tests check the mapping offline.
using System;

namespace RynthCore.Plugin.RynthAi.Huds;

internal static class MovedTabs
{
    /// <summary>RynthOracle's Character tab.</summary>
    public const string OracleCharacter = "/ro character";

    /// <summary>RynthOracle's Pet Breeding tab (Infinite Leaftide; the same tracker as the Pets tab's Breeding sub-tab).</summary>
    public const string OracleBreeding = "/ro breeding";

    /// <summary>RynthInventory's Global Inventory "Banking" tab.</summary>
    public const string GlobalBank = "/ginv bank";

    /// <summary>RynthCore Options > Diagnostics (the server picker; RynthCore 2026.10.5.54+).</summary>
    public const string Diagnostics = "/rc diagnostics";

    /// <summary>RynthAi Settings (Advanced Settings panel; RynthCore SettingsFace).</summary>
    public const string Settings = "/rc ui open Settings";

    /// <summary>
    /// The chat command that opens where tab <paramref name="tab"/> lives now, or null when it is
    /// still a Mini Remote tab (or not a tab at all). Case and padding don't matter.
    /// </summary>
    public static string? CommandFor(string? tab) => (tab ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "character" or "char" or "rates" or "charms" or "charm" => OracleCharacter,
        "banking" or "bank" => GlobalBank,
        _ => null,
    };

    /// <summary>Where <paramref name="command"/> opens, in words, for chat replies and tooltips.</summary>
    public static string HomeOf(string command) => command switch
    {
        OracleCharacter => "RynthOracle's Character tab",
        OracleBreeding => "RynthOracle's Pet Breeding tab",
        GlobalBank => "Global Inventory > Banking",
        _ => command,
    };
}
