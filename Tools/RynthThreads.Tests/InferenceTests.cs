using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthThreads.Threads;
using RynthCore.RynthAiTests.Harness;

namespace RynthThreads.Tests;

// The odds (Threads/PressInference.cs): rates, intervals and comparisons, on the first real presses
// and on made-up ones.
internal static class InferenceTests
{
    public static void Register(Runner r)
    {
        r.Add("infer: the 4 real Lead Scarab presses (old JSON lines): rates, intervals, Affinity group", RealPresses);
        r.Add("infer: with/without a component; 'more likely' only when clear; 'always' only at 10+", WithWithout);
        r.Add("infer: Wilson intervals and the Fisher test", Statistics);
        r.Add("infer: exact combinations of components", Combinations);
        r.Add("infer: No components and All presses rows; incomplete presses left out", NoComponentsAndAll);
        r.Add("infer: Affinity grouping and the split by material", AffinityGroup);
        r.Add("infer: per-modifier view: overall, no components, each component (0% too)", ModifierView);
        r.Add("infer: values by level, tags, results and modifiers per press", ReportDetails);
        r.Add("infer: the sample press runs through the whole report", SampleReport);
    }

    /// <summary>The first 4 presses from presses.jsonl (2026-10-12, all Lead Scarab), exactly as written.</summary>
    internal static readonly string[] RealLines =
    {
        """{"v":1,"id":"ccbf1dd788704e08","time":"2026-10-12T23:44:32.8501639Z","server":"DreamWeave","character":"Drakkon","fragment":"Raw Fragment","fragmentLevel":185,"tool":"Fragment Press","outcome":"Pressed. It holds.","componentsSeen":true,"components":["Lead Scarab"],"random":"Remaining slots drawn at random.","modifiers":[{"name":"Radiant","description":"kills pay x1.84 luminance","numbers":[{"value":1.84,"kind":"mult","text":"x1.84"}]},{"name":"Amethyst Affinity","description":"10% of kills leave amethyst to salvage","numbers":[{"value":10,"kind":"pct","text":"10%"}]},{"name":"Precise","description":"monsters have \u002B27 critical strike rating, landing more critical hits","numbers":[{"value":27,"kind":"plus","text":"\u002B27"}]},{"name":"Guarded","description":"the boss has x1.91 health","numbers":[{"value":1.91,"kind":"mult","text":"x1.91"}]},{"name":"Savage","description":"monsters attack with \u002B35 damage rating, hitting harder","numbers":[{"value":35,"kind":"plus","text":"\u002B35"}]}],"resultLevel":175,"entries":3,"complete":true,"end":"done","raw":["Using the Raw Fragment (Level 185) with the Fragment Press","Pressed. It holds.","Components selected: Lead Scarab","Remaining slots drawn at random.","The gem carries:","  Radiant - kills pay x1.84 luminance","  Amethyst Affinity - 10% of kills leave amethyst to salvage","  Precise - monsters have \u002B27 critical strike rating, landing more critical hits","  Guarded - the boss has x1.91 health","  Savage - monsters attack with \u002B35 damage rating, hitting harder","Level 175, 3 entries."],"extra":[]}""",
        """{"v":1,"id":"6613196a36424f8c","time":"2026-10-12T23:50:31.1229501Z","server":"DreamWeave","character":"Drakkon","fragment":"Raw Fragment","fragmentLevel":185,"tool":"Fragment Press","outcome":"Pressed. It holds.","componentsSeen":true,"components":["Lead Scarab"],"random":"Remaining slots drawn at random.","modifiers":[{"name":"Radiant","description":"kills pay x1.75 luminance","numbers":[{"value":1.75,"kind":"mult","text":"x1.75"}]},{"name":"Iron Affinity","description":"8% of kills leave iron to salvage","numbers":[{"value":8,"kind":"pct","text":"8%"}]},{"name":"Teeming","description":"67% more monsters","numbers":[{"value":67,"kind":"pct","text":"67%"}]},{"name":"Guarded","description":"the boss has x1.83 health","numbers":[{"value":1.83,"kind":"mult","text":"x1.83"}]},{"name":"Swift","description":"monsters move at x1.21 speed","numbers":[{"value":1.21,"kind":"mult","text":"x1.21"}]},{"name":"Shieldbreaker","description":"monsters ignore 54% of your shield","numbers":[{"value":54,"kind":"pct","text":"54%"}]}],"resultLevel":175,"entries":3,"complete":true,"end":"done","raw":["Using the Raw Fragment (Level 185) with the Fragment Press","Pressed. It holds.","Components selected: Lead Scarab","Remaining slots drawn at random.","The gem carries:","  Radiant - kills pay x1.75 luminance","  Iron Affinity - 8% of kills leave iron to salvage","  Teeming - 67% more monsters","  Guarded - the boss has x1.83 health","  Swift - monsters move at x1.21 speed","  Shieldbreaker - monsters ignore 54% of your shield","Level 175, 3 entries."],"extra":[]}""",
        """{"v":1,"id":"1e960ea87fa64343","time":"2026-10-13T00:22:04.7703741Z","server":"DreamWeave","character":"Drakkon","fragment":"Raw Fragment","fragmentLevel":185,"tool":"Fragment Press","outcome":"Pressed. It holds.","componentsSeen":true,"components":["Lead Scarab"],"random":"Remaining slots drawn at random.","modifiers":[{"name":"Bounteous","description":"kills drop x1.51 loot","numbers":[{"value":1.51,"kind":"mult","text":"x1.51"}]},{"name":"Serpentine Affinity","description":"22% of kills leave serpentine to salvage","numbers":[{"value":22,"kind":"pct","text":"22%"}]},{"name":"Champions","description":"30% of the run\u0027s monsters are elites","numbers":[{"value":30,"kind":"pct","text":"30%"}]},{"name":"Guarded","description":"the boss has x2.18 health","numbers":[{"value":2.18,"kind":"mult","text":"x2.18"}]},{"name":"Enraged","description":"the boss attacks with \u002B54 damage rating, hitting harder","numbers":[{"value":54,"kind":"plus","text":"\u002B54"}]},{"name":"Precise","description":"monsters have \u002B19 critical strike rating, landing more critical hits","numbers":[{"value":19,"kind":"plus","text":"\u002B19"}]}],"resultLevel":175,"entries":3,"complete":true,"end":"done","raw":["Using the Raw Fragment (Level 185) with the Fragment Press","Pressed. It holds.","Components selected: Lead Scarab","Remaining slots drawn at random.","The gem carries:","  Bounteous - kills drop x1.51 loot","  Serpentine Affinity - 22% of kills leave serpentine to salvage","  Champions - 30% of the run\u0027s monsters are elites","  Guarded - the boss has x2.18 health","  Enraged - the boss attacks with \u002B54 damage rating, hitting harder","  Precise - monsters have \u002B19 critical strike rating, landing more critical hits","Level 175, 3 entries."],"extra":[]}""",
        """{"v":1,"id":"f0bf2c2811904c52","time":"2026-10-13T00:34:08.9352276Z","server":"DreamWeave","character":"Drakkon","fragment":"Raw Fragment","fragmentLevel":185,"tool":"Fragment Press","outcome":"Pressed. It holds.","componentsSeen":true,"components":["Lead Scarab"],"random":"Remaining slots drawn at random.","modifiers":[{"name":"Enlightened","description":"kills pay x1.43 experience","numbers":[{"value":1.43,"kind":"mult","text":"x1.43"}]},{"name":"Obsidian Affinity","description":"7% of kills leave obsidian to salvage","numbers":[{"value":7,"kind":"pct","text":"7%"}]},{"name":"Shieldbreaker","description":"monsters ignore 51% of your shield","numbers":[{"value":51,"kind":"pct","text":"51%"}]},{"name":"Savage","description":"monsters attack with \u002B33 damage rating, hitting harder","numbers":[{"value":33,"kind":"plus","text":"\u002B33"}]},{"name":"Stalwart","description":"monsters have \u002B20 damage resist rating, taking less damage from you","numbers":[{"value":20,"kind":"plus","text":"\u002B20"}]}],"resultLevel":175,"entries":3,"complete":true,"end":"done","raw":["Using the Raw Fragment (Level 185) with the Fragment Press","Pressed. It holds.","Components selected: Lead Scarab","Remaining slots drawn at random.","The gem carries:","  Enlightened - kills pay x1.43 experience","  Obsidian Affinity - 7% of kills leave obsidian to salvage","  Shieldbreaker - monsters ignore 51% of your shield","  Savage - monsters attack with \u002B33 damage rating, hitting harder","  Stalwart - monsters have \u002B20 damage resist rating, taking less damage from you","Level 175, 3 entries."],"extra":["[BANK] Debited 1 Trade Notes (250,000) (250,000 pyreals) from your bank."]}""",
    };

