using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.RynthOracle.Views;

namespace RynthOracle.Tests
{
    // The Infinite Leaftide wiki quest guide (2026-11-01): RynthILT reads leaftidewiki.com's quest pages
    // with WikiQuestParser and saves them as a QuestGuide; Oracle's Quests tab shows the steps. The two
    // page shapes the player sent: numbered steps with their stamps under them (Mistreated Virindi), and
    // paragraphs with the stamps listed apart ("Pistol" Pete).
    internal static partial class Program
    {
        private static string TestText(string name)
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("missing test data " + name);
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        private static void WikiGuide()
        {
            // ── Mistreated Virindi: numbered steps, "#*" stamps ───────────────
            GuideQuest v = WikiQuestParser.Parse("Mistreated Virindi", TestText("TestWiki.mistreated-virindi.txt"), 1234);
            Check(v.Title == "Mistreated Virindi" && v.Rev == 1234, "wiki: title and revision kept");
            Check(v.Url == "https://www.leaftidewiki.com/Mistreated_Virindi", "wiki: the page's address (" + v.Url + ")");
            Check(v.Level == "180+" && v.RecommendedLevel == "275+" && v.Type == "Solo/Fellowship", "wiki: the summary box's levels and type");
            Check(v.StartsWith == "Mistreated Virindi" && v.StartsAt == "Zaikhal", "wiki: starts with / at, links as their words");
            Check(v.Repeat == "20 Hours" && v.RepeatSec == 20 * 3600 && v.IsDaily && !v.IsWeekly && !v.IsOneTime, "wiki: 20 Hours is a daily");
            Check(v.Qb == 0, "wiki: an empty QB is 0");
            Check(v.Categories.SequenceEqual(new[] { "Enlightened Coin Quests" }), "wiki: categories");
            Check(v.Overview.StartsWith("Quest to upgrade Imbued Asteliary items") && v.Overview.Contains("Simulacra Infiltrators"), "wiki: overview cleaned");
            Check(v.Steps.Count == 10, $"wiki: ten numbered steps ({v.Steps.Count})");
            Check(v.Steps[0].Text == "Talk to Mistreated Virindi in Zaikhal to start the quest." && v.Steps[0].Stamps.SequenceEqual(new[] { "Sneaky_Virindi" }),
                "wiki: step 1 text, its #* stamp");
            Check(v.Steps[0].Notes.Count == 0, "wiki: a stamp-only sub-line is no note");
            Check(v.Steps[1].Coords.SequenceEqual(new[] { "28.5S, 90.9W" }) && v.Steps[1].Notes.Count == 1 && v.Steps[1].Notes[0].StartsWith("Run from Mount Lethe"),
                "wiki: step 2 coordinates and its tip");
            Check(v.Steps[2].Items.SequenceEqual(new[] { "Shiny Red Stone" }) && v.Steps[2].Text.Contains("Tortured Raveners") && v.Steps[2].Text.EndsWith("for a Shiny Red Stone."),
                "wiki: step 3 item, plural link kept whole");
            Check(v.Steps[9].Stamps.Count == 0 && v.Steps[9].Items.Count == 4, "wiki: the upgrade step names four items, no stamp");
            Check(v.AllStamps().SequenceEqual(new[] { "Sneaky_Virindi", "Shiny_Red_Stone_Found", "Guardian_of_the_Asteliary_Message_Shard",
                "Powerful_Blood_Obtained", "Blind_Revenge", "Shiny_Red_Stone_Turnin" }), "wiki: every stamp in order");
            Check(v.ExtraStamps.Count == 0, "wiki: no stamps left over");
            Check(v.Loot.SequenceEqual(new[] { "Shiny Red Stone", "Guardian of the Asteliary's Message Shard", "Vial of The Architect's Blood" }),
                "wiki: loot = the items killed or looted for, not the ones handed in or upgraded (" + string.Join(" | ", v.Loot) + ")");
            Check(v.Rewards.Contains("For Vial of The Architect's Blood:") && v.Rewards.Contains("Enlightened Coin (x80)") && v.Rewards.Contains("Mythical Key (x5)"),
                "wiki: both reward tables (" + string.Join(" | ", v.Rewards) + ")");
            Check(!v.Rewards.Any(r => r.Contains("{{") || r.Contains("[[")), "wiki: rewards have no markup left");

            // ── "Pistol" Pete: paragraphs, stamps in QB Stamps ───────────────
            GuideQuest p = WikiQuestParser.Parse("\"Pistol\" Pete Trophy Hunts", TestText("TestWiki.pistol-pete.txt"));
            Check(p.StartsWith == "\"Pistol\" Pete", "pete: the NPC without its picture (" + p.StartsWith + ")");
            Check(p.RepeatSec == 7 * 86400 && p.IsWeekly && p.Qb == 15, "pete: weekly, QB 15");
            Check(p.Overview.StartsWith("\"Pistol\" Pete in Candeth Keep"), "pete: the Overview wins over the Discord Description");
            Check(p.Steps.Count == 5, $"pete: one step per paragraph ({p.Steps.Count})");
            Check(p.Steps[1].Coords.SequenceEqual(new[] { "85.4N, 6.4W" }) && p.Steps[2].Coords.SequenceEqual(new[] { "62.8S, 97.9E" }),
                "pete: '85.4N 6.4W' and '62.8S, 97.9E'");
            Check(p.Steps[3].Coords.SequenceEqual(new[] { "62.7S, 66.6W", "85.8S, 78.8W" }), "pete: '62.7s 66.6W' and '85.8s,78.8w' in one step");
            Check(p.Steps[1].Stamps.SequenceEqual(new[] { "NephriteTooth" }), "pete: NephriteTooth goes with the Nephrite Golem");
            Check(p.Steps[2].Stamps.SequenceEqual(new[] { "WinstonsLeg" }), "pete: WinstonsLeg goes with Winston");
            Check(p.Steps[3].Stamps.SequenceEqual(new[] { "FrostWyrmScale" }), "pete: FrostWyrmScale goes with the Frost Wyrm");
            Check(p.ExtraStamps.SequenceEqual(new[] { "PeteTrophiesDone" }), "pete: a stamp two steps could own is kept apart");
            Check(p.AllStamps().Count() == 4, "pete: four stamps in all");
            Check(p.Loot.SequenceEqual(new[] { "Nephrite Tooth", "Winston's Leg", "Frozen Wyrm Scale" }), "pete: the three trophies are the loot");
            Check(p.Rewards.Contains("Enlightened Coin (x50)") && p.Rewards.Contains("Luminance: 2,000,000"), "pete: rewards with luminance");

            // ── Smaller pieces ───────────────────────────────────────────────
            Check(WikiQuestParser.RepeatSeconds("") == 0 && WikiQuestParser.RepeatSeconds("None") == 0 && WikiQuestParser.RepeatSeconds("Once") == 0, "repeat: none");
            Check(WikiQuestParser.RepeatSeconds("20h") == 72000 && WikiQuestParser.RepeatSeconds("3d 12h") == 3 * 86400 + 12 * 3600, "repeat: short forms");
            Check(WikiQuestParser.RepeatSeconds("1 Week") == 7 * 86400 && WikiQuestParser.RepeatSeconds("Daily") == 86400, "repeat: week, daily");
            Check(WikiQuestParser.RepeatSeconds("when the moon is full") == -1, "repeat: not understood is -1");
            Check(WikiQuestParser.Clean("Kill [[Tortured Ravener]]s and '''[[Damaged Doll|dolls]]''' {{Itemlink|X}}<br/>{{SomeBox|a=b}}") == "Kill Tortured Raveners and dolls X",
                "clean: links, bold, item templates, tags and other templates");
            Check(GuideStamp.Key("Shiny_Red_Stone_Found") == GuideStamp.Key("shinyredstonefound") && GuideStamp.Key("Shiny Red Stone Found") == "shinyredstonefound",
                "stamp: one key for every spelling");
            Check(GuideStamp.FromChat("You've stamped Sneaky_Virindi!") == "Sneaky_Virindi" && GuideStamp.FromChat("You say, \"hi\"") == null, "stamp: the server's line");
            Check(!WikiQuestParser.IsQuestPage("Just a page") && WikiQuestParser.IsQuestPage("{{Quest Summary|Level=1}}"), "wiki: only pages with the summary box are quests");

            // ── The saved guide round-trips ──────────────────────────────────
            var g = new QuestGuide { Source = "api", UpdatedUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc) };
            g.Quests.Add(v);
            g.Quests.Add(p);
            QuestGuide back = QuestGuide.Parse(g.ToJson());
            Check(back.Source == "api" && back.UpdatedUtc == g.UpdatedUtc && back.Quests.Count == 2, "guide json: header and count");
            GuideQuest bv = back.Find("mistreated virindi")!;
            Check(bv != null && bv.Steps.Count == 10 && bv.Steps[1].Coords[0] == "28.5S, 90.9W" && bv.Rev == 1234 && bv.RepeatSec == 72000,
                "guide json: steps, coordinates, revision and timer come back");
            Check(bv!.AllStamps().SequenceEqual(v.AllStamps()) && bv.Loot.SequenceEqual(v.Loot) && bv.Rewards.SequenceEqual(v.Rewards), "guide json: stamps, loot, rewards");
            Check(back.Find("\"Pistol\" Pete Trophy Hunts")!.ExtraStamps.SequenceEqual(new[] { "PeteTrophiesDone" }), "guide json: extra stamps");
            Check(QuestGuide.Parse("not json").Quests.Count == 0 && QuestGuide.Parse("").Quests.Count == 0, "guide json: junk reads as empty");

