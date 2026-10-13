// SectionsManifest.cs - What RynthAi adds to RynthCore's Mini Remote sections.
//
// RynthCore 2026.10.5.46+ shows the Mini Remote as a rail of sections: its core twelve (Char, Map,
// Skills, Combat, Nav, Inventory, Crafting, Spells, Chat, Friends, Fellowship, Pets) and any a
// plugin adds. It reads each plugin's optional RynthPluginGetSectionsJson export every couple of
// seconds (schema: UI/Sections/SectionManifest.cs in RynthCore). RynthAi fills:
//   every world   Pets, Crafting, Spells, Fellowship (the views of those core sections) and its own
//                 Quests section
//   Infinite      the ILT Character and Charms views and the ILT server options in Char, Banking
//   Leaftide      and Gear in Inventory, and its own Games section
// Each view is a surface "section:<name>" RynthCore draws through RynthPluginRenderEmbed. A view
// whose ILT feature the server hasn't reported is listed but not enabled, with a note.
//
// Pure: the availability and sizes come in as functions, so the host tests build it offline.
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthAi.IltHub;

namespace RynthCore.Plugin.RynthAi.Huds;

internal static class SectionsManifest
{
    /// <summary>Prefix of every RynthAi section surface ("section:pets").</summary>
    public const string SurfacePrefix = "section:";

    /// <summary>The ILT server options in RynthCore's Char section.</summary>
    public const string IltOptionsSurface = SurfacePrefix + "ilt-options";
    public const string SpellsSurface = SurfacePrefix + "spells";
    public const string FellowshipSurface = SurfacePrefix + "fellowship";

    /// <summary>RynthCore's id for Infinite Leaftide.</summary>
    public const string IltServer = IltServerOptions.EngineServerId;

    /// <summary>One view RynthAi puts in a section.</summary>
    internal sealed record View(string Section, string Server, string Label, string Surface, bool Primary,
                                Vector2 Size, bool Enabled, string Note, string Tooltip, int Order);

    /// <summary>A section of RynthAi's own (beyond RynthCore's core twelve).</summary>
    internal sealed record Section(string Id, string Label, int Order);

    /// <summary>RynthAi's own sections: Quests after Crafting, Games (ILT) after Pets.</summary>
    public static readonly Section[] OwnSections =
    {
        new("quests", "Quests", 650),
        new("games", "Games", 1150),
    };

    private const string NotReportedNote = "Shows once the server reports this ILT feature on (the Char section's ILT server features can ask again).";

    /// <summary>The surface name of an ILT tab's view ("section:banking").</summary>
    public static string SurfaceOf(IltSection section) => SurfacePrefix + IltSections.TabId(section);

    /// <summary>
    /// Every view, from what is available now. <paramref name="available"/> says whether an ILT
    /// tab can show (IltHubController.SectionAvailable); <paramref name="size"/> is its body size.
    /// </summary>
    public static List<View> Views(Func<IltSection, bool> available, Func<IltSection, Vector2> size)
    {
        View Tab(IltSection s, string section, string server, string label, int order, string tooltip)
        {
            bool on = SafeAvailable(available, s);
            return new View(section, server, label, SurfaceOf(s), true, size(s), on, on ? string.Empty : NotReportedNote, tooltip, order);
        }

        return new List<View>
        {
            // Every world.
            Tab(IltSection.Pet, "pets", string.Empty, "Pets", 100, "Combat pets: the summon out, essences, and the pet picker (RynthAi)."),
            Tab(IltSection.Crafting, "crafting", string.Empty, "Crafting", 100, "Tinkering benches, chunking / clap and split arrows (RynthAi)."),
            Tab(IltSection.Quests, "quests", string.Empty, "Quests", 100, "Quest timers and favourites (RynthAi)."),
            new("spells", string.Empty, "Spells", SpellsSurface, true, new Vector2(900, 560), true, string.Empty,
                "The spell catalog and buff profiles (RynthAi).", 100),
            new("fellowship", string.Empty, "Fellowship", FellowshipSurface, true, new Vector2(380, 360), true, string.Empty,
                "Your fellowship: members, leader, XP sharing (RynthAi).", 100),

            // Infinite Leaftide.
            Tab(IltSection.Character, "char", IltServer, "ILT Character", 200, "Session rates and the ILT world status (RynthAi)."),
            Tab(IltSection.Charms, "char", IltServer, "Charms", 210, "Registry charms and the Guardian Hand (RynthAi)."),
            new("char", IltServer, "ILT server features", IltOptionsSurface, false, new Vector2(0, 260), true, string.Empty,
                "Which ILT features the server reports, the refresh, and the Force-ILT override (RynthAi).", 100),
            Tab(IltSection.Banking, "inventory", IltServer, "Banking", 200, "ILT bank balances and transfers (RynthAi)."),
            Tab(IltSection.Gear, "inventory", IltServer, "Gear", 210, "ILT gear tools (RynthAi)."),
            Tab(IltSection.Games, "games", IltServer, "Games", 100, "ILT games (RynthAi)."),
        };
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

    /// <summary>The ILT tab a "section:&lt;tab&gt;" surface draws, if it is one.</summary>
    public static bool TryTabOf(string surface, out IltSection section)
    {
        section = default;
        if (!surface.StartsWith(SurfacePrefix, StringComparison.Ordinal)) return false;
        string tab = surface.Substring(SurfacePrefix.Length);
        return IltSections.TryParse(tab, out section) && IltSections.IsTab(section)
               && string.Equals(IltSections.TabId(section), tab, StringComparison.Ordinal);
    }

    private static bool SafeAvailable(Func<IltSection, bool> available, IltSection s)
    {
        try { return available(s); }
        catch { return false; }
    }
}