    internal static List<Press> Real() => RealLines.Select(l => PressStore.FromJson(l)!).ToList();

    private static int _n;

    /// <summary>A complete press with these components and modifiers ("Name" or "Name=x1.5 text").</summary>
    internal static Press P(string components, string modifiers, int level = 100, bool complete = true, int? result = null, int? entries = null)
    {
        var p = new Press
        {
            Id = "t" + (++_n),
            TimeUtc = new DateTime(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc).AddSeconds(_n),
            Character = "Tester",
            Server = "DreamWeave",
            Fragment = "Raw Fragment",
            FragmentLevel = level,
            Outcome = "Pressed. It holds.",
            ComponentsSeen = true,
            Components = PressParser.SplitComponents(components),
            Complete = complete,
            End = complete ? "done" : "timeout",
            ResultLevel = result,
            Entries = entries,
        };
        foreach (string m in modifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = m.Split('=', 2);
            string desc = parts.Length > 1 ? parts[1] : "something";
            p.Modifiers.Add(new GemModifier { Name = parts[0], Description = desc, Numbers = PressParser.Numbers(desc) });
        }
        return p;
    }

    private static List<Press> Many(int count, string components, string modifiers) =>
        Enumerable.Range(0, count).Select(_ => P(components, modifiers)).ToList();

