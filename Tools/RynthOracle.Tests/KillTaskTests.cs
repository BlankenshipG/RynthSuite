using System;
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
            Check(view.GuideTabs().Contains(GuideTab.KillTasks) && view.GuideTabs().Count == 8, "kill tab: on Infinite Leaftide");
            Check(!new OracleView(On("")).GuideTabs().Contains(GuideTab.KillTasks), "kill tab: not elsewhere");
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

            // Hide collected leaves the waiting one out.
            c.Settings.GuideHideCollected = true;
            body = Frame(host, main, view, "kill tasks, hide collected");
            Check(!body.Contains("###g.r.w:Kill: Rynthid Minions") && body.Contains("###g.r.w:Drudge Lurker Kill Task"), "kill tab: hide collected");
            c.Settings.GuideHideCollected = false;

            Check(OracleView.FlagLabel("KillTaskDrudgeLurkers_0507") == "Kill Task Drudge Lurkers 0507", "kill tab: a flag's label");
            try { File.Delete(file); } catch { }
        }
    }
}