            // ── Special:Export ───────────────────────────────────────────────
            string xml = "<mediawiki xmlns=\"http://www.mediawiki.org/xml/export-0.11/\"><siteinfo><sitename>Leaftide</sitename></siteinfo>"
                + "<page><title>Mistreated Virindi</title><ns>0</ns><id>77</id><revision><id>901</id><contributor><username>A</username><id>5</id></contributor>"
                + "<text bytes=\"10\">{{Quest Summary|Level=1}}</text></revision></page>"
                + "<page><title>Category:Quest Index</title><ns>14</ns><id>78</id><revision><id>902</id><text>x</text></revision></page>"
                + "<page><title>Another</title><ns>0</ns><id>79</id><revision><id>903</id><text>y</text></revision></page></mediawiki>";
            var pages = WikiQuestParser.ReadExport(new StringReader(xml));
            Check(pages.Count == 2 && pages[0].Title == "Mistreated Virindi" && pages[0].Rev == 901 && pages[0].Text == "{{Quest Summary|Level=1}}"
                  && pages[1].Title == "Another" && pages[1].Rev == 903,
                "export: main-namespace pages with their revision ids and text (" + string.Join(", ", pages.Select(x => x.Title + "@" + x.Rev)) + ")");

            WikiGuideView();
        }