    private static Odds? Odd(GroupReport? g, string mod) => g?.Find(mod);

    private static void RealPresses()
    {
        List<Press> presses = Real();
        Check.True(presses.All(p => p != null && p.Usable), "all 4 old lines load as complete presses");
        Check.Eq(string.Join(",", presses.Select(p => p.ComponentsText).Distinct()), "Lead Scarab", "all Lead Scarab");
        ThreadsReport r = PressInference.Build(presses);
        GroupReport? ls = r.Component("Lead Scarab");
        Check.NotNull(ls, "Lead Scarab row");
        if (ls == null) return;
        Check.Eq(ls.Presses, 4, "4 presses");

        Odds g = Odd(ls, "Guarded")!;
        Check.Eq((g.Hits, g.N), (3, 4), "Guarded 3/4");
        Check.Eq(PressInference.OddsText(g), "75% (3/4, 30-95%)", "Guarded text");
        Check.Eq(g.Values, "x1.83 to x2.18", "Guarded values");
        Odds rad = Odd(ls, "Radiant")!;
        Check.Eq(PressInference.OddsText(rad), "50% (2/4, 15-85%)", "Radiant 2/4");
        Check.Eq(PressInference.OddsText(Odd(ls, "Champions")!), "25% (1/4, 5-70%)", "Champions 1/4");

        Odds aff = Odd(ls, PressInference.AffinityGroup)!;
        Check.Eq((aff.Hits, aff.N), (4, 4), "Affinity group 4/4");
        Check.Eq(PressInference.OddsText(aff), "100% (4/4, 51-100%)", "Affinity text");
        Check.Eq(PressInference.SplitText(aff), "Amethyst 1, Iron 1, Obsidian 1, Serpentine 1", "split 1 each");
        Check.Eq(aff.Values, "7% to 22%", "affinity values across materials");
        Check.Null(Odd(ls, "Amethyst Affinity"), "materials are not rows of their own");
        Check.Eq(ls.Odds[0].Modifier, PressInference.AffinityGroup, "sorted by rate: Affinity first");
        Check.Eq(ls.Odds[1].Modifier, "Guarded", "then Guarded");

        Check.Eq(PressInference.OtherText(g, ls.OtherLabel), "without: no data", "nothing without Lead Scarab");
        Check.False(ls.Odds.Any(o => o.MoreLikely), "no 'more likely' with nothing to compare");
        Check.Eq(r.All?.Presses, 4, "All presses row");
        Check.Eq(r.Combinations.Count, 0, "no combinations");
        Check.Null(r.NoComponents, "no No components row");
        Check.Eq(PressInference.ModifiersText(ls), "5.5 modifiers per press (5-6)", "modifiers per press");
        Check.Eq(PressInference.ResultsText(ls), "L185 -> 175, 3 entries: 4", "results");
        Check.Eq(r.Modifier("Affinity")?.Overall.Hits, 4, "modifier view: Affinity 4/4");
        Check.Null(r.Modifier("Iron Affinity"), "no Iron Affinity modifier entry");
        Check.Eq(r.Modifiers.Count, 13, "13 modifiers (affinities as one)");
    }

