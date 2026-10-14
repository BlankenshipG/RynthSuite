// IltSections.cs - The Infinite Leaftide tabs of RynthAi's Mini Remote and the Guardian panel.
// Originally SilentKelpie's ILT Hub sections (RynthAi IltHub/); moved to RynthILT 2026-10-13.
//
// Gear and Games are Mini Remote tabs: RynthAi's MiniRemoteHud draws the tab bar (after its Remote,
// Quests and Pets tabs) and asks RynthILT for each tab's body (RynthILT.Hub GetTabs / RenderTab);
// "/ra hub open <section>" picks one. Guardian is a RynthCore ILT panel instead (the bar's ILT
// button, next to Bounties; "/rc guardian"), drawn by this plugin through
// RynthPluginRenderEmbed("guardian"). The Quests and Pets tabs are RynthAi's (every server:
// /myquests and summoning essences aren't ILT's).
//
// Character, Charms and Banking were Mini Remote tabs until 2026-10-13. They moved out:
//   Character -> RynthCore's Mini Remote "Session Stats" section (session rates; the force-server
//                override lives only in RynthCore Options > Diagnostics) and RynthOracle's
//                Character tab ("/ro character");
//   Charms    -> RynthOracle's Character tab, a list fed by "RynthILT.Data" ("/ro character");
//   Banking   -> RynthInventory's Global Inventory "Banking" tab, every character on this server
//                ("/ginv bank").
// Their names still parse, so old links and commands land on the new home (MovedCommand).
using System;
using System.Numerics;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>ILT sections: the Mini Remote's ILT tabs, the sections that moved out, then the Guardian panel.</summary>
internal enum IltSection
{
    /// <summary>Moved out (2026-10-13): RynthOracle's Character tab.</summary>
    Character,
    /// <summary>Moved out (2026-10-13): RynthInventory's Global Inventory "Banking" tab.</summary>
    Banking,
    Gear,
    Games,
    /// <summary>Moved out (2026-10-13): RynthOracle's Character tab (the charms list).</summary>
    Charms,
    // Keep Guardian last.
    Guardian,
}

internal static class IltSections
{
    /// <summary>The ILT tabs, in tab-bar order (RynthAi puts them after its own tabs).</summary>
    public static readonly IltSection[] Tabs =
    {
        IltSection.Gear, IltSection.Games,
    };

    /// <summary>The Mini Remote's own tab (the remote itself): its tab id.</summary>
    public const string RemoteTab = "";

    /// <summary>RynthOracle's Character tab: the character, its session and the charms list.</summary>
    public const string OracleCharacterCommand = "/ro character";

    /// <summary>RynthInventory's Global Inventory "Banking" tab: every character's bank on this server.</summary>
    public const string GlobalBankCommand = "/ginv bank";

    public static string Label(IltSection s) => s switch
    {
        IltSection.Character => "Character",
        IltSection.Banking => "Banking",
        IltSection.Gear => "Gear",
        IltSection.Games => "Games",
        IltSection.Charms => "Charms",
        IltSection.Guardian => "Guardian",
        _ => s.ToString(),
    };

    /// <summary>
    /// The section's tab id: saved as RynthAi's HudState.MiniRemoteTab and sent by the engine's
    /// dashboard ("/ra hub tab &lt;id&gt;", "/rc miniremote tab &lt;id&gt;"). TryParse reads it back.
    /// </summary>
    public static string TabId(IltSection s) => s switch
    {
        IltSection.Character => "character",
        IltSection.Banking => "banking",
        IltSection.Gear => "gear",
        IltSection.Games => "games",
        IltSection.Charms => "charms",
        IltSection.Guardian => "guardian",
        _ => s.ToString().ToLowerInvariant(),
    };

    /// <summary>True for the sections drawn as Mini Remote tabs (Gear and Games).</summary>
    public static bool IsTab(IltSection s) => s is IltSection.Gear or IltSection.Games;

    /// <summary>
    /// Where a section that moved out of the Mini Remote lives now, as the chat command that opens
    /// it; null for a section that is still RynthILT's (a tab or the Guardian panel).
    /// </summary>
    public static string? MovedCommand(IltSection s) => s switch
    {
        IltSection.Character or IltSection.Charms => OracleCharacterCommand,
        IltSection.Banking => GlobalBankCommand,
        _ => null,
    };

    /// <summary>"quests" / "pets" (and their aliases): RynthAi's universal tabs, not ILT sections.</summary>
    public static bool IsUniversalTabName(string text) => (text ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "quests" or "quest" or "pets" or "pet" => true,
        _ => false,
    };

    /// <summary>Parses a "/ra hub open" section name (a few aliases accepted).</summary>
    public static bool TryParse(string text, out IltSection section)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "character": case "char": case "rates": section = IltSection.Character; return true;
            case "banking": case "bank": section = IltSection.Banking; return true;
            case "gear": section = IltSection.Gear; return true;
            case "games": case "game": section = IltSection.Games; return true;
            case "charms": case "charm": section = IltSection.Charms; return true;
            case "guardian": case "temple": case "riddle": section = IltSection.Guardian; return true;
            default: section = IltSection.Gear; return false;
        }
    }

    /// <summary>
    /// Size of a Mini Remote tab's body (the old section window's default size): the tab draws in a
    /// child this big, and the Mini Remote (or the engine window hosting it) fits to it.
    /// </summary>
    public static Vector2 TabSize(IltSection section) => section switch
    {
        IltSection.Gear => new Vector2(560, 520),
        IltSection.Games => new Vector2(520, 440),
        _ => new Vector2(520, 440),
    };

    /// <summary>"show" / "hide" / "toggle" (anything else toggles) applied to the current state.</summary>
    public static bool Resolve(string mode, bool current) => (mode ?? string.Empty).ToLowerInvariant() switch
    {
        "show" or "on" or "open" => true,
        "hide" or "off" or "close" => false,
        _ => !current,
    };
}
