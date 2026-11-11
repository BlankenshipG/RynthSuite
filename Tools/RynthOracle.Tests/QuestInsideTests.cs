using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;

namespace RynthOracle.Tests
{
    // A quest's person inside a dungeon or the Bur (2026-11-11, Silentkelpie: "some npc can be inside dungeons or bur
    // and should route to them correctly"): RynthNav's arrival names the cell, RynthAi walks the dungeon's map there.
    internal static partial class Program
    {
        private static void QuestInsideTests()
        {
            Check(QuestRunner.InsideCell("[RynthNav] goto: arrived at Drogur (Drogur is in cell 009C0270)") == 0x009C0270u, "inside: the arrival's cell is read");
            Check(QuestRunner.InsideCell("[RynthNav] goto: arrived at Drogur") == 0, "inside: no cell, none");

            var q = new GuideQuest { Title = "Bur Run" };
            q.Steps.Add(new GuideStep { Text = "Talk to Drogur.", Stamps = new List<string> { "TalkedDrogur" } });
            q.Steps.Add(new GuideStep { Text = "Talk to Kuurak.", Stamps = new List<string> { "TalkedKuurak" } });
            var nav = new List<string>(); var ai = new List<string>(); var said = new List<string>();
            var run = new QuestRunner(l => { nav.Add(l); return true; }, l => { ai.Add(l); return true; }, said.Add);
            run.OnWhere(false, 0);
            run.Start(q, _ => false, loot: false);
            Check(nav.Count == 1 && nav[0].StartsWith("go Drogur"), "inside: RynthNav is asked for the person: " + string.Join(" | ", nav));
            run.OnWhere(true, 0x009C);   // Bur Recall, mid-goto
            run.OnChat("[RynthNav] goto: arrived at Drogur (Drogur is in cell 009C0270)");
            int to = ai.FindIndex(l => l == "dungeon to 009C0270");
            int talk = ai.FindIndex(l => l.StartsWith("talk Drogur"));
            Check(to >= 0 && talk > to && run.DungeonWalk, "inside: RynthAi walks the map to the cell, then the talk waits on the way: " + string.Join(" | ", ai));

            // Already inside: the next step's person is asked for in this dungeon first.
            nav.Clear(); ai.Clear();
            run.Next();
            Check(nav.Count == 1 && nav[0].StartsWith("inside Kuurak") && run.Travelling, "inside: in the dungeon, RynthNav says which cell: " + string.Join(" | ", nav));
            run.OnChat("[RynthNav] goto: arrived in the Bur: Kuurak is inside (Kuurak is in cell 009C012D)");
            Check(ai.Exists(l => l == "dungeon to 009C012D") && ai.Exists(l => l.StartsWith("talk Kuurak")), "inside: walked there: " + string.Join(" | ", ai));

            // Not in this dungeon by the Atlas: patrolled as before.
            run.Back(); nav.Clear(); ai.Clear();
            run.Next();
            run.OnChat("[RynthNav] inside: none of these is someone inside a dungeon: Kuurak");
            Check(!run.Travelling && ai.Exists(l => l.StartsWith("dungeon patrol")) && ai.Exists(l => l.StartsWith("talk Kuurak")), "inside: not here: the dungeon is patrolled: " + string.Join(" | ", ai));
            run.Stop();

            // 2026-11-11: one hand-in for every Quests view.
            var hq = new GuideQuest { Title = "Collar", StartsWith = "Xsao Lann" };
            hq.Steps.Add(new GuideStep { Text = "Kill the wolf and loot Darling's Collar.", Items = new List<string> { "Darling's Collar" } });
            hq.Steps.Add(new GuideStep { Text = "Return the collar to Xsao Lann.", Stamps = new List<string> { "CollarReturned" } });
            var h = RynthCore.Plugin.RynthOracle.Views.OracleView.HandInOf(hq);
            Check(h is { } a1 && a1.Step == 1 && ReferenceEquals(a1.Quest, hq), "hand-in: the quest's own hand-in step");
            var kq = new GuideQuest { Title = "Tusks", StartsWith = "Ulgrim" };
            kq.Steps.Add(new GuideStep { Text = "Kill Female Tuskers for their tusks." });
            kq.Rewards.Add("For Hand in Female Tusker Tusk:");
            var k = RynthCore.Plugin.RynthOracle.Views.OracleView.HandInOf(kq);
            Check(k is { } a2 && a2.Quest.Steps.Count == 1 && QuestNames.IsHandIn(a2.Quest.Steps[0]) && QuestNames.HandInItems(a2.Quest, 0).Contains("Female Tusker Tusk")
                  && QuestNames.TargetsAt(a2.Quest, 0).Contains("Ulgrim"),
                "hand-in: rewards for handing in, to the person it starts with: " + (k?.Quest.Steps[0].Text ?? "none"));
            Check(RynthCore.Plugin.RynthOracle.Views.OracleView.HandInOf(new GuideQuest { Title = "Walk" }) == null, "hand-in: nothing to hand in, none");
        }
    }
}
