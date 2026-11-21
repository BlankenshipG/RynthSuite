using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthOracle;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.RynthOracle.Views;

namespace RynthOracle.Tests
{
    // RynthOracle's own wiki feed from the AC Community Wiki (asheron.fandom.com) and the Quests tab's Wiki, Kill
    // Tasks, Society and Contracts lists on every server (2026-11-21, Silentkelpie: "under oracle duplicate itl
    // functions ... tabs for quests, kill task, socity quests, contracts ... should not be gated behind any server
    // check"). The pages are the wiki's own (2026-11-21), cut down.
    internal static partial class Program
    {
        private const string FandomDrudge = """
        {{Quest Summary
         |           Kill Task = Yes
         |          Quest Type = Solo
         |               Level = Any
         |   Recommended Level = 20+
         |         Starts With = [[Jarvis Hammerstone]]
         |           Starts At = 25.6N, 49.4E in [[Cragstone]]
         |               Route =
         |        Repeat Timer = 20 Hours
         |    Time to Complete =
         |           Contracts =
        }}
        == Walkthrough & Notes ==
        # Kill 100 [[Drudge Lurker]]s.
        # Return to [[Jarvis Hammerstone]] for your rewards.

        == Rewards ==
        {{Rewards Table
         |           Quest Task =
         |        Max XP Reward = 441,350 |        Percent of Level = 50 |         Level Cap = 30
         |         Title Reward = Drudge Doom
        }}
        """;

        private const string FandomAdept = """
        {{Quest Summary
         |               Level = 180
         |        Society Rank = Initiate
         |          Quest Type = Solo
         |         Starts With = [[Aldrion of the Celestial Hand]] ([[Celestial Hand|CH]])
         |           Starts At = [[Faction Stronghold]] (east wing)
         |        Repeat Timer = Non Repeatable
        }}
        == Walkthrough & Notes ==
        # Take the [[Neydisa Castle]] portal in [[Town Network]] (South Wing) and run north to 74.8N, 18.4E. Enter the [[Rogue Delvings]].
        # At the end of each wing is a named Captain surrounded by Scouts that will give you a key (if same faction) or will drop the key on his corpse (if opposing faction).
        ## {{Itemlink|Donnarion's Key}} : can be found in the west wing
        """;

        private const string FandomContract = """
        {{Intro
         | Patch Introduced = [[Stirrings in the Dark]]
         |   Related Quests = [[Aerbax's Citadel]]
         |          Updated =
        }}
        {{Item
         |               Name = Contract for Aerbax's Defeat
         |         Icon Image = Contract for Aerbax's Defeat Icon.png
         |        Description = The Shadow of Aerbax must be defeated.
         |  Recommended Level = 150
        }}
        == Notes ==
        * Purchased from [[Hanadi the Barkeeper]] in the Sho hall of the [[Town Network]].

        [[Category:Contracts]]
        """;

        private const string FandomCitadel = """
        {{Quest Summary
         |          Quest Type = Fellowship
         |         Starts With = [[Hanadi the Barkeeper]]
         |        Repeat Timer = 20 Hours
         |           Contracts = [[Contract for Aerbax's Defeat]]
        }}
        == Walkthrough & Notes ==
        # Use the contract, then enter [[Aerbax's Citadel]].
        # Defeat the Shadow of Aerbax.
        """;

