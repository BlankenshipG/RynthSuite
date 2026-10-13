// SectionsManifest.cs - What RynthAi adds to RynthCore's Mini Remote sections.
//
// RynthCore 2026.10.5.46+ shows the Mini Remote as a rail of sections: its core twelve (Char, Map,
// Skills, Combat, Nav, Inventory, Crafting, Spells, Chat, Friends, Fellowship, Pets) and any a
// plugin adds. It reads each plugin's optional RynthPluginGetSectionsJson export every couple of
// seconds (schema: UI/Sections/SectionManifest.cs in RynthCore). RynthAi fills:
//   every world   Pets, Crafting, Spells and Fellowship (the views of those core sections)
//   Infinite      RynthILT's tabs, hosted here as they are in RynthAi's own Mini Remote (RynthILT.Hub,
//   Leaftide      MiniRemote/IltClient.cs): ILT Character and Charms in Char, Banking and Gear in
//                 Inventory, and a Games section of RynthAi's own
// Quests are RynthOracle's (since 2026-10-13), and RynthILT draws its ILT settings and /fship
// views itself (its own manifest), so neither is listed here.
// Each view is a surface "section:<name>" RynthCore draws through RynthPluginRenderEmbed. An ILT
// view whose feature the server hasn't reported is listed but not enabled, with a note.
//
// Pure: RynthILT's tabs come in as a list, so the host tests build it offline.
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthAi.MiniRemote;

namespace RynthCore.Plugin.RynthAi.Huds;

internal static class SectionsManifest
{
    /// <summary>Prefix of every RynthAi section surface ("section:pets").</summary>
    public const string SurfacePrefix = "section:";

    public const string PetsSurface = SurfacePrefix + MiniRemoteTabs.Pets;
    public const string CraftingSurface = SurfacePrefix + MiniRemoteTabs.Crafting;
    public const string SpellsSurface = SurfacePrefix + "spells";
    public const string FellowshipSurface = SurfacePrefix + "fellowship";

    /// <summary>RynthCore's id for Infinite Leaftide.</summary>
    public const string IltServer = "ilt";

    /// <summary>Longest tab id taken from RynthILT (its ids are short ASCII words).</summary>
    public const int MaxTabIdLength = 32;

    /// <summary>One view RynthAi puts in a section.</summary>
    internal sealed record View(string Section, string Server, string Label, string Surface, bool Primary,
                                Vector2 Size, bool Enabled, string Note, string Tooltip, int Order);

    /// <summary>A section of RynthAi's own (beyond RynthCore's core twelve).</summary>
    internal sealed record Section(string Id, string Label, int Order);

    /// <summary>RynthAi's own sections: Games (ILT) after Pets.</summary>
    public static readonly Section[] OwnSections =
    {
        new("games", "Games", 1150),
    };

    private const string NotReportedNote = "Shows once the server reports this ILT feature on (RynthILT's ILT settings can ask again).";

