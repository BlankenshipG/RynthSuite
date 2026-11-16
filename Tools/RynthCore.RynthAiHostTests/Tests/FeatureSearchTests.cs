using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.Features;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Find a Feature search (Features/FeatureSearch.cs) on a small made-up catalog: every word must
// match somewhere, case and light stemming don't matter, the start of a word counts, the name ranks
// over the keywords over the summary and where, and no words lists everything by group.
internal static class FeatureSearchTests
{
    public static void Register(Runner r)
    {
        r.Add("feature search: every word must match (AND), in any order and case", AndOfWords);
        r.Add("feature search: plural s, -ing and -ed are stemmed", Stemming);
        r.Add("feature search: the start of a word matches (vend finds Vendors)", Prefix);
        r.Add("feature search: name over keywords over summary and where", Ranking);
        r.Add("feature search: no words lists every feature by group", EmptyQuery);
        r.Add("feature search: the count line", CountLine);
    }

    private static FeatureEntry E(string id, string name, string group, string summary = "", string[]? where = null,
        string command = "", string open = "", string[]? keywords = null, string plugin = "RynthAi") => new()
    {
        Id = id, Name = name, Plugin = plugin, Group = group, Summary = summary,
        Where = (where ?? new[] { "RynthAi window" }).ToList(), Command = command, Open = open,
        Keywords = (keywords ?? Array.Empty<string>()).ToList(),
    };

    private static readonly List<FeatureEntry> Catalog = new()
    {
        E("ai.keep-hunting-after-death", "Keep Hunting After Death", "Hunting",
            "After a death the Outdoor Hunt starts again once you're back up.",
            new[] { "Outdoor Hunt window > Keep Hunting After Death" }, "/ra hunt death [on|off]", "/ra hunt panel",
            new[] { "die", "respawn", "lifestone" }),
        E("ai.outdoor-hunt", "Outdoor Hunt", "Hunting", "Walks from monster to monster outdoors.",
            new[] { "RynthAi window > Hunt" }, "/ra hunt [start|stop]", "/ra hunt panel", new[] { "farm", "grind" }),
        E("ai.recall-on-death", "Recall On Death", "Death", "Recalls after you respawn, then runs back to your corpse.",
            new[] { "Settings > Misc > Recall On Death" }, keywords: new[] { "portal" }),
        E("ai.auto-vendor", "Auto Vendor", "Vendors", "Sells and buys when a vendor opens.",
            new[] { "Settings > Vendoring > Auto Vendor" }, "/ub autovendor", keywords: new[] { "merchant", "sell" }),
        E("ai.looting", "Looting", "Looting", "Opens corpses and takes what the loot profile keeps.",
            new[] { "RynthAi window > Looting" }, keywords: new[] { "pickup", "corpse" }),
        E("ai.buff-items", "Buff Items", "Buffs", "Uses gems and rations that buff you.",
            new[] { "Spells window > Buff Items" }, "/ra buffitems list", keywords: new[] { "gem" }),
        // "death" only in where and summary (never in the name or keywords): ranks under the name hits.
        E("ai.death-window", "Corpse Finder", "Death", "Finds your corpse after a death.",
            new[] { "Death window > Corpse Finder" }),
        // "death" only in a keyword.
        E("ai.patrol", "Dungeon Patrol", "Navigation", "Walks every room of a dungeon.",
            keywords: new[] { "death", "sweep" }),
        E("oracle.quests", "Quests", "Quests", "Your quest timers.", new[] { "RynthCore bar > Oracle > Quests" },
            "/ro quests", plugin: "RynthOracle"),
    };

    private static List<string> Ids(string query) => FeatureSearch.Search(Catalog, query).Select(h => h.Entry.Id).ToList();

    private static void AndOfWords()
    {
        List<string> both = Ids("death hunt");
        Check.True(both.Contains("ai.keep-hunting-after-death"), "both words: " + string.Join(",", both));
        Check.False(both.Contains("ai.recall-on-death"), "death but no hunt: out");
        Check.False(both.Contains("ai.outdoor-hunt"), "hunt but no death: out");
        Check.Eq(string.Join(",", Ids("HUNT Death")), string.Join(",", both), "order and case don't matter");
        Check.Eq(Ids("death zzzz").Count, 0, "a word that matches nothing: no results");
        Check.True(Ids("oracle quests").SequenceEqual(new[] { "oracle.quests" }), "the plugin's name counts as a field");
        Check.True(Ids("/ra buffitems").Contains("ai.buff-items"), "a command's words, slash and all");
        Check.True(Ids("vendoring").Contains("ai.auto-vendor"), "a where path's words count");
    }

