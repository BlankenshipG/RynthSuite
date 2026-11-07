using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;

namespace RynthOracle.Tests
{
    // A quest run going into a dungeon (2026-11-07, Silentkelpie: "when creating a route for a quest and enters a
    // portal to a dungeon, look if a map is loaded if so use that map to then create a route to continue the
    // quest inside the dungeon, to fight, pickup items, unlock doors, open doors").
    internal static partial class Program
    {
        private static void QuestDungeonTests()
        {
            var q = new GuideQuest { Title = "Keep Run", StartsWith = "Bob" };
            q.Steps.Add(new GuideStep { Text = "Enter the Hidden Keep at 28.5S, 90.9W.", Coords = new List<string> { "28.5S, 90.9W" } });
            q.Steps.Add(new GuideStep { Text = "Inside, kill the Captain and loot the Cave Door Key.", Items = new List<string> { "Cave Door Key" } });
            q.Steps.Add(new GuideStep { Text = "Use the Cave Door Key on the door at 12.3S, 45.6E.", Coords = new List<string> { "12.3S, 45.6E" }, Stamps = new List<string> { "KeepDoorOpened" } });
            q.Steps.Add(new GuideStep { Text = "Return to Bob for your reward." });
            q.Loot.Add("Cave Door Key");

            var nav = new List<string>(); var ai = new List<string>(); var said = new List<string>();
            var run = new QuestRunner(l => { nav.Add(l); return true; }, l => { ai.Add(l); return true; }, said.Add);
            run.OnWhere(false);
            run.Start(q, _ => false, loot: false);
            Check(nav.Count == 1 && nav[0] == "go 28.5S, 90.9W", "dungeon: the way in goes to the entrance: " + string.Join(" | ", nav));
            run.OnChat("[RynthNav] goto: arrived");
            Check(ai.Count > 0 && ai[^1] == "dungeon enter", "dungeon: at an 'Enter ...' step's place, RynthAi takes the portal there: " + string.Join(" | ", ai));

            run.OnWhere(true);
            Check(run.Step == 1 && run.DungeonWalk && ai[^1] == "dungeon patrol | Cave Door Key",
                "dungeon: in: the way-in step is done, and a step without coordinates patrols the dungeon's map (fight, loot, doors; the quest's key): " + ai[^1]);
            Check(said.Exists(l => l.StartsWith("Step 1 done: you're in")) && run.Note.Contains("patrolling the dungeon"), "dungeon: chat and the view say so (" + run.Note + ")");
            int navBefore = nav.Count;
            run.Next();
            Check(run.Step == 2 && nav.Count == navBefore && ai[^1] == "dungeon goto -12.3 45.6 | Cave Door Key",
                "dungeon: a step with coordinates walks the map there with RynthAi, not RynthNav: " + ai[^1]);
            run.OnWhere(false);
            Check(ai[^1] == "dungeon stop" && !run.DungeonWalk, "dungeon: out again: the dungeon walk ends");
            run.OnWhere(true);
            Check(ai[^1].StartsWith("dungeon goto") && run.DungeonWalk, "dungeon: back in: the step is walked again");
            run.Stop();
            Check(ai.Contains("dungeon stop") && ai[^1] == "dungeon stop" && !run.DungeonWalk, "dungeon: Stop ends the dungeon walk: " + ai[^1]);

            Check(QuestRunner.TrySignedCoords("28.5S, 90.9W", out double ns, out double ew) && ns == -28.5 && ew == -90.9
                  && QuestRunner.TrySignedCoords("85.8s,78.8e", out ns, out ew) && ns == -85.8 && ew == 78.8
                  && !QuestRunner.TrySignedCoords("the Keep", out _, out _), "dungeon: coordinates as signed numbers (north and east positive)");
            Check(QuestRunner.EntersOnly(q.Steps[0]) && !QuestRunner.EntersOnly(q.Steps[1]) && !QuestRunner.EntersOnly(q.Steps[2]),
                "dungeon: only a step whose job is going in is done by going in");
            var keys = QuestRunner.QuestKeys(new GuideQuest { Loot = new List<string> { "Cave Door Key", "Monkey Paw", "Keystone", "Bonecrunch's Chest Key" } });
            Check(string.Join(",", keys) == "Cave Door Key,Bonecrunch's Chest Key", "dungeon: the quest's keys (not a Monkey Paw or a Keystone): " + string.Join(",", keys));
        }
    }
}
