// ============================================================================
//  RynthSuite - Plugins/Shared/RynthIltHubApi.cs
//  The "RynthILT.Hub" interface: RynthILT's Infinite Leaftide features as RynthAi's
//  Mini Remote shows them. The Mini Remote stays RynthAi's (its window, tab bar,
//  Remote tab, Quests and Pets tabs); RynthILT supplies the ILT tabs (Character,
//  Banking, Gear, Charms, Games), a few ILT sections drawn inside RynthAi's
//  surfaces, the server's ILT feature bits and the "/ra hub ..." ILT verbs.
//  Compiled into both plugins (linked source), so the layout can't drift.
//
//  Rules (as the RynthAi.* interfaces, Shared/RynthAiScriptApi.cs):
//  - Append-only: new functions go at the end, and Version goes up.
//  - Strings are UTF-8, NUL-terminated.
//  - Threads: each function says where it may be called. The render-thread ones
//    draw into the caller's current ImGui window and context (the engine's cimgui,
//    shared by both plugins).
//  - Every function returns safely (no exception crosses the boundary), and returns
//    0 / "unknown" once RynthILT has shut down.
//  - The table is allocated once per loaded copy of RynthILT and never freed: its
//    functions stay callable from the render thread while plugins shut down (a
//    plugin DLL is never unloaded, see the engine's PluginManager.PluginsUnloaded).
//    Shutdown sets Version to 0; a caller re-queries (host GetPluginInterface)
//    when it sees that, after a plugin reload, or every few seconds.
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

/// <summary>Constants of the "RynthILT.Hub" interface.</summary>
public static class RynthIltHub
{
    /// <summary>The providing plugin's display name.</summary>
    public const string PluginName = "RynthILT";

    /// <summary>Tab flag: the Mini Remote shows this tab now (an ILT world, or forced, and the tab is usable).</summary>
    public const uint TabShown = 1;
    /// <summary>Tab flag: the server reports the tab's feature on (Character is always available).</summary>
    public const uint TabAvailable = 2;

    // Sections RynthAi draws through RenderSection (each draws nothing off ILT).
    /// <summary>Remote tab, Bank section: the ILT bank balances.</summary>
    public const string SectionRemoteBank = "remote-bank";
    /// <summary>Remote tab, Guardian line: the Temple guardian's last answer.</summary>
    public const string SectionRemoteGuardian = "remote-guardian";
    /// <summary>Quests tab: the "Quest bonus (/qb)" list.</summary>
    public const string SectionQuestBonus = "quests-qb";
    /// <summary>Pets tab: the /pets and /shinies logs.</summary>
    public const string SectionPetLogs = "pets-logs";
    /// <summary>
    /// Pets tab, Breeding sub-tab: the pet breeding tracker, the sub-tab's whole body (RynthILT 0.3.1+;
    /// older copies draw nothing). RynthAi shows the sub-tab on an ILT world unless FeaturePetBreeding is off.
    /// </summary>
    public const string SectionPetBreeding = "pets-breeding";
    /// <summary>Mini Remote menu, under Infinite Leaftide: why no ILT feature is on, the Force-ILT override, Refresh.</summary>
    public const string SectionIltStatus = "ilt-status";
    /// <summary>Mini Remote menu without RynthCore's server list: RynthILT's own Infinite Leaftide items.</summary>
    public const string SectionServerMenu = "server-menu";

    /// <summary>Feature keys RynthAi reads (GetFeature).</summary>
    public const string FeatureQuests = "quests";
    public const string FeaturePetRefill = "pet_refill";
    public const string FeatureUniversalMastery = "universal_mastery";
    /// <summary>ACECustom siphon lenses (monster capture into a pet essence).</summary>
    public const string FeatureMonsterCapture = "monster_capture";
    public const string FeatureRequireComps = "require_spell_comps";
    /// <summary>Pet breeding (the Pets tab's Breeding sub-tab is hidden while the server reports it off).</summary>
    public const string FeaturePetBreeding = "pet_breeding";
}

/// <summary>One ILT tab as GetTabs hands it out. Blittable.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct IltTabV1
{
    /// <summary>The tab id (UTF-8, NUL-terminated): saved as the Mini Remote's tab, "/ra hub open &lt;id&gt;".</summary>
    public fixed byte Id[24];
    /// <summary>The tab bar label (UTF-8, NUL-terminated).</summary>
    public fixed byte Label[32];
    /// <summary>The size of the tab body (the child it draws in).</summary>
    public float Width;
    public float Height;
    /// <summary>RynthIltHub.Tab* bits.</summary>
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthIltHubApiV1
{
    public const string InterfaceName = "RynthILT.Hub";
    public const uint InterfaceVersion = 1;

    public uint Version;      // InterfaceVersion of the table RynthILT filled; 0 once it has shut down
    public uint StructSize;   // sizeof(RynthIltHubApiV1) as RynthILT built it

    /// <summary>IsIltWorld(): 1 when this world is Infinite Leaftide / ACECustom (or forced). Any thread.</summary>
    public delegate* unmanaged[Cdecl]<int> IsIltWorld;

    /// <summary>GetFeature(key): the server's bit for a feature, -1 unknown, 0 off, 1 on (-1 off ILT). Any thread.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int> GetFeature;

    /// <summary>GetRates(coinsPerHour, pyrealsPerHour): ILT session rates; 0 (nothing written) off ILT. Any thread.</summary>
    public delegate* unmanaged[Cdecl]<double*, double*, int> GetRates;

    /// <summary>GetForceIlt(): this character's "Treat this world as ILT" override (1 on). Any thread.</summary>
    public delegate* unmanaged[Cdecl]<int> GetForceIlt;

    /// <summary>SetForceIlt(on): sets that override. Any thread (applied and saved on RynthILT's next tick).</summary>
    public delegate* unmanaged[Cdecl]<int, void> SetForceIlt;

    /// <summary>
    /// GetTabs(tabs, capacity): every ILT tab in tab-bar order with its flags. Writes up to
    /// capacity, returns how many there are (tabs null: just the count). Render thread.
    /// </summary>
    public delegate* unmanaged[Cdecl]<IltTabV1*, int, int> GetTabs;

    /// <summary>RenderTab(id): draws that tab's body into the current (child) window; 1 = drawn. Render thread.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int> RenderTab;

    /// <summary>RenderSection(id): draws a RynthIltHub.Section* into the current window; 1 = drawn. Render thread.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int> RenderSection;

    /// <summary>
    /// Command(text): one of the ILT "/ra" verbs, as typed after "/ra" ("hub bank", "hub attr run",
    /// "guardian handin", "bounty show", "rates reset"). RynthILT answers in chat itself.
    /// 1 = handled, 0 = not an ILT verb. Pump thread.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, int> Command;
}
