// IltSections.cs - The Mini Remote's tabs and the Guardian panel. Quests and Pets are universal
// tabs (every server: /myquests and summoning essences aren't ILT's); Character, Banking, Gear,
// Charms and Games are Infinite Leaftide tabs. All are picked from the Mini Remote's tab bar (MiniRemoteHud)
// or "/ra hub open <section>". Guardian is ILT only and a RynthCore ILT panel instead (the bar's
// ILT button, next to Bounties; "/rc guardian"), drawn by this plugin through
// RynthPluginRenderEmbed("guardian").
using System;

namespace RynthCore.Plugin.RynthAi.IltHub;

/// <summary>ILT sections: the Mini Remote's tabs, then the Guardian panel.</summary>
internal enum IltSection
{
    Character,
    Quests,
    Pet,
    Banking,
    Gear,
    Games,
    /// <summary>Registry charms (acquired / status / tier / server) and the Guardian Hand.</summary>
    Charms,
    // Keep Guardian last: MiniRemoteHud sizes its per-tab tables by it.
    Guardian,
}

internal static class IltSections
{
    /// <summary>
    /// The Mini Remote's tabs, in tab-bar order (after its own Remote tab): the universal ones
    /// first, then the Infinite Leaftide ones.
    /// </summary>
    public static readonly IltSection[] Tabs =
    {
        IltSection.Quests, IltSection.Pet,
        IltSection.Character, IltSection.Banking, IltSection.Gear, IltSection.Charms, IltSection.Games,
    };

    /// <summary>
    /// True for the tabs every server gets (Quests, Pets); the rest are Infinite Leaftide's and
    /// show only on an ILT world (or with the Force-ILT override).
    /// </summary>
    public static bool IsUniversal(IltSection s) => s is IltSection.Quests or IltSection.Pet;

    /// <summary>The Mini Remote's own tab (the remote itself): its tab id.</summary>
    public const string RemoteTab = "";

    public static string Label(IltSection s) => s switch
    {
        IltSection.Character => "Character",
        IltSection.Quests => "Quests",
        IltSection.Pet => "Pets",
        IltSection.Banking => "Banking",
        IltSection.Gear => "Gear",
        IltSection.Games => "Games",
        IltSection.Charms => "Charms",
        IltSection.Guardian => "Guardian",
        _ => s.ToString(),
    };

    /// <summary>
    /// The section's tab id: saved as HudState.MiniRemoteTab and sent by the engine's dashboard
    /// ("/ra hub tab &lt;id&gt;"). TryParse reads it back.
    /// </summary>
    public static string TabId(IltSection s) => s switch
    {
        IltSection.Character => "character",
        IltSection.Quests => "quests",
        IltSection.Pet => "pets",
        IltSection.Banking => "banking",
        IltSection.Gear => "gear",
        IltSection.Games => "games",
        IltSection.Charms => "charms",
        IltSection.Guardian => "guardian",
        _ => s.ToString().ToLowerInvariant(),
    };

    /// <summary>True for the sections drawn as Mini Remote tabs (all but the Guardian panel).</summary>
    public static bool IsTab(IltSection s) => s != IltSection.Guardian;

    /// <summary>Parses a "/ra hub open" section name (a few aliases accepted).</summary>
    public static bool TryParse(string text, out IltSection section)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "character": case "char": case "rates": section = IltSection.Character; return true;
            case "quests": case "quest": section = IltSection.Quests; return true;
            case "pets": case "pet": section = IltSection.Pet; return true;
            case "banking": case "bank": section = IltSection.Banking; return true;
            case "gear": section = IltSection.Gear; return true;
            case "games": case "game": section = IltSection.Games; return true;
            case "charms": case "charm": section = IltSection.Charms; return true;
            case "guardian": case "temple": case "riddle": section = IltSection.Guardian; return true;
            default: section = IltSection.Character; return false;
        }
    }

    /// <summary>"show" / "hide" / "toggle" (anything else toggles) applied to the current state.</summary>
    public static bool Resolve(string mode, bool current) => (mode ?? string.Empty).ToLowerInvariant() switch
    {
        "show" or "on" or "open" => true,
        "hide" or "off" or "close" => false,
        _ => !current,
    };
}
