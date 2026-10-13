using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RynthCore.Engine.UI.Sections;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Mini Remote sections (RynthCore 2026.10.5.46): RynthCore sends its server verdicts (V lines) and
// section rail (X lines) in the "servermenu" payload, and reads RynthAi's views from
// RynthPluginGetSectionsJson. The manifest is checked against the engine's own parser and catalog
// (UI/Sections, linked), so the two sides can't drift apart.
internal static class SectionsTests
{
    public static void Register(Runner r)
    {
        r.Add("sections: V and X lines parse; groups unchanged", ParsesVerdicts);
        r.Add("sections: the engine verdict drives the ILT gate, and goes away with an older payload", VerdictDrivesGate);
        r.Add("sections: RynthAi's manifest passes the engine parser unchanged", ManifestRoundTrip);
        r.Add("sections: the engine catalog places RynthAi's views by server", CatalogPlacement);
        r.Add("sections: every ILT tab surface maps back to its tab", SurfaceRoundTrip);
        r.Add("sections: only non-default, chat-safe world patterns are copied to RynthCore", PatternsToCopy);
    }

    private const string Payload =
        "V\tilt\tInfinite Leaftide\t1\ton\t0\n" +
        "V\taelrynth\tAelrynth\t0\tauto\t0\n" +
        "X\tchar\tChar\n" +
        "X\tquests\tQuests\n" +
        "S\tilt\tInfinite Leaftide\t1\t/rc server ilt on\n" +
        "I\tProgression\t/rc progression\tPlanners.\t\n";

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
        Check.Eq(p.Sections[1].Id, "quests", "rail order kept");
        Check.Eq(p.Groups.Length, 1, "one server group");
        Check.Eq(p.Groups[0].Items.Single().Command, "/rc progression", "the group's item");

        ServerMenuPayload junk = ServerMenuModel.ParsePayload("V\tilt\n" + "X\t\t\n" + "V\tx\ty\tmaybe\tauto\t1\n");
        Check.Eq(junk.Groups.Length, 0, "no groups from junk");
        Check.True(junk.Servers.Length <= 1, "short verdict lines skipped");
    }

    private static void VerdictDrivesGate()
    {
        try
        {
            ServerMenuModel.Apply(Payload.Replace("V\tilt\tInfinite Leaftide\t1\ton\t0", "V\tilt\tInfinite Leaftide\t0\toff\t1"));
            Check.True(IltServerOptions.EngineDecides, "the engine decides once it sends verdicts");
            Check.Eq(ServerMenuModel.ServerActive("ILT"), false, "verdict lookup is case-insensitive");
            Check.False(ServerFeatureGate.IsEnabled("InfiniteLeaftide", null), "forced off beats the local list");
            Check.True(ServerFeatureGate.Describe(null).Contains("forced off"), "Settings explains the engine's verdict");

            ServerMenuModel.Apply(Payload);
            Check.True(ServerFeatureGate.IsEnabled("SomewhereElse", null), "forced on beats the local list");

            // An older RynthCore: groups only, no verdicts.
            ServerMenuModel.Apply("S\tilt\tInfinite Leaftide\t1\t/rc ilt on\n");
            Check.False(IltServerOptions.EngineDecides, "no verdicts, RynthAi decides");
            Check.True(ServerFeatureGate.IsEnabled("InfiniteLeaftide", null), "local list again");
        }
        finally { ServerMenuModel.Apply(string.Empty); }
    }

    private static List<SectionsManifest.View> SomeViews()
        => SectionsManifest.Views(s => s != IltSection.Banking, s => new Vector2(500, 400));

    private static void ManifestRoundTrip()
    {
        var views = SomeViews();
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
        SectionContribution banking = m.Contributions.Single(c => c.Surface == SectionsManifest.SurfaceOf(IltSection.Banking));
        Check.False(banking.Enabled, "unavailable Banking is disabled");
        Check.True(banking.Note.Length > 0, "with a note");
        Check.True(json.Length < SectionManifestParser.MaxJsonLength / 16, "small");
    }

    private static void CatalogPlacement()
    {
        SectionManifest m = SectionManifestParser.Parse("RynthAi", SectionsManifest.Build(SomeViews(), SectionsManifest.OwnSections), out _);
        var iltOn = new ServerState("ilt", "Infinite Leaftide", true, true, ServerForce.Auto, true, "");
        var iltOff = iltOn with { Active = false };

        List<SectionView> off = SectionCatalog.Build(new[] { m }, _ => false, new[] { iltOff });
        Check.True(off.Single(v => v.Id == "pets").Panes.Any(p => p.Contribution?.Surface == SectionsManifest.SurfaceOf(IltSection.Pet)),
            "Pets everywhere");
        Check.True(off.Any(v => v.Id == "quests"), "RynthAi's Quests everywhere");
        Check.False(off.Any(v => v.Id == "games"), "no Games off ILT");
        SectionView charOff = off.Single(v => v.Id == "char");
        Check.Eq(charOff.Panes.Length, 1, "Char: only the overview off ILT");
        Check.Eq(charOff.Groups.Length, 0, "Char: no ILT options off ILT");

        List<SectionView> on = SectionCatalog.Build(new[] { m }, _ => false, new[] { iltOn });
        SectionView charOn = on.Single(v => v.Id == "char");
        Check.True(charOn.Panes.Any(p => p.Title == "ILT Character") && charOn.Panes.Any(p => p.Title == "Charms"), "Char: ILT views on ILT");
        Check.Eq(charOn.Groups.Single().Items.Single().Surface, SectionsManifest.IltOptionsSurface, "Char: the ILT options in the server area");
        Check.True(on.Any(v => v.Id == "games"), "Games on ILT");
        Check.True(on.Single(v => v.Id == "spells").Panes.Any(p => p.Contribution?.Surface == SectionsManifest.SpellsSurface), "Spells");
        Check.True(on.Single(v => v.Id == "fellowship").Panes.Any(p => p.Contribution?.Surface == SectionsManifest.FellowshipSurface), "Fellowship");
    }

    private static void SurfaceRoundTrip()
    {
        foreach (IltSection s in IltSections.Tabs)
        {
            string surface = SectionsManifest.SurfaceOf(s);
            Check.True(SectionsManifest.TryTabOf(surface, out IltSection back) && back == s, "round trip " + surface);
            Check.Eq(SectionManifestParser.Surface(surface), surface, "the engine accepts " + surface);
        }
        Check.False(SectionsManifest.TryTabOf(SectionsManifest.SpellsSurface, out _), "spells isn't a tab");
        Check.False(SectionsManifest.TryTabOf("section:nonsense", out _), "unknown");
        Check.False(SectionsManifest.TryTabOf("pets", out _), "no prefix");
    }

    private static void PatternsToCopy()
    {
        var copied = RynthAiPlugin.PatternsToCopy(new[]
        {
            "InfiniteLeaftide", "*leaftide*", "Frosthaven", "frosthaven", " Coldeve* ", "", "bad\nline",
            new string('x', 65),
        }).ToArray();
        Check.Eq(string.Join("|", copied), "Frosthaven|Coldeve*", "defaults, duplicates, control characters and overlong names skipped");
        Check.Eq(RynthAiPlugin.PatternsToCopy(null).Count(), 0, "no list");
    }
}