    private static void WithWithout()
    {
        var presses = Many(5, "Red Taper", "Bounteous, Hardy");
        presses.Add(P("Red Taper", "Teeming, Hardy"));
        presses.AddRange(Many(3, "", "Hardy, Teeming"));
        presses.AddRange(Many(3, "", "Savage, Teeming"));
        ThreadsReport r = PressInference.Build(presses);
        GroupReport red = r.Component("Red Taper")!;
        Odds b = Odd(red, "Bounteous")!;
        Check.Eq((b.Hits, b.N, b.OtherHits, b.OtherN), (5, 6, 0, 6), "Bounteous 5/6 with, 0/6 without");
        Check.True(b.MoreLikely, "Bounteous more likely with Red Taper");
        Check.Eq(PressInference.LikelyText(b, red), "more likely with Red Taper", "wording");
        Check.Eq(PressInference.OtherText(b, red.OtherLabel), "without: 0% (0/6)", "without text");
        Odds h = Odd(red, "Hardy")!;
        Check.Eq((h.Hits, h.OtherHits), (6, 3), "Hardy 6/6 with, 3/6 without");
        Check.False(h.MoreLikely, "Hardy 6/6 vs 3/6: not clear (p ~ 0.09)");
        Check.Eq(red.Odds[0].Modifier, "Hardy", "sorted by rate: Hardy (100%) first");

        // Few presses: nothing claimed.
        r = PressInference.Build(Many(2, "Blue Taper", "Bounteous").Concat(Many(2, "", "Hardy")).ToList());
        Check.False(Odd(r.Component("Blue Taper"), "Bounteous")!.MoreLikely, "2/2 vs 0/2: not claimed");

        // 'always' only at 100% over 10 or more.
        r = PressInference.Build(Many(9, "Green Taper", "Teeming").Concat(Many(9, "", "Hardy")).ToList());
        GroupReport green = r.Component("Green Taper")!;
        Check.Eq(PressInference.LikelyText(Odd(green, "Teeming")!, green), "more likely with Green Taper", "9/9: more likely, not always");
        r = PressInference.Build(Many(10, "Green Taper", "Teeming").Concat(Many(10, "", "Hardy")).ToList());
        green = r.Component("Green Taper")!;
        Check.Eq(PressInference.LikelyText(Odd(green, "Teeming")!, green), "always with Green Taper so far", "10/10: always so far");

        // The No components row is compared with presses that had components.
        r = PressInference.Build(Many(6, "Red Taper", "Hardy").Concat(Many(6, "", "Teeming")).ToList());
        Odds t = Odd(r.NoComponents, "Teeming")!;
        Check.True(t.MoreLikely, "Teeming 6/6 plain vs 0/6 with components");
        Check.Eq(PressInference.LikelyText(t, r.NoComponents!), "more likely with no components", "no-components wording");
    }

