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
    // The Quests tab's Kill Tasks list (2026-11-21, Silentkelpie: "add a tab to track kill tasks ... track progress
    // by looking at quests in the client"), and the wiki stamps cut to their flag ("X on first completion").
    // The quests are the wiki guide's own (wiki-quests.json, 2026-11-21), as saved before the stamps were cut.
    internal static partial class Program
    {
        private const string KillGuideJson = """
        {"schema":1,"source":"api","updated":"2026-11-21T13:52:54Z","quests":[
         {"title":"Drudge Lurker Kill Task","url":"u","rev":492370,"type":"Solo","killTask":"Yes","startsWith":"Jarvis Hammerstone",
          "startsAt":"25.6N, 49.4E in Cragstone","repeat":"20 Hours","repeatSec":72000,"qb":0,"steps":[
           {"text":"Talk with Jarvis Hammerstone.","stamps":["DrudgeKillTasks_Start","KillTaskDrudgeLurkers_0507"]},
           {"text":"Kill 100 Drudge Lurkers.","notes":["First Kill:"],"stamps":["KillTaskDrudgeLurkers_0507 on first completion"]},
           {"text":"Return to Jarvis Hammerstone for your rewards.","stamps":["KillTaskDrudgeLurkersWait_0507"]}]},
         {"title":"Kill: Rynthid Minions","url":"u","rev":494756,"level":"180","type":"Solo","killTask":"Yes","startsWith":"Marcus",
          "repeat":"20 Hours","repeatSec":72000,"qb":0,"steps":[
           {"text":"Talk to Marcus.","stamps":["KillTaskRynthidMinions0813"]},
           {"text":"Head to the Rynthid Infested Plains and kill 25 Rynthid Minions.","stamps":["KillTaskRynthidMinions0813 on first completion"]},
           {"text":"Return to Marcus for your rewards.","stamps":["KillTaskRynthidMinionsCompleted0813"]}]},
         {"title":"Void Lord Kill Task","url":"u","rev":1,"type":"Solo","killTask":"Yes","repeat":"20 Hours","repeatSec":72000,"qb":0,"steps":[
           {"text":"Kill 50 Void Lords."},{"text":"Return for your reward."}]},
         {"title":"Crown the Mad","url":"u","rev":2,"type":"Solo","killTask":"False","repeatSec":0,"qb":0,"steps":[
           {"text":"Find the token.","stamps":["You've stamped TokenOfTheMadFound!"]}]},
         {"title":"Mistreated Virindi","url":"u","rev":3,"type":"Solo","repeatSec":72000,"qb":0,"steps":[{"text":"Talk.","stamps":["Sneaky_Virindi"]}]}
        ]}
        """;

        // The Stamps tab from the wiki's Dynamic Stamps page (2026-11-21, Silentkelpie: "the dynamic stamp tab should be
        // using this tab https://www.leaftidewiki.com/Dynamic_Stamps"). The page as it was that day, cut to a sample.
        private static void WikiDynamicStamps()
        {
            List<GuideDynStamp> rows = WikiQuestParser.ParseDynamicStamps(TestText("TestWiki.dynamic-stamps.txt"));
            Check(rows.Count == 11, $"dyn page: every stamped row once, not the intro's, NO STAMP rows or the unsorted list ({rows.Count})");
            GuideDynStamp hiyp = rows[0];
            Check(hiyp.Area == "Arwic" && hiyp.Place == "Arwic" && hiyp.Npc == "Hiyp the Toad" && hiyp.Coords == "33.6N, 56.8E"
                  && hiyp.Stamp == "Dynamic_1_8M6jL^Bwr3S$p" && hiyp.Reward == "Hearty Rabbit Noodle", "dyn page: a row with one cell per line");
            Check(rows[1].Coords == "33.3N, 56.5E" && rows[2].Stamp == "Dynamic_1_w", "dyn page: a tab in a cell, a space in the stamp");
            Check(rows.Any(r => r.Npc == "Gustuv Lansdown" && r.Note == "Mill with Aug NPCs" && r.Coords == "24.9N, 48.5E"), "dyn page: notes and coordinates kept");
            GuideDynStamp saelar = rows.Single(r => r.Npc == "Saelar's Apprentice");
            Check(saelar.Area == "Samsur" && saelar.Coords == "2.5S, 16.4E" && saelar.Reward == "Renegade Ukira of the Mountains" && saelar.Note == "Mnemosyne Collection Site",
                "dyn page: a row on one line split by ||");
            Check(rows.Single(r => r.Npc == "Drogur").Area == "Kor-Gursha / Bur" && !rows.Any(r => r.Npc == "Rheegur"), "dyn page: two links in a heading; NO STAMP left out");
            Check(rows.Single(r => r.Npc == "Gear Knight Recruiter").Area == "Gear Knight Camp, Inner Sea", "dyn page: a heading's shown text");
            Check(rows.Count(r => r.Stamp == "Dynamic_1_lm9d1e7kD") == 1 && rows.Single(r => r.Stamp == "Dynamic_1_lm9d1e7kD").Npc == "Robert Crow", "dyn page: a stamp twice, first row wins");
            GuideDynStamp abbess = rows.Single(r => r.Npc == "Abbess Zik'Lir");
            Check(abbess.Area == "Miscellaneous" && abbess.Place == "Ithaenc" && abbess.Reward == "Hearty Fish Noodle" && abbess.Note == "", "dyn page: miscellaneous, its notes are the place");
            Check(rows.Single(r => r.Npc == "Arikas, Warden of Knorr").Place == "Seat of Knorr", "dyn page: a linked place");
            Check(!rows.Any(r => r.Stamp.StartsWith("Dynamic_1_0eHSd9aS", StringComparison.Ordinal)), "dyn page: the unsorted list is left out");
            Check(WikiQuestParser.ParseDynamicStamps("").Count == 0 && WikiQuestParser.ParseDynamicStamps("no tables").Count == 0, "dyn page: nothing in, nothing out");

            // In the guide file and in the Stamps tab.
            var g = new QuestGuide { DynStamps = rows, DynStampsRev = 77 };
            QuestGuide back = QuestGuide.Parse(g.ToJson());
            Check(back.DynStamps.Count == 11 && back.DynStampsRev == 77 && back.DynStamps[0].Coords == "33.6N, 56.8E", "guide json: the Dynamic stamps round-trip");
            int sheet = DynamicStampList.All.Count;
            Check(DynamicStampList.Source == "sheet" && sheet > 200, "stamps: the sheet until the wiki's page is read");
            DynamicStampList.UseWiki(back.DynStamps);
            Check(DynamicStampList.Source == "wiki" && DynamicStampList.All.Count == 11, "stamps: the wiki's page replaces the sheet");
            Check(DynamicStampList.Areas.SequenceEqual(new[] { "Arwic", "Cragstone", "Samsur", "Kor-Gursha / Bur", "Gear Knight Camp, Inner Sea", "Miscellaneous" }),
                "stamps: the wiki's places, in page order");
            DynamicStamp h = DynamicStampList.All[0];
            Check(h.Stamp == "dynamic_1_8m6jl^bwr3s$p" && h.NavCandidates()[0] == "33.6N, 56.8E", "stamps: lower-case flag, RynthNav goes to the coordinates first");
            OracleContext c = On("ilt");
            c.Flags.OnChatLine("Dynamic_1_8M6jL^Bwr3S$p - 1 solves (1719432000)\"x\" -1 0", c.NowMs);
            c.Flags.Tick(c.NowMs + 5_000);
            Check(h.Have(c.Flags), "stamps: a wiki stamp matches /myquests");
            DynamicStampList.UseWiki(null);
            Check(DynamicStampList.Source == "sheet" && DynamicStampList.All.Count == sheet, "stamps: back to the sheet when the guide goes");

            // The client takes the guide's stamps when it reads the file.
            string file = Path.Combine(Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N") + "-dyn.json");
            File.WriteAllText(file, g.ToJson());
            c.Guide.Take("{\"path\":" + System.Text.Json.JsonSerializer.Serialize(file) + ",\"revision\":1,\"count\":0,\"login\":true,\"running\":false,\"status\":\"ok\"}");
            Check(DynamicStampList.Source == "wiki" && DynamicStampList.All.Count == 11, "stamps: the guide client hands them over");
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            c.Settings.QuestGuideView = true;
            c.Settings.TabName = OracleView.TabQuests;
            view.ShowGuide(GuideTab.Stamps, "a:Arwic");
            string body = Frame(host, main, view, "stamps from the wiki");
            Check(body.Contains("Stamps: the wiki's Dynamic Stamps page (11 stamps in 6 places)") && body.Contains("###g.r.a:Arwic") && body.Contains("1 / 3"),
                "stamps tab: the wiki's places, with what you hold");
            Check(body.Contains("Hiyp the Toad - Arwic"), "stamps tab: the opened place lists its NPCs");
            c.Guide.Clear();
            Check(DynamicStampList.Source == "sheet", "stamps: a cleared guide goes back to the sheet");
            try { File.Delete(file); } catch { }
        }

        private static void KillTasks()
        {
            // ── Stamps cut to the flag ───────────────────────────────────────
            Check(GuideStamp.Clean("KillTaskDrudgeLurkers_0507 on first completion") == "KillTaskDrudgeLurkers_0507", "stamp clean: 'on first completion' cut");
            Check(GuideStamp.Clean("You've stamped GleeqeoStart!") == "GleeqeoStart" && GuideStamp.Clean("stamped BackToTheFuture!") == "BackToTheFuture",
                "stamp clean: the server's line copied into the wiki");
            Check(GuideStamp.Clean("Shiny Red Stone Found") == "Shiny Red Stone Found" && GuideStamp.Clean("Sneaky_Virindi") == "Sneaky_Virindi",
                "stamp clean: plain stamps left alone");
            Check(GuideStamp.Clean("Bonus_Flag (after the boss)") == "Bonus_Flag", "stamp clean: a note in brackets cut");
            Check(WikiQuestParser.Version >= 2, "parser: version raised for the cut stamps");

            QuestGuide g = QuestGuide.Parse(KillGuideJson);
            Check(g.Parser == 1, "guide json: a guide saved before the version reads as reader 1 (read again whole)");
            GuideQuest drudge = g.Find("Drudge Lurker Kill Task")!;
            Check(drudge.Steps[1].Stamps.SequenceEqual(new[] { "KillTaskDrudgeLurkers_0507" }), "guide json: an old guide's stamps are cut on reading");
            Check(drudge.AllStamps().Count() == 3, "guide json: the counter stamp counted once");
            Check(g.Find("Crown the Mad")!.Steps[0].Stamps[0] == "TokenOfTheMadFound", "guide json: 'You've stamped X!' read as X");
            var round = QuestGuide.Parse(new QuestGuide { Parser = WikiQuestParser.Version, Quests = g.Quests }.ToJson());
            Check(round.Parser == WikiQuestParser.Version, "guide json: the reader's version round-trips");
            GuideQuest wiki = WikiQuestParser.Parse("X Kill Task", "{{Quest Summary|Kill Task=Yes}}\n== Walkthrough ==\n# Kill 10 X.\n#* {{stamped|KillTaskX_01 on first completion}}\n");
            Check(wiki.AllStamps().SequenceEqual(new[] { "KillTaskX_01" }), "parser: {{stamped|X on first completion}} is X");

            // ── Which quests are kill tasks, the goal, the creature ──────────
            Check(drudge.IsKillTask && g.Find("Void Lord Kill Task")!.IsKillTask && !g.Find("Crown the Mad")!.IsKillTask && !g.Find("Mistreated Virindi")!.IsKillTask,
                "kill task: the box's Yes; False is not one");
            Check(new GuideQuest { Title = "Kill: Rynthid Slayers" }.IsKillTask && new GuideQuest { Title = "Dave and Buster's Killtask" }.IsKillTask,
                "kill task: by the title when the box doesn't say");
            Check(drudge.KillTarget == 100 && g.Find("Kill: Rynthid Minions")!.KillTarget == 25, "kill task: the goal from the walkthrough");
            Check(drudge.KillCreatureKey == "drudgelurker" && g.Find("Kill: Rynthid Minions")!.KillCreatureKey == "rynthidminion", "kill task: the creature key");

            // ── Kill lines in chat ───────────────────────────────────────────
            var kc = new KillChat();
            DateTime t = DateTime.UtcNow;
            Check(kc.OnChatLine("You have killed 27 Empyrean Handlers! You must kill 250 to complete your task.", t), "kill chat: Kelpie's line read");
            Check(kc.For("empyreanhandler") is KillCount e && e.Kills == 27 && e.Goal == 250, "kill chat: count and goal kept by creature");
            Check(kc.OnChatLine("You have killed some Coral Golem Viceroys", t) && kc.For("coralgolemviceroy") is KillCount cv && cv.Kills == 0,
                "kill chat: 'some X' marks the creature");
            Check(!kc.OnChatLine("You have killed the Olthoi Queen with a mighty blow.", t) || kc.ByCreature.Count == 3, "kill chat: other lines change little");
            Check(!kc.OnChatLine("Jarvis tells you, \"Kill 100 drudges\"", t), "kill chat: not a kill line");

            // ── The tab ──────────────────────────────────────────────────────
            string file = Path.Combine(Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N") + "-kills.json");
            File.WriteAllText(file, KillGuideJson);
            OracleContext c = On("ilt");
            c.UtcNow = t;
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            Check(view.GuideTabs().Contains(GuideTab.KillTasks) && view.GuideTabs().Count == 10, "kill tab: on Infinite Leaftide");
            Check(new OracleView(On("")).GuideTabs().Contains(GuideTab.KillTasks), "kill tab: on every server (2026-11-21)");
            c.Settings.QuestGuideView = true;
            c.Settings.TabName = OracleView.TabQuests;
            c.Guide.Take("{\"path\":" + System.Text.Json.JsonSerializer.Serialize(file) + ",\"revision\":1,\"count\":5,\"login\":true,\"running\":false,\"status\":\"ok\"}");
            view.ShowGuide(GuideTab.KillTasks, "w:Drudge Lurker Kill Task");
            Check(view.KillTasksShowing, "kill tab: says it is showing");

            string body = Frame(host, main, view, "kill tasks, nothing read");
            Check(body.Contains("###g.r.w:Drudge Lurker Kill Task") && body.Contains("###g.r.w:Void Lord Kill Task") && body.Contains("###g.r.w:Kill: Rynthid Minions"),
                "kill tab: the wiki's kill tasks");
            Check(!body.Contains("###g.r.w:Mistreated Virindi") && !body.Contains("###g.r.w:Crown the Mad"), "kill tab: only kill tasks");
            Check(body.Contains("0 / 100") && body.Contains("0 / 25"), "kill tab: not started shows the goal");
            Check(body.Contains("Kills: not started (the task is 100 kills)"), "kill tab: the opened task's line");
            // 2026-11-22: each kill task says first time or done before.
            Check(body.Contains("(first time)") && body.Contains("First time: no stamp or flag of this kill task held yet.") && !body.Contains("Done before:"),
                "kill tab: nothing held: every task is the first time");

            // /myquests: the counter flag at 37 of 100.
            long at = new DateTimeOffset(t.AddMinutes(-30)).ToUnixTimeSeconds();
            string[] flags =
            {
                $"drudgekilltasks_start - 1 solves ({at})\"x\" -1 0",
                $"killtaskdrudgelurkers_0507 - 37 solves ({at})\"Drudge Lurkers\" 100 0",
                $"killtaskrynthidminions0813 - 25 solves ({at})\"x\" 25 0",
                $"killtaskoldtumeroks_0101 - 12 solves ({at})\"Tumeroks\" 40 0",
                $"voidlordkilltask - 3 solves ({at})\"x\" 50 0",
            };
            void Read(params string[] lines)
            {
                foreach (string l in lines) c.Flags.OnChatLine(l, c.NowMs);
                c.Flags.Tick(c.NowMs + 5_000);
            }
            Read(flags);
            body = Frame(host, main, view, "kill tasks, flags read");
            Check(body.Contains("37 / 100") && body.Contains("Kills: 37 / 100  (63 left)"), "kill tab: the counter flag's kills");
            Check(body.Contains("from /myquests: killtaskdrudgelurkers_0507 (37 of 100)"), "kill tab: says where the count came from");
            Check(body.Contains("Turn in"), "kill tab: all 25 minions killed: turn in");
            Check(body.Contains("3 / 50"), "kill tab: a task the wiki lists no stamps for finds its counter by the creature");
            Check(body.Contains("###g.r.k:killtaskoldtumeroks_0101") && body.Contains("12 / 40"), "kill tab: a counter no wiki task names is listed too");
            Check(body.Contains("Kill tasks: 2 counting, 1 to turn in"), "kill tab: the summary line");
            Check(body.Contains("(first time)") && !body.Contains("(done before)"), "kill tab: counting and the start flag don't make it done before");
            int iTurn = body.IndexOf("###g.r.w:Kill: Rynthid Minions", StringComparison.Ordinal), iCount = body.IndexOf("###g.r.w:Drudge Lurker Kill Task", StringComparison.Ordinal);
            Check(iTurn >= 0 && iCount > iTurn, "kill tab: to turn in first, then counting");

            // A kill line after the read moves the count at once.
            Check(c.Kills.OnChatLine("You have killed 38 Drudge Lurkers! You must kill 100 to complete your task.", t.AddMinutes(1)), "kill chat: a Drudge Lurker");
            body = Frame(host, main, view, "kill tasks, a kill");
            Check(body.Contains("38 / 100") && body.Contains("from the last kill line in chat"), "kill tab: the chat count wins over an older read");

            // Turned in: the wait flag on its timer.
            long now = new DateTimeOffset(t).ToUnixTimeSeconds();
            Read(flags.Append($"killtaskrynthidminionscompleted0813 - 1 solves ({now})\"x\" -1 72000").ToArray());
            body = Frame(host, main, view, "kill tasks, turned in");
            Check(body.Contains("Done 20h") || body.Contains("Done 19h 59m"), "kill tab: a turned-in task waits on its timer");
            Check(body.Contains("Done before: a stamp or flag of this kill task is held"), "kill tab: a turned-in task is done before");

            // Hide collected leaves the waiting one out.
            c.Settings.GuideHideCollected = true;
            body = Frame(host, main, view, "kill tasks, hide collected");
            Check(!body.Contains("###g.r.w:Kill: Rynthid Minions") && body.Contains("###g.r.w:Drudge Lurker Kill Task"), "kill tab: hide collected");
            c.Settings.GuideHideCollected = false;

            // The wiki's Kill Tasks category marks a page whose box says nothing.
            var g2 = new QuestGuide { KillTasks = { "Weeding of the Deru Tree" } };
            Check(g2.IsKillTask(new GuideQuest { Title = "Weeding of the Deru Tree" }) && !g2.IsKillTask(new GuideQuest { Title = "Mistreated Virindi" }),
                "kill task: the wiki's Kill Tasks category");
            Check(QuestGuide.Parse(g2.ToJson()).KillTasks.SequenceEqual(new[] { "Weeding of the Deru Tree" }), "guide json: the kill task list round-trips");

            Check(OracleView.FlagLabel("KillTaskDrudgeLurkers_0507") == "Kill Task Drudge Lurkers 0507", "kill tab: a flag's label");
            try { File.Delete(file); } catch { }
        }
    }
}