        /// <summary>The Quests tab's guide view: tabs, the two panes, stamps ticked, Run this quest and the loot map.</summary>
        private static void WikiGuideView()
        {
            GuideQuest v = WikiQuestParser.Parse("Mistreated Virindi", TestText("TestWiki.mistreated-virindi.txt"), 1);
            GuideQuest p = WikiQuestParser.Parse("\"Pistol\" Pete Trophy Hunts", TestText("TestWiki.pistol-pete.txt"), 2);
            var g = new QuestGuide { Source = "api", UpdatedUtc = DateTime.UtcNow };
            g.Quests.Add(p);
            g.Quests.Add(v);
            string file = Path.Combine(Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N") + "-wiki-quests.json");
            File.WriteAllText(file, g.ToJson());

            OracleContext c = On("ilt");
            var nav = new List<string>();
            var ai = new List<string>();
            var said = new List<string>();
            c.Runner = new QuestRunner(l => { nav.Add(l); return true; }, l => { ai.Add(l); return true; }, said.Add);
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            Check(view.GuideTabs().Count == 7, "guide: seven tabs on Infinite Leaftide");
            c.Settings.QuestGuideView = true;
            c.Settings.TabName = OracleView.TabQuests;

            string body = Frame(host, main, view, "guide, no RynthILT");
            Check(body.Contains("RynthILT isn't serving them"), "guide: says when RynthILT has no guide");
            c.Guide.Take("{\"path\":" + System.Text.Json.JsonSerializer.Serialize(file) + ",\"revision\":4,\"count\":2,\"login\":true,\"running\":false,\"status\":\"2 quests.\"}");
            Check(c.Guide.Available && c.Guide.Guide.Quests.Count == 2, "guide client: reads the file the info names");
            int gr = c.Guide.Revision;
            c.Guide.Take("{\"path\":" + System.Text.Json.JsonSerializer.Serialize(file) + ",\"revision\":4,\"count\":2,\"login\":true,\"running\":false,\"status\":\"2 quests.\"}");
            Check(c.Guide.Revision == gr, "guide client: the same info again changes nothing");

            view.ShowGuide(GuideTab.Wiki, "w:Mistreated Virindi");
            body = Frame(host, main, view, "guide, ILT Wiki", out int ops);
            Check(body.Contains("###g.r.w:Mistreated Virindi") && body.Contains("###g.r.w:\"Pistol\" Pete Trophy Hunts"), "guide: both wiki quests listed");
            Check(body.Contains("0 / 6") && body.Contains("0 / 4"), "guide: stamps held of all, per quest");
            Check(body.Contains("Daily (every 20h 00m)") && body.Contains("Run this quest") && body.Contains("Loot map (3)"), "guide: timer, Run, Loot map");
            Check(body.Contains("Starts with Mistreated Virindi at Zaikhal") && body.Contains("###g.start.go"), "guide: where it starts, with Go");
            Check(body.Contains("2. Run to the Hidden Infiltrator Keep") && body.Contains("Go 28.5S, 90.9W"), "guide: numbered steps with their Go buttons");
            Check(body.Contains("[ ] Sneaky_Virindi - not stamped yet") && !body.Contains("You've stamped"), "guide: stamps not held yet, said so");
            Check(body.Contains("0 of 6 stamps done this run. Still to get:") && body.Contains("Sneaky_Virindi (step 1)") && body.Contains("Blind_Revenge (step 8)"),
                "guide: the stamps still to get, each with its step");
            Check(body.Contains("[>] 1. Talk to Mistreated Virindi"), "guide: the next step is marked");
            Check(ops < 1500, $"guide: within the engine's op limit ({ops})");

            // A stamp from chat ticks it at once.
            c.SessionStamps.Add(GuideStamp.Key("Sneaky_Virindi"));
            c.SessionStampRevision++;
            body = Frame(host, main, view, "guide, a stamp");
            Check(body.Contains("[x] You've stamped Sneaky_Virindi!") && body.Contains("1 / 6"), "guide: a held stamp is ticked and counted");
            Check(body.Contains("[x] 1. Talk to") && body.Contains("[>] 2. Run to the Hidden Infiltrator Keep"), "guide: step 1 done, step 2 next");
            Check(body.Contains("1 of 6 stamps done this run") && !body.Contains("Sneaky_Virindi (step 1)"), "guide: the done stamp leaves the still-to-get list");

            // A later step stamped: the stampless step before it counts as passed ([~]).
            c.SessionStamps.Add(GuideStamp.Key("Shiny_Red_Stone_Found"));
            c.SessionStampRevision++;
            body = Frame(host, main, view, "guide, step 3 stamped");
            Check(body.Contains("[~] 2. Run to the Hidden Infiltrator Keep") && body.Contains("[x] 3. Inside the dungeon") && body.Contains("[>] 4."),
                "guide: a step with no stamp before a done one is passed; the next is step 4");

            // A daily's stamp from yesterday (timer up) is "ready again", not done; inside its timer it is done.
            long ago = DateTimeOffset.UtcNow.AddHours(-30).ToUnixTimeSeconds(), recent = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
            c.Flags.OnChatLine($"Blind_Revenge - 3 solves ({ago})\"Blind revenge\" -1 72000", c.NowMs);
            c.Flags.OnChatLine($"Powerful_Blood_Obtained - 3 solves ({recent})\"Blood\" -1 72000", c.NowMs);
            c.Flags.Tick(c.NowMs + 10_000);
            body = Frame(host, main, view, "guide, timers");
            Check(body.Contains("Blind_Revenge - done on an earlier run; ready again") && body.Contains("Blind_Revenge (step 8, done before: ready again)"),
                "guide: a daily stamped 30 h ago is ready again, not done ({c.Flags.Flags.Count} flags)");
            Check(body.Contains("[x] You've stamped Powerful_Blood_Obtained!"), "guide: one stamped 2 h ago (20 h timer) is done");
            c.SessionStamps.Remove(GuideStamp.Key("Shiny_Red_Stone_Found"));   // the run below starts before step 3
            c.SessionStampRevision++;

            // Tabs.
            view.ShowGuide(GuideTab.Dailies);
            body = Frame(host, main, view, "guide, dailies");
            Check(body.Contains("###g.r.w:Mistreated Virindi") && !body.Contains("###g.r.w:\"Pistol\""), "guide: Dailies has the 20-hour quest only");
            view.ShowGuide(GuideTab.Qb);
            body = Frame(host, main, view, "guide, qb");
            Check(!body.Contains("###g.r.w:Mistreated Virindi") && body.Contains("###g.r.w:\"Pistol\""), "guide: QB has the QB 15 quest only");
            view.ShowGuide(GuideTab.Stamps);
            body = Frame(host, main, view, "guide, stamps", out int sops);
            Check(body.Contains("###g.r.a:" + DynamicStampList.Areas[0]) && sops < 1500, $"guide: Stamps lists the areas ({sops} ops)");
            view.ShowGuide(GuideTab.All);
            body = Frame(host, main, view, "guide, all", out int aops);
            Check(body.Contains("###g.r.w:Mistreated Virindi") && body.Contains("###g.page.next") && aops < 1500, $"guide: All pages the wiki and the quest list together ({aops} ops)");

            // Run this quest: the first step not stamped with a place, the loot onto RynthAi's list.
            view.ShowGuide(GuideTab.Wiki, "w:Mistreated Virindi");
            Frame(host, main, view, "guide, before run");
            Click(host, main, "g.run");
            QuestRunner run = c.Runner!;
            Check(run.Active && run.Step == 1 && nav.Count == 1 && nav[0] == "go 28.5S, 90.9W", "run: step 1 is stamped, so RynthNav goes to step 2's place: " + string.Join(" | ", nav));
            Check(ai.Count == 4 && ai[0] == "add Shiny Red Stone" && ai[2] == "add Vial of The Architect's Blood", "run: the loot goes to RynthAi: " + string.Join(" | ", ai));
            Check(ai[3].StartsWith("profile Mistreated Virindi|Shiny Red Stone|") && ai[3].EndsWith("|Vial of The Architect's Blood"),
                "run: RynthAi uses the quest's own loot profile meanwhile: " + ai[3]);
            body = Frame(host, main, view, "guide, running");
            Check(body.Contains("###g.stop") && body.Contains("###g.next") && body.Contains("going to 28.5S, 90.9W"), "run: Stop / Next and what it is doing");
            run.OnChat("[RynthNav] goto: arrived at 28.5S, 90.9W");
            Check(!run.Travelling && said.Exists(l => l.StartsWith("Step 2/10: Run to the Hidden Infiltrator Keep")), "run: arrival prints the step");
            Check(ai[^1] == "dungeon enter", "run: the step goes into the Keep: RynthAi takes the portal there (2026-11-07)");
            run.Next();
            // A step without coordinates goes to what it names (2026-11-07); RynthNav doesn't know these two.
            Check(run.Step == 2 && nav.Count == 2 && nav[1] == "go Tortured Raveners || Damaged Dolls",
                "run: Next, a step without coordinates goes to the names in it: " + string.Join(" | ", nav));
            run.OnChat("[RynthNav] none of these is a place on the map: Tortured Raveners , Damaged Dolls");
            Check(!run.Travelling && said.Exists(l => l.StartsWith("Step 3/10: Inside the dungeon")) && run.Note.Contains("doesn't know where Tortured Raveners"),
                "run: names RynthNav doesn't know: the step is shown and waits (" + run.Note + ")");
            Check(run.LastNpc == "", "run: a kill step talks to nobody");
            run.OnChat("You've stamped Shiny_Red_Stone_Found!");
            Check(run.Step == 3, "run: its stamp moves it on");
            run.OnChat("You've stamped Someone_Elses_Stamp!");
            Check(run.Step == 3, "run: another stamp doesn't");
            run.Stop();
            Check(!run.Active && ai.Count == 9 && ai[5] == "remove Shiny Red Stone" && ai[8] == "profile off",
                "run: Stop takes the loot off RynthAi's list and goes back to the loot profile before: " + string.Join(" | ", ai));
            Check(run.BuildProfile(v) && ai[^1].StartsWith("build Mistreated Virindi|Shiny Red Stone|"), "Loot profile: RynthAi writes the quest's profile");

            // Pete: the paragraph quest goes to each of the Frost Wyrm step's two places.
            nav.Clear();
            c.SessionStamps.Add(GuideStamp.Key("NephriteTooth"));
            c.SessionStamps.Add(GuideStamp.Key("WinstonsLeg"));
            run.Start(p, s => c.SessionStamps.Contains(GuideStamp.Key(s)), loot: false);
            Check(run.Step == 0 && nav.Count == 1 && nav[0] == "go \"Pistol\" Pete || Pistol Pete || Candeth Keep",
                "pete: a first step without coordinates goes to the quest's person, then where it starts: " + string.Join(" | ", nav));
            run.Next();
            Check(run.Step == 3 && nav[^1] == "go 62.7S, 66.6W", "pete: the stamped steps are skipped");
            run.Next();
            Check(run.Step == 3 && run.Place == 1 && nav[^1] == "go 85.8S, 78.8W", "pete: Next goes to the step's second place");
            run.OnChat("You've stamped FrostWyrmScale!");
            Check(run.Step == 4 && nav[^1] == "go \"Pistol\" Pete || Pistol Pete", "pete: the last step goes to Pete: " + string.Join(" | ", nav));
            int aiBefore = ai.Count;
            run.OnChat("[RynthNav] goto: arrived");
            Check(said.Exists(l => l.StartsWith("Step 5/5: Return to Pete")) && run.LastNpc == "handin" && ai.Count == aiBefore + 1
                  && ai[^1].StartsWith("handin \"Pistol\" Pete || Pistol Pete"),
                "pete: there, RynthAi hands Pete the trophies: " + (ai.Count > aiBefore ? ai[^1] : "nothing sent"));
            run.Next();
            Check(!run.Active && said.Exists(l => l.Contains("Quest run done") && l.Contains("PeteTrophiesDone")), "pete: done, naming the stamps no step had");

            // Hide collected.
            c.SessionStamps.Add(GuideStamp.Key("FrostWyrmScale"));
            c.SessionStamps.Add(GuideStamp.Key("PeteTrophiesDone"));
            c.SessionStampRevision++;
            c.Settings.GuideHideCollected = true;
            view.ShowGuide(GuideTab.Wiki);
            body = Frame(host, main, view, "guide, hide collected");
            Check(!body.Contains("###g.r.w:\"Pistol\"") && body.Contains("###g.r.w:Mistreated Virindi"), "guide: Hide collected leaves out a quest with every stamp");
            c.Settings.GuideHideCollected = false;

            // Off Infinite Leaftide: the quest list only, four tabs, the flag list a click away.
            c.Server.Force("", "Coldeve");
            Check(view.GuideTabs().Count == 4, "guide: four tabs elsewhere");
            body = Frame(host, main, view, "guide, retail");
            Check(!body.Contains("###g.r.w:") && body.Contains("###q.view.flags"), "guide: no wiki quests off ILT; the Flag list switch");
            c.Settings.QuestGuideView = false;
            File.Delete(file);
        }
    }
}