    private static void Statistics()
    {
        var (lo, hi) = PressInference.Wilson(3, 4);
        Check.True(Math.Abs(lo - 0.3006) < 0.001 && Math.Abs(hi - 0.9544) < 0.001, $"3/4: 30.1-95.4% ({lo:0.0000}-{hi:0.0000})");
        (lo, hi) = PressInference.Wilson(0, 5);
        Check.True(lo == 0 && Math.Abs(hi - 0.4345) < 0.001, $"0/5: 0-43% ({hi:0.0000})");
        (lo, hi) = PressInference.Wilson(0, 0);
        Check.True(lo == 0 && hi == 1, "0/0: anything");
        Check.True(Math.Abs(PressInference.FisherGreater(3, 3, 0, 3) - 0.05) < 1e-9, "3/3 vs 0/3: p = 1/20");
        Check.True(Math.Abs(PressInference.FisherGreater(5, 6, 0, 6) - 7.0 / 924) < 1e-9, "5/6 vs 0/6: p = 7/924");
        Check.True(Math.Abs(PressInference.FisherGreater(0, 4, 4, 4) - 1) < 1e-9, "0 hits: p = 1");
        Check.Eq(PressInference.OddsText(new Odds { Hits = 30, N = 60 }), "50% (30/60)", "no interval from 50 presses");
        Check.Eq(PressInference.OddsText(new Odds { Hits = 0, N = 0 }), "no data", "empty");
    }

    private static void Combinations()
    {
        var presses = Many(3, "Red Taper, Blue Taper", "Bounteous, Savage");
        presses.AddRange(Many(2, "Red Taper", "Bounteous, Hardy"));
        presses.Add(P("Blue Taper", "Savage, Hardy"));
        presses.AddRange(Many(2, "", "Hardy, Teeming"));
        ThreadsReport r = PressInference.Build(presses);
        Check.Eq(r.Combinations.Count, 1, "one combination");
        GroupReport? combo = r.Combination("Blue Taper + Red Taper");
        Check.NotNull(combo, "named by its sorted components");
        if (combo == null) return;
        Check.Eq(combo.Presses, 3, "3 presses with exactly both");
        Check.Eq(combo.Members.Count, 2, "two members");
        Check.Eq(r.Component("Red Taper")!.Presses, 5, "component row counts every press with it");
        Check.Eq(r.Component("Blue Taper")!.Presses, 4, "Blue Taper 4");
        Odds s = Odd(combo, "Savage")!;
        Check.Eq((s.Hits, s.N, s.OtherHits, s.OtherN), (3, 3, 1, 5), "Savage 3/3 in the combination, 1/5 in other presses");
        Check.Eq(PressInference.OtherText(s, combo.OtherLabel), "other presses: 20% (1/5)", "comparison label");
        Check.Eq(PressInference.Build(Many(2, "Blue Taper, Red Taper", "Hardy").Concat(Many(1, "Red Taper, Blue Taper", "Hardy")).ToList())
                     .Combination("Blue Taper + Red Taper")?.Presses, 3, "order of components doesn't matter");
    }