    /// <summary>Where each of RynthILT's tabs goes: section, label, order, tooltip. Unknown ids aren't listed.</summary>
    private static readonly Dictionary<string, (string Section, string Label, int Order, string Tooltip)> IltPlacement =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["character"] = ("char", "ILT Character", 200, "Session rates and the ILT world status (RynthILT)."),
            ["charms"] = ("char", "Charms", 210, "Registry charms and the Guardian Hand (RynthILT)."),
            ["banking"] = ("inventory", "Banking", 200, "ILT bank balances, deposits, withdrawals and transfers (RynthILT)."),
            ["gear"] = ("inventory", "Gear", 210, "ILT gear tools (RynthILT)."),
            ["games"] = ("games", "Games", 100, "ILT games (RynthILT)."),
        };

    /// <summary>The surface name of a tab's view ("section:banking").</summary>
    public static string SurfaceOf(string tabId) => SurfacePrefix + tabId;

    /// <summary>
    /// Every view, from what is available now: RynthAi's own (every world) and
    /// <paramref name="iltTabs"/> (RynthILT's tabs with their sizes and whether the server has
    /// each feature on; empty without RynthILT or off ILT).
    /// </summary>
    public static List<View> Views(IReadOnlyList<IltTabInfo> iltTabs)
    {
        var views = new List<View>
        {
            // Every world.
            new("pets", string.Empty, "Pets", PetsSurface, true, MiniRemoteTabs.Size(MiniRemoteTabs.Pets), true, string.Empty,
                "Combat pets: the summon out, essences, and the pet picker (RynthAi).", 100),
            new("crafting", string.Empty, "Crafting", CraftingSurface, true, MiniRemoteTabs.Size(MiniRemoteTabs.Crafting), true, string.Empty,
                "Tinkering benches, chunking / clap and split arrows (RynthAi).", 100),
            new("spells", string.Empty, "Spells", SpellsSurface, true, new Vector2(900, 560), true, string.Empty,
                "The spell catalog and buff profiles (RynthAi).", 100),
            new("fellowship", string.Empty, "Fellowship", FellowshipSurface, true, new Vector2(380, 360), true, string.Empty,
                "Your fellowship: members, leader, XP sharing (RynthAi; also RynthOracle's Fellowship tab).", 100),
        };

        // Infinite Leaftide: RynthILT's tabs that it shows on this world.
        foreach (IltTabInfo tab in iltTabs)
        {
            if (!tab.Shown || !IsTabId(tab.Id) || !IltPlacement.TryGetValue(tab.Id, out var place)) continue;
            var size = new Vector2(MathF.Max(0f, tab.Width), MathF.Max(0f, tab.Height));
            views.Add(new View(place.Section, IltServer, place.Label, SurfaceOf(tab.Id.ToLowerInvariant()), true, size,
                tab.Available, tab.Available ? string.Empty : NotReportedNote, place.Tooltip, place.Order));
        }
        return views;
    }

    /// <summary>The manifest JSON for <paramref name="views"/> (UTF-8 text, schema version 1).</summary>
    public static string Build(IReadOnlyList<View> views, IReadOnlyList<Section> sections)
    {
        using var stream = new MemoryStream(4096);
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteStartArray("sections");
            foreach (Section s in sections)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("label", s.Label);
                w.WriteNumber("order", s.Order);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("contributions");
            foreach (View v in views)
            {
                w.WriteStartObject();
                w.WriteString("section", v.Section);
                w.WriteString("server", v.Server);
                w.WriteString("label", v.Label);
                w.WriteString("surface", v.Surface);
                w.WriteBoolean("primary", v.Primary);
                if (v.Size.X > 0) w.WriteNumber("width", v.Size.X);
                if (v.Size.Y > 0) w.WriteNumber("height", v.Size.Y);
                w.WriteBoolean("enabled", v.Enabled);
                if (v.Note.Length > 0) w.WriteString("note", v.Note);
                if (v.Tooltip.Length > 0) w.WriteString("tooltip", v.Tooltip);
                w.WriteNumber("order", v.Order);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The Mini Remote tab of RynthAi's own a surface draws ("pets", "crafting"), if it is one.</summary>
    public static bool TryOwnTabOf(string surface, out string tabId)
    {
        tabId = surface switch
        {
            PetsSurface => MiniRemoteTabs.Pets,
            CraftingSurface => MiniRemoteTabs.Crafting,
            _ => string.Empty,
        };
        return tabId.Length > 0;
    }

    /// <summary>The RynthILT tab a "section:&lt;tab&gt;" surface draws, if it names one we place.</summary>
    public static bool TryIltTabOf(string surface, out string tabId)
    {
        tabId = string.Empty;
        if (!surface.StartsWith(SurfacePrefix, StringComparison.Ordinal)) return false;
        string id = surface.Substring(SurfacePrefix.Length);
        if (!IsTabId(id) || !IltPlacement.ContainsKey(id)) return false;
        tabId = id;
        return true;
    }

    /// <summary>A short lowercase-ASCII id (letters, digits, '-'): nothing else is spliced into a surface name.</summary>
    private static bool IsTabId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxTabIdLength) return false;
        foreach (char c in id)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')) return false;
        return true;
    }
}