    private static void Stemming()
    {
        Check.Eq(FeatureSearch.Stem("vendors"), "vendor", "plural s");
        Check.Eq(FeatureSearch.Stem("looting"), "loot", "-ing");
        Check.Eq(FeatureSearch.Stem("looted"), "loot", "-ed");
        Check.Eq(FeatureSearch.Stem("stopped"), "stop", "-ed with a doubled letter");
        Check.Eq(FeatureSearch.Stem("buffs"), "buff", "buffs");
        Check.Eq(FeatureSearch.Stem("pass"), "pass", "ss isn't a plural");
        Check.Eq(FeatureSearch.Stem("status"), "status", "-us isn't a plural");
        Check.True(Ids("deaths").Contains("ai.recall-on-death"), "deaths finds Death");
        Check.True(Ids("hunting").Contains("ai.outdoor-hunt"), "hunting finds Hunt");
        Check.True(Ids("looted corpses").Contains("ai.looting"), "looted corpses finds Looting (corpse)");
        Check.True(Ids("gems").Contains("ai.buff-items"), "gems finds the keyword gem");
    }

    private static void Prefix()
    {
        Check.True(Ids("vend").Contains("ai.auto-vendor"), "vend finds Vendors / Vendor");
        Check.True(Ids("merch").Contains("ai.auto-vendor"), "the start of a keyword");
        Check.True(Ids("resp").Contains("ai.keep-hunting-after-death"), "resp finds respawn");
        Check.False(Ids("endor").Contains("ai.auto-vendor"), "the middle of a word doesn't count");
        Check.Eq(FeatureSearch.WordMatch("vend", "vendors"), 1, "a start is a partial match");
        Check.Eq(FeatureSearch.WordMatch("vendors", "vendor"), 2, "the same stem is a whole match");
    }

    private static void Ranking()
    {
        List<FeatureHit> hits = FeatureSearch.Search(Catalog, "death");
        List<string> ids = hits.Select(h => h.Entry.Id).ToList();
        int name1 = ids.IndexOf("ai.keep-hunting-after-death"), name2 = ids.IndexOf("ai.recall-on-death");
        int keyword = ids.IndexOf("ai.patrol"), whereOnly = ids.IndexOf("ai.death-window");
        Check.True(name1 >= 0 && name2 >= 0 && keyword >= 0 && whereOnly >= 0, "all four found: " + string.Join(",", ids));
        Check.True(Math.Max(name1, name2) < keyword, "the name hits before the keyword hit: " + string.Join(",", ids));
        Check.True(keyword < whereOnly, "the keyword hit before the summary / where hit: " + string.Join(",", ids));
        Check.True(hits.Zip(hits.Skip(1)).All(p => p.First.Score >= p.Second.Score), "best first");
        // Equal scores: by name.
        int a = ids.IndexOf("ai.keep-hunting-after-death"), b = ids.IndexOf("ai.recall-on-death");
        Check.True(a < b, "a tie goes by name (Keep before Recall)");
        Check.True(FeatureSearch.Score(Catalog[1], new[] { "hunt" }) > FeatureSearch.Score(Catalog[1], new[] { "hun" }),
            "a whole word scores over its start");
    }

    private static void EmptyQuery()
    {
        foreach (string q in new[] { "", "   ", "--- ..." })
        {
            List<FeatureHit> all = FeatureSearch.Search(Catalog, q);
            Check.Eq(all.Count, Catalog.Count, $"'{q}': every feature");
        }
        List<FeatureGroup> groups = FeatureSearch.Grouped(Catalog);
        Check.Eq(groups.Sum(g => g.Entries.Count), Catalog.Count, "every feature in a group");
        Check.Eq(groups.Count, Catalog.Select(e => e.Group).Distinct().Count(), "one group per group name");
        var order = groups.Select(g => g.Name).ToList();
        Check.True(order.IndexOf("Buffs") < order.IndexOf("Hunting") && order.IndexOf("Hunting") < order.IndexOf("Looting"),
            "the schema's group order: " + string.Join(",", order));
        FeatureGroup hunting = groups.Single(g => g.Name == "Hunting");
        Check.True(hunting.Entries.Select(e => e.Name).SequenceEqual(new[] { "Keep Hunting After Death", "Outdoor Hunt" }), "by name inside a group");
        List<string> flat = FeatureSearch.Search(Catalog, "").Select(h => h.Entry.Id).ToList();
        Check.True(flat.SequenceEqual(groups.SelectMany(g => g.Entries).Select(e => e.Id)), "no words: the groups' order");
        Check.Eq(FeatureSearch.Words("/ra hunt, death!").Count, 3, "words without punctuation");
    }

    private static void CountLine()
    {
        Check.Eq(FeatureSearch.CountLine(12, "death"), "12 features match 'death'", "many");
        Check.Eq(FeatureSearch.CountLine(1, " death "), "1 feature matches 'death'", "one");
        Check.Eq(FeatureSearch.CountLine(0, "xyz"), "No feature matches 'xyz'", "none");
        Check.Eq(FeatureSearch.CountLine(40, ""), "40 features", "no words");
    }
}