    private static void NoComponentsAndAll()
    {
        var presses = Many(4, "Red Taper", "Bounteous, Hardy");
        presses.AddRange(Many(2, "", "Hardy, Teeming"));
        presses.Add(P("", "Teeming"));
        presses.Add(P("Red Taper", "Savage", complete: false));
        presses.Add(P("", ""));   // finished but no gem block: not usable
        ThreadsReport r = PressInference.Build(presses);
        Check.Eq((r.Total, r.Usable, r.Incomplete), (9, 7, 1), "totals");
        Check.Eq(r.Component("Red Taper")!.Presses, 4, "incomplete press left out");

        GroupReport? nc = r.NoComponents;
        Check.NotNull(nc, "No components row");
        if (nc == null) return;
        Check.Eq((nc.Presses, nc.Kind), (3, GroupKind.NoComponents), "3 plain presses");
        Odds t = Odd(nc, "Teeming")!;
        Check.Eq((t.Hits, t.N, t.OtherHits, t.OtherN), (3, 3, 0, 4), "Teeming 3/3 plain, 0/4 with components");
        Check.Eq(PressInference.OtherText(t, nc.OtherLabel), "with components: 0% (0/4)", "comparison label");
        Check.Eq(nc.Odds[0].Modifier, "Teeming", "most common plain draw first");

        GroupReport all = r.All!;
        Check.Eq((all.Presses, all.Kind), (7, GroupKind.All), "All presses: every usable press");
        Odds h = Odd(all, "Hardy")!;
        Check.Eq((h.Hits, h.N), (6, 7), "Hardy 6/7 overall");
        Check.False(h.Compared, "the baseline isn't compared");
        Check.Eq(PressInference.OtherText(h, all.OtherLabel), "", "no comparison text");
        Check.False(all.Odds.Any(o => o.MoreLikely), "never flagged");
    }

    private static void AffinityGroup()
    {
        Check.Eq(PressInference.Material("Green Garnet Affinity"), "Green Garnet", "two-word material");
        Check.Eq(PressInference.Key("Tourmaline Affinity"), PressInference.AffinityGroup, "grouped");
        Check.Null(PressInference.Material("Affinity"), "bare 'Affinity' is not a material");
        Check.Eq(PressInference.Key("Guarded"), "Guarded", "others keep their name");
        Check.Eq(PressInference.Display(PressInference.AffinityGroup), "Affinity (any)", "display name");

        var presses = new List<Press>
        {
            P("Red Taper", "Iron Affinity=8% of kills leave iron to salvage, Hardy"),
            P("Red Taper", "Iron Affinity=10% of kills leave iron to salvage, Teeming"),
            P("Red Taper", "Amethyst Affinity=12% of kills leave amethyst to salvage, Gold Affinity=5% of kills leave gold to salvage"),
            P("", "Hardy"),
        };
        ThreadsReport r = PressInference.Build(presses);
        Odds a = Odd(r.Component("Red Taper"), PressInference.AffinityGroup)!;
        Check.Eq((a.Hits, a.N, a.OtherHits, a.OtherN), (3, 3, 0, 1), "a press with two affinities counts once");
        Check.Eq(PressInference.SplitText(a), "Iron 2, Amethyst 1, Gold 1", "split counts both, most first");
        ModifierReport m = r.Modifier("Affinity")!;
        Check.True(m.IsGroup, "modifier view: the group");
        Check.Eq(m.Tag, "reward", "salvage: reward");
        Check.Eq(m.Values, "8% to 12%", "values over every material (first affinity per press)");
    }

