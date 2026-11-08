using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthOracle;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Views;

namespace RynthOracle.Tests
{
    // 2026-11-08, Silentkelpie: "on multi step quests, add in a options to click on the step you want to do vs using
    // just next or back. also when one clicks on a npc and they talk back check to see if their text is the start of
    // a quest and ask if you want to do that quest."
    internal static partial class Program
    {
        private static void QuestOfferTests()
        {
            // Any step, picked.
            var q = new GuideQuest { Title = "Three Steps" };
            q.Steps.Add(new GuideStep { Text = "Go to 1.0N, 1.0E.", Coords = new List<string> { "1.0N, 1.0E" } });
            q.Steps.Add(new GuideStep { Text = "Go to 2.0N, 2.0E.", Coords = new List<string> { "2.0N, 2.0E" } });
            q.Steps.Add(new GuideStep { Text = "Go to 3.0N, 3.0E.", Coords = new List<string> { "3.0N, 3.0E" } });
            var nav = new List<string>(); var ai = new List<string>(); var said = new List<string>();
            var run = new QuestRunner(l => { nav.Add(l); return true; }, l => { ai.Add(l); return true; }, said.Add);
            run.Start(q, _ => false, loot: false);
            run.GoTo(2);
            Check(run.Step == 2 && nav.Count == 3 && nav[1] == "stop" && nav[2] == "go 3.0N, 3.0E", "steps: a picked step: the trip stops, its place is next: " + string.Join(" | ", nav));
            run.GoTo(0);
            Check(run.Step == 0 && nav[^1] == "go 1.0N, 1.0E", "steps: back to the first, done or not");
            run.GoTo(7);
            Check(run.Step == 0, "steps: no such step: nothing changes");
            run.Stop();

            // An NPC talking.
            Check(QuestOffers.TryNpcLine("Xsao Lann tells you, \"Have you seen my dog?\"", out string who, out string text) && who == "Xsao Lann" && text == "Have you seen my dog?",
                "offer: an NPC's tell: who and what");
            Check(QuestOffers.TryNpcLine("Warden says, \"Show me what you found.\"", out who, out _) && who == "Warden", "offer: an NPC's say");
            Check(!QuestOffers.TryNpcLine("You say, \"hello\"", out _, out _) && !QuestOffers.TryNpcLine("You've stamped AcidAxe!", out _, out _),
                "offer: your own words and other lines are not an NPC");

            var collar = new GuideQuest { Title = "Darling's Collar", StartsWith = "Xsao Lann", Overview = "Find the lost dog collar." };
            var other = new GuideQuest { Title = "Fort Supplies", StartsWith = "Xsao Lann / Guard Bob" };
            var axe = new GuideQuest { Title = "Acid Axe Quest", StartsWith = "Warden (Suntik)" };
            var list = QuestOffers.StartedBy(new[] { other, collar, axe }, "Xsao Lann", "Have you seen my dog? He lost his collar.");
            Check(list.Count == 2 && list[0] == collar && list[1] == other, "offer: the NPC's quests, the one its words match first: " + string.Join(", ", list.ConvertAll(x => x.Title)));
            Check(QuestOffers.StartedBy(new[] { axe }, "Warden", "").Count == 1, "offer: a note in brackets after the name doesn't count");
            Check(QuestOffers.StartedBy(new[] { axe, collar }, "Xsao", "").Count == 0, "offer: only the whole name");

            // The offer window: quests the character can run now.
            var g = new QuestGuide { Source = "api", UpdatedUtc = DateTime.UtcNow };
            g.Quests.Add(collar);
            g.Quests.Add(other);
            string file = Path.Combine(Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N") + "-wiki-quests.json");
            File.WriteAllText(file, g.ToJson());
            OracleContext c = On("ilt");
            c.Runner = new QuestRunner(l => { nav.Add(l); return true; }, l => { ai.Add(l); return true; }, said.Add);
            c.Guide.Take("{\"path\":" + System.Text.Json.JsonSerializer.Serialize(file) + ",\"revision\":9,\"count\":2,\"login\":true,\"running\":false,\"status\":\"2 quests.\"}");
            var view = new OracleView(c);
            bool shown = true;
            view.SetOfferWindow = v => shown = v;
            Check(view.OfferFor("Xsao Lann", "my dog's collar", 1000) && view.Offered.Count == 2 && view.Offered[0].Title == "Darling's Collar",
                "offer window: the NPC's quests are offered: " + string.Join(", ", ListTitles(view.Offered)));
            Check(!view.OfferFor("Xsao Lann", "again", 2000), "offer window: the same NPC isn't asked about again right away");
            Check(!view.OfferFor("Guard Jim", "hello", 3000), "offer window: an NPC who starts nothing: no offer");
            File.Delete(file);
        }

        private static List<string> ListTitles(IReadOnlyList<GuideQuest> qs)
        {
            var l = new List<string>();
            foreach (GuideQuest x in qs) l.Add(x.Title);
            return l;
        }
    }
}