        private static void Fandom()
        {
            // ── The pages ────────────────────────────────────────────────────
            GuideQuest drudge = WikiQuestParser.Parse("Drudge Lurker Kill Task", FandomDrudge, 11, FandomFeed.WikiBase);
            Check(drudge.Url == "https://asheron.fandom.com/wiki/Drudge_Lurker_Kill_Task" && drudge.IsKillTask && drudge.KillTarget == 100 && drudge.RepeatSec == 72000,
                "fandom: a kill task page, the fandom's address");
            Check(drudge.Steps.Count == 2 && drudge.StartsWith == "Jarvis Hammerstone", "fandom: its steps and start");
            GuideQuest adept = WikiQuestParser.Parse("Adept Test", FandomAdept, 12, FandomFeed.WikiBase);
            Check(adept.SocietyRank == "Initiate" && adept.IsOneTime && adept.Steps.Count >= 2, $"fandom: a society quest's rank from its box ({adept.SocietyRank}|{adept.RepeatSec}|{adept.Steps.Count}|{string.Join(",", adept.Loot)}|{string.Join(";", adept.Steps.SelectMany(x => x.Items))})");
            Check(adept.Loot.Contains("Donnarion's Key"), "fandom: the items a step names are the quest's loot (its loot profile)");
            Check(WikiQuestParser.IsContractPage("Contract for Aerbax's Defeat", FandomContract) && !WikiQuestParser.IsContractPage("Adept Test", FandomAdept),
                "fandom: a contract page");
            GuideContract con = WikiQuestParser.ParseContract("Contract for Aerbax's Defeat", FandomContract, 13, FandomFeed.WikiBase);
            Check(con.Name == "Contract for Aerbax's Defeat" && con.Description == "The Shadow of Aerbax must be defeated." && con.Level == "150",
                "fandom contract: name, description, level");
            Check(con.Npc == "Hanadi the Barkeeper" && con.Where.StartsWith("Purchased from Hanadi the Barkeeper in the Sho hall", StringComparison.Ordinal),
                "fandom contract: where it comes from, and who");
            Check(con.Quests.SequenceEqual(new[] { "Aerbax's Citadel" }) && con.Rev == 13, "fandom contract: the quest it is for");
            Check(GuideStamp.Clean("x") == "x" && adept.AllStamps().Count() == 0, "fandom: no stamps on the AC Community Wiki");

            // ── The feed's own logic ─────────────────────────────────────────
            Check(FandomFeed.RankOf("Category:Society Quests: Knight Rank") == "Knight" && FandomFeed.RankOf("Category:Society Quests: Initiate Rank") == "Initiate"
                  && FandomFeed.RankOf("Category:Joining a Society Quests") == "Joining" && FandomFeed.RankOf("Category:Kill Tasks") == "",
                "fandom feed: the rank from the society sub-category");
            Check(FandomFeed.SkipCategory("Category:Quest Items") && FandomFeed.SkipCategory("Category:Contract Brokers and Barkeeps")
                  && !FandomFeed.SkipCategory("Category:Kill Tasks by Level") && !FandomFeed.SkipCategory("Category:Society Quests: Adept Rank"),
                "fandom feed: item and NPC categories are not followed");
            var (pages, subs, cont) = MediaWikiJson.Members("""{"continue":{"cmcontinue":"page|X","continue":"-||"},"query":{"categorymembers":[{"ns":0,"title":"Adept Test"},{"ns":14,"title":"Category:Society Quests: Adept Rank"},{"ns":6,"title":"File:x.png"}]}}""", "categorymembers");
            Check(pages.SequenceEqual(new[] { "Adept Test" }) && subs.SequenceEqual(new[] { "Category:Society Quests: Adept Rank" }) && cont.Count == 2,
                "fandom feed: a category page, its sub-categories and the continue");
            var old = new QuestGuide { Parser = WikiQuestParser.Version, UpdatedUtc = DateTime.UtcNow };
            old.Quests.Add(drudge);
            old.Contracts.Add(con);
            var revs = new Dictionary<string, long> { ["Drudge Lurker Kill Task"] = 11, ["Contract for Aerbax's Defeat"] = 14, ["Adept Test"] = 12, ["Gone"] = 1 };
            Check(FandomFeed.PagesToRead(old, revs).OrderBy(x => x).SequenceEqual(new[] { "Adept Test", "Contract for Aerbax's Defeat", "Gone" }),
                "fandom feed: only new and edited pages are read");
            Check(FandomFeed.PagesToRead(new QuestGuide { Parser = 1, Quests = { drudge } }, revs).Count == 4, "fandom feed: every page for an older reader");
            QuestGuide merged = FandomFeed.Merge(old, revs, new Dictionary<string, GuideQuest> { ["Adept Test"] = adept }, new Dictionary<string, GuideContract>());
            Check(merged.Quests.Select(q => q.Title).SequenceEqual(new[] { "Adept Test", "Drudge Lurker Kill Task" }) && merged.Contracts.Count == 1 && merged.Source == "fandom",
                "fandom feed: the merge keeps unchanged pages and takes fresh ones");
            Check(FandomFeed.Due(new QuestGuide(), DateTime.UtcNow) && !FandomFeed.Due(old, DateTime.UtcNow) && FandomFeed.Due(old, DateTime.UtcNow.AddHours(25)),
                "fandom feed: read at login when none, or a day old");

            // The guide file keeps the society ranks and the contracts.
            GuideQuest citadel = WikiQuestParser.Parse("Aerbax's Citadel", FandomCitadel, 15, FandomFeed.WikiBase);
            var g = new QuestGuide { Source = "fandom", Parser = WikiQuestParser.Version, UpdatedUtc = DateTime.UtcNow, Quests = { adept, citadel, drudge }, Contracts = { con } };
            g.KillTasks.Add("Drudge Lurker Kill Task");
            g.Society["Collect Gear Knight Parts"] = "Initiate";
            QuestGuide back = QuestGuide.Parse(g.ToJson());
            Check(back.Contracts.Count == 1 && back.Contracts[0].Npc == "Hanadi the Barkeeper" && back.Contracts[0].Quests[0] == "Aerbax's Citadel"
                  && back.Society["Collect Gear Knight Parts"] == "Initiate" && back.Find("Adept Test")!.SocietyRank == "Initiate",
                "fandom guide json: contracts and society ranks round-trip");

            // ── The tabs, off Infinite Leaftide ──────────────────────────────
            string file = Path.Combine(Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N") + "-fandom.json");
            OracleContext c = On("");
            c.Fandom = new FandomFeed(file);
            Check(c.Fandom.Guide.Quests.Count == 0 && c.Fandom.Status == "not read yet", "fandom feed: nothing saved yet");
            c.Fandom.Use(back);
            Check(c.WikiIsFandom && ReferenceEquals(c.Wiki, c.Fandom.Guide) && c.FindWiki("Aerbax's Citadel") != null, "context: the AC Wiki is the wiki in use off ILT");
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            c.Settings.QuestGuideView = true;
            c.Settings.TabName = OracleView.TabQuests;
            List<GuideTab> tabs = view.GuideTabs();
            Check(tabs.Contains(GuideTab.Wiki) && tabs.Contains(GuideTab.KillTasks) && tabs.Contains(GuideTab.Society) && tabs.Contains(GuideTab.Contracts) && !tabs.Contains(GuideTab.Qb),
                "fandom tabs: Wiki, Kill Tasks, Society and Contracts on any server");

            view.ShowGuide(GuideTab.Wiki, "w:Adept Test");
            string body = Frame(host, main, view, "fandom, wiki");
            Check(body.Contains("AC Wiki") && body.Contains("AC Wiki: 3 quests, 1 contracts"), "fandom tabs: the tab and the source line say AC Wiki");
            Check(body.Contains("###g.r.w:Adept Test") && body.Contains("###g.r.w:Drudge Lurker Kill Task") && body.Contains("###g.r.w:Aerbax's Citadel"), "fandom tabs: every wiki quest");
            Check(body.Contains("###g.run") && body.Contains("###g.loot") && body.Contains("###g.lootprofile"), "fandom tabs: Run this quest, its loot and loot profile");

            view.ShowGuide(GuideTab.KillTasks, "w:Drudge Lurker Kill Task");
            body = Frame(host, main, view, "fandom, kill tasks");
            Check(body.Contains("###g.r.w:Drudge Lurker Kill Task") && !body.Contains("###g.r.w:Adept Test") && body.Contains("0 / 100"), "fandom tabs: the kill tasks");
            Check(body.Contains("Kills: not started (the task is 100 kills)"), "fandom tabs: the kill line off ILT");

            view.ShowGuide(GuideTab.Society, "w:Adept Test");
            body = Frame(host, main, view, "fandom, society");
            Check(body.Contains("Initiate: Adept Test") && !body.Contains("###g.r.w:Drudge Lurker Kill Task"), "fandom tabs: society quests by rank");

            view.ShowGuide(GuideTab.Contracts, "c:Contract for Aerbax's Defeat");
            body = Frame(host, main, view, "fandom, contracts");
            Check(body.Contains("###g.r.c:Contract for Aerbax's Defeat") && body.Contains("Purchased from Hanadi the Barkeeper") && body.Contains("###g.c.go"),
                "fandom tabs: a contract, where it comes from, Go");
            Check(body.Contains("###g.c.q.0") && body.Contains("Aerbax's Citadel"), "fandom tabs: the contract's quest, Open");

            // A quest the wiki has no stamps for takes its state from the quest list's flag of the same name.
            Quest? listed = c.Catalog.Quests.FirstOrDefault(q => !q.IsNew && q.Name.Length > 0 && q.MinDelta <= 0 && !q.Flag.Contains(' '));
            if (listed != null)
            {
                var g2 = new QuestGuide { Source = "fandom", Parser = WikiQuestParser.Version, UpdatedUtc = DateTime.UtcNow,
                    Quests = { new GuideQuest { Title = listed.DisplayName, Url = "u", Steps = { new GuideStep { Text = "Do it." } } } } };
                c.Fandom.Use(g2);
                c.Flags.OnChatLine($"{listed.Flag} - 1 solves ({new DateTimeOffset(DateTime.UtcNow.AddDays(-1)).ToUnixTimeSeconds()})\"x\" 1 0", c.NowMs);
                c.Flags.Tick(c.NowMs + 5_000);
                view.ShowGuide(GuideTab.Wiki, "w:" + listed.DisplayName);
                body = Frame(host, main, view, "fandom, catalog state");
                Check(listed.IsComplete(c.Flags) && body.Contains("Completed"), "fandom tabs: a stampless quest's state from the quest list");
            }
            // A server's own wiki guide ("RynthServer.QuestGuide", RynthConquest's) over the AC Community Wiki's.
            c.Fandom.Use(back);
            var sg = new QuestGuide { Source = "conquest", Parser = WikiQuestParser.Version, UpdatedUtc = DateTime.UtcNow };
            var darsh = new GuideQuest { Title = "Darshmaul the Brutal", Url = "https://conquestac.fandom.com/wiki/Darshmaul_the_Brutal", Type = "Conquest", Repeat = "Daily", RepeatSec = 72000 };
            darsh.Steps.Add(new GuideStep { Text = "Portal to Fort 33.6S 74.9E", Coords = { "33.6S, 74.9E" } });
            darsh.Steps.Add(new GuideStep { Text = "Give to Vovran 33.4S 72.8E", Coords = { "33.4S, 72.8E" }, Stamps = { "assaultonaimaru" } });
            sg.Quests.Add(darsh);
            sg.Quests.Add(new GuideQuest { Title = "Adept Test", Url = "server", Steps = { new GuideStep { Text = "The server's way." } } });
            string sgFile = Path.Combine(Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N") + "-server-guide.json");
            File.WriteAllText(sgFile, sg.ToJson());
            c.ServerGuide.Client.Take("{\"schema\":1,\"path\":" + System.Text.Json.JsonSerializer.Serialize(sgFile) + ",\"revision\":1,\"running\":false,\"login\":true,\"status\":\"read\",\"tag\":\"Conquest\"}");
            Check(c.ServerGuide.Client.Tag == "Conquest" && c.ServerGuide.Client.Guide.Quests.Count == 2, "server guide: taken from the plugin's info");
            Check(!c.WikiIsFandom && c.Wiki.Find("Darshmaul the Brutal") != null && c.Wiki.Find("Drudge Lurker Kill Task") != null && c.Wiki.Quests.Count == 4,
                "server guide: its quests over the AC Wiki's");
            Check(c.Wiki.Find("Adept Test")!.Url == "server", "server guide: its quest of the same name wins");
            Check(c.Wiki.Contracts.Count == 1 && c.WikiLabel == "Conquest Wiki", "server guide: the AC Wiki's contracts kept; the tab is the server's wiki");
            view.ShowGuide(GuideTab.Wiki, "w:Darshmaul the Brutal");
            body = Frame(host, main, view, "server guide");
            Check(body.Contains("Conquest Wiki") && body.Contains("###g.r.w:Darshmaul the Brutal") && body.Contains("###g.run"), "server guide: the tab, its quest, Run this quest");
            c.ServerGuide.Clear();
            Check(c.WikiIsFandom && c.WikiLabel == "AC Wiki", "server guide: gone with the server");
            try { File.Delete(sgFile); } catch { }

            c.Settings.QuestGuideView = false;
            try { File.Delete(file); } catch { }
        }
    }
}