    private static void ModifierView()
    {
        var presses = Many(3, "Red Taper", "Bounteous, Hardy");
        presses.AddRange(Many(2, "Blue Taper", "Hardy"));
        presses.Add(P("Blue Taper, Red Taper", "Bounteous"));
        presses.AddRange(Many(2, "", "Teeming, Bounteous"));
        ThreadsReport r = PressInference.Build(presses);
        ModifierReport b = r.Modifier("Bounteous")!;
        Check.Eq((b.Overall.Hits, b.Overall.N), (6, 8), "overall 6/8");
        Check.Eq((b.NoComponents?.Hits, b.NoComponents?.N, b.NoComponents?.OtherHits, b.NoComponents?.OtherN), ((int?)2, (int?)2, (int?)4, (int?)6), "no components 2/2 vs 4/6");
        Check.Eq(string.Join(", ", b.ByGroup.Select(x => $"{x.Group.Name} {x.Odds.Hits}/{x.Odds.N}")),
                 "Red Taper 4/4, Blue Taper + Red Taper 1/1, Blue Taper 1/3", "each component and combination, likeliest first");
        Odds teemingBlue = r.Modifier("Teeming")!.ByGroup.First(x => x.Group.Name == "Blue Taper").Odds;
        Check.Eq((teemingBlue.Hits, teemingBlue.N, teemingBlue.OtherHits, teemingBlue.OtherN), (0, 3, 2, 5), "0% rows are kept, with their comparison");
    }

    private static void ReportDetails()
    {
        var presses = new List<Press>
        {
            P("Red Taper", "Bounteous=kills drop x1.38 loot, Hardy=monsters have x1.5 health", level: 185, result: 175, entries: 3),
            P("Red Taper", "Bounteous=kills drop x1.52 loot", level: 185, result: 175, entries: 3),
            P("Red Taper", "Bounteous=kills drop x1.20 loot", level: 120, result: 110, entries: 2),
            P("Red Taper", "Hardy=monsters have x2 health", complete: false),
            P("", "Hardy=monsters have x1.81 health, Teeming=75% more monsters", level: 120),
            P("", "Teeming=60% more monsters", level: 120),
        };
        ThreadsReport r = PressInference.Build(presses);
        GroupReport red = r.Component("Red Taper")!;
        Check.Eq(PressInference.ResultsText(red), "L185 -> 175, 3 entries: 2; L120 -> 110, 2 entries: 1", "results, most common first");
        Check.Eq(PressInference.ModifiersText(red), "1.33 modifiers per press (1-2)", "modifiers per press");
        Check.Eq(Odd(red, "Bounteous")!.Values, "x1.2 to x1.52", "values with the component");

        ModifierReport b = r.Modifier("Bounteous")!;
        Check.Eq(b.Values, "x1.2 to x1.52", "value range");
        Check.Eq(b.ByLevel.Count, 2, "two fragment levels");
        Check.Eq(b.ByLevel[0].Level, 120, "lowest level first");
        Check.Eq(b.ByLevel[1].Values, "x1.38 to x1.52", "level 185 range");
        Check.Eq(b.Tag, "reward", "loot is a reward");
        Check.Eq(r.Modifier("Hardy")!.Tag, "harder", "monster health is harder");
        Check.Eq(PressInference.Tag("something odd"), "", "no tag when the text doesn't say");

        ThreadsReport empty = PressInference.Build(new List<Press>());
        Check.True(empty.Usable == 0 && empty.All == null && empty.Modifiers.Count == 0, "empty input");
    }

    private static void SampleReport()
    {
        Press p = new PressParser().Feed(string.Join("\n", ParserTests.Sample), 1)![0];
        ThreadsReport r = PressInference.Build(new[] { p });
        Check.Eq(r.Component("Red Taper")?.Presses, 1, "Red Taper counted");
        Check.Eq(r.Modifiers.Count, 6, "six modifiers in the report");
        Check.Eq(r.Modifier("Affinity")?.Tag, "reward", "salvage is a reward");
        Check.Eq(PressInference.SplitText(r.Modifier("Affinity")!.Overall), "Tourmaline 1", "Tourmaline");
        Check.Eq(r.Modifier("Enraged")?.Tag, "harder", "boss damage is harder");
        Check.Eq(PressInference.OddsText(r.Component("Red Taper")!.Find("Hardy")!), "100% (1/1, 21-100%)", "one press: wide range");
    }
}
