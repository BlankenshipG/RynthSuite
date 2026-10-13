using System.Collections.Generic;
using System.Linq;
using RynthCore.Engine.UI.Sections;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Mini Remote sections (RynthCore 2026.10.5.46+): RynthCore sends its server verdicts (V lines) and
// section rail (X lines) in the "servermenu" payload, and reads RynthAi's views from
// RynthPluginGetSectionsJson. RynthAi lists its own views (Pets, Crafting, Spells, Fellowship) and
// hosts RynthILT's ILT tabs (RynthILT.Hub); Quests are RynthOracle's. The manifest is checked
// against the engine's own parser and catalog (UI/Sections, linked), so the two sides can't drift.
internal static class SectionsTests
{
    public static void Register(Runner r)
    {
        r.Add("sections: V and X lines parse; groups unchanged", ParsesVerdicts);
        r.Add("sections: the engine verdict is looked up case-insensitively, and goes away with an older payload", VerdictLookup);
        r.Add("sections: RynthAi's manifest passes the engine parser unchanged", ManifestRoundTrip);
        r.Add("sections: the engine catalog places RynthAi's views by server", CatalogPlacement);
        r.Add("sections: tab surfaces map back to their tabs; foreign ids are refused", SurfaceRoundTrip);
    }

    private const string Payload =
        "V\tilt\tInfinite Leaftide\t1\ton\t0\n" +
        "V\taelrynth\tAelrynth\t0\tauto\t0\n" +
        "X\tchar\tChar\n" +
        "X\tgames\tGames\n" +
        "S\tilt\tInfinite Leaftide\t1\t/rc server ilt on\n" +
        "I\tProgression\t/rc progression\tPlanners.\t\n";

    /// <summary>RynthILT's tabs as its GetTabs would give them (Banking not reported, Games hidden).</summary>
    private static readonly IltTabInfo[] IltTabs =
    {
        new("character", "Character", 520, 440, Shown: true, Available: true),
        new("banking", "Banking", 560, 520, Shown: true, Available: false),
        new("gear", "Gear", 560, 520, Shown: true, Available: true),
        new("charms", "Charms", 560, 520, Shown: true, Available: true),
        new("games", "Games", 520, 440, Shown: false, Available: false),
        new("bogus id!", "Nope", 100, 100, Shown: true, Available: true),
        new("unknowntab", "Unknown", 100, 100, Shown: true, Available: true),
    };

    private static void ParsesVerdicts()
    {
        ServerMenuPayload p = ServerMenuModel.ParsePayload(Payload);
        Check.Eq(p.Servers.Length, 2, "two verdicts");
        Check.Eq(p.Servers[0].Id, "ilt", "first verdict id");
        Check.True(p.Servers[0].Active, "ILT active");
        Check.Eq(p.Servers[0].Force, "on", "ILT forced on");
        Check.False(p.Servers[0].Detected, "ILT not detected (forced)");
        Check.False(p.Servers[1].Active, "Aelrynth inactive");
        Check.Eq(p.Sections.Length, 2, "two sections");
        Check.Eq(p.Sections[1].Id, "games", "rail order kept");
        Check.Eq(p.Groups.Length, 1, "one server group");
        Check.Eq(p.Groups[0].Items.Single().Command, "/rc progression", "the group's item");

        ServerMenuPayload junk = ServerMenuModel.ParsePayload("V\tilt\n" + "X\t\t\n" + "V\tx\ty\tmaybe\tauto\t1\n");
        Check.Eq(junk.Groups.Length, 0, "no groups from junk");
        Check.True(junk.Servers.Length <= 1, "short verdict lines skipped");
    }

    private static void VerdictLookup()
    {
        try
        {
            ServerMenuModel.Apply(Payload.Replace("V\tilt\tInfinite Leaftide\t1\ton\t0", "V\tilt\tInfinite Leaftide\t0\toff\t1"));
            Check.Eq(ServerMenuModel.ServerActive("ILT"), false, "verdict lookup is case-insensitive");
            Check.Eq(ServerMenuModel.Server("ilt")?.Force, "off", "the override comes with it");

            ServerMenuModel.Apply(Payload);
            Check.Eq(ServerMenuModel.ServerActive("ilt"), true, "forced on");

            // An older RynthCore: groups only, no verdicts (the Mini Remote's ILT force then also
            // sets RynthILT's own "Treat this world as ILT").
            ServerMenuModel.Apply("S\tilt\tInfinite Leaftide\t1\t/rc ilt on\n");
            Check.True(ServerMenuModel.ServerActive("ilt") == null, "no verdicts: none to follow");
        }
        finally { ServerMenuModel.Apply(string.Empty); }
    }

    private static List<SectionsManifest.View> SomeViews() => SectionsManifest.Views(IltTabs);

    private static void ManifestRoundTrip()
    {
        var views = SomeViews();
        Check.False(views.Any(v => v.Section == "quests"), "no Quests view: RynthOracle has them");
        Check.False(views.Any(v => v.Surface == "section:games"), "a tab RynthILT doesn't show isn't listed");
        Check.False(views.Any(v => v.Surface.Contains("unknowntab") || v.Surface.Contains("bogus")), "unplaced or unsafe tab ids aren't listed");

        string json = SectionsManifest.Build(views, SectionsManifest.OwnSections);
        SectionManifest m = SectionManifestParser.Parse("RynthAi", json, out string error);
        Check.Eq(error, string.Empty, "no parse error");
        Check.Eq(m.Contributions.Length, views.Count, "every view survives");
        Check.Eq(m.Sections.Length, SectionsManifest.OwnSections.Length, "own sections survive");
        for (int i = 0; i < views.Count; i++)
        {
            SectionsManifest.View v = views[i];
            SectionContribution c = m.Contributions.Single(x => x.Surface == v.Surface && x.Section == v.Section);
            Check.Eq(c.Label, v.Label, "label " + v.Surface);
            Check.Eq(c.Server, v.Server, "server " + v.Surface);
            Check.Eq(c.Primary, v.Primary, "primary " + v.Surface);
            Check.Eq(c.Enabled, v.Enabled, "enabled " + v.Surface);
            Check.Eq(c.Order, v.Order, "order " + v.Surface);
        }
        SectionContribution banking = m.Contributions.Single(c => c.Surface == SectionsManifest.SurfaceOf("banking"));
        Check.False(banking.Enabled, "unavailable Banking is disabled");
        Check.True(banking.Note.Length > 0, "with a note");
        Check.True(json.Length < SectionManifestParser.MaxJsonLength / 16, "small");

        string noIlt = SectionsManifest.Build(SectionsManifest.Views(System.Array.Empty<IltTabInfo>()), SectionsManifest.OwnSections);
        SectionManifest plain = SectionManifestParser.Parse("RynthAi", noIlt, out string error2);
        Check.Eq(error2, string.Empty, "without RynthILT: no parse error");
        Check.Eq(plain.Contributions.Length, 4, "without RynthILT: Pets, Crafting, Spells, Fellowship");
    }

    private static void CatalogPlacement()
    {
        SectionManifest m = SectionManifestParser.Parse("RynthAi", SectionsManifest.Build(SomeViews(), SectionsManifest.OwnSections), out _);
        var iltOn = new ServerState("ilt", "Infinite Leaftide", true, true, ServerForce.Auto, true, "");
        var iltOff = iltOn with { Active = false };

        List<SectionView> off = SectionCatalog.Build(new[] { m }, _ => false, new[] { iltOff });
        Check.True(off.Single(v => v.Id == "pets").Panes.Any(p => p.Contribution?.Surface == SectionsManifest.PetsSurface),
            "Pets everywhere");
        Check.True(off.Single(v => v.Id == "crafting").Panes.Any(p => p.Contribution?.Surface == SectionsManifest.CraftingSurface),
            "Crafting everywhere");
        Check.False(off.Any(v => v.Id == "quests"), "no Quests section of RynthAi's");
        Check.False(off.Any(v => v.Id == "games"), "no Games off ILT");
        SectionView charOff = off.Single(v => v.Id == "char");
        Check.Eq(charOff.Panes.Length, 1, "Char: only the overview off ILT");

        List<SectionView> on = SectionCatalog.Build(new[] { m }, _ => false, new[] { iltOn });
        SectionView charOn = on.Single(v => v.Id == "char");
        Check.True(charOn.Panes.Any(p => p.Title == "ILT Character") && charOn.Panes.Any(p => p.Title == "Charms"), "Char: ILT views on ILT");
        SectionView inventoryOn = on.Single(v => v.Id == "inventory");
        Check.True(inventoryOn.Panes.Any(p => p.Title == "Banking") && inventoryOn.Panes.Any(p => p.Title == "Gear"), "Inventory: Banking and Gear on ILT");
        Check.True(on.Single(v => v.Id == "spells").Panes.Any(p => p.Contribution?.Surface == SectionsManifest.SpellsSurface), "Spells");
        Check.True(on.Single(v => v.Id == "fellowship").Panes.Any(p => p.Contribution?.Surface == SectionsManifest.FellowshipSurface), "Fellowship");
    }

    private static void SurfaceRoundTrip()
    {
        foreach (string id in new[] { "character", "charms", "banking", "gear", "games" })
        {
            string surface = SectionsManifest.SurfaceOf(id);
            Check.True(SectionsManifest.TryIltTabOf(surface, out string back) && back == id, "round trip " + surface);
            Check.Eq(SectionManifestParser.Surface(surface), surface, "the engine accepts " + surface);
        }
        Check.True(SectionsManifest.TryOwnTabOf(SectionsManifest.PetsSurface, out string pets) && pets == MiniRemoteTabs.Pets, "pets is RynthAi's");
        Check.True(SectionsManifest.TryOwnTabOf(SectionsManifest.CraftingSurface, out string craft) && craft == MiniRemoteTabs.Crafting, "crafting is RynthAi's");
        Check.False(SectionsManifest.TryIltTabOf(SectionsManifest.PetsSurface, out _), "pets isn't an ILT tab");
        Check.False(SectionsManifest.TryIltTabOf(SectionsManifest.SpellsSurface, out _), "spells isn't a tab");
        Check.False(SectionsManifest.TryIltTabOf("section:nonsense", out _), "unknown");
        Check.False(SectionsManifest.TryIltTabOf("banking", out _), "no prefix");
        Check.False(SectionsManifest.TryIltTabOf("section:quests", out _), "Quests are RynthOracle's");
    }
}
