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
            Check(ai.Count > 0 && ai[^1].StartsWith("dungeon enter"), "dungeon: at an 'Enter ...' step's place, RynthAi takes the portal there: " + string.Join(" | ", ai));

            run.OnWhere(true);
            Check(run.Step == 1 && run.DungeonWalk && ai.FindLast(l => !l.StartsWith("have ")) == "dungeon patrol | Cave Door Key" && ai[^1] == "have Cave Door Key",
                "dungeon: in: the way-in step is done, a step without coordinates patrols the dungeon's map (fight, loot, doors; the quest's key), and RynthAi is asked whether the key is carried: " + string.Join(" | ", ai));
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
            // 2026-11-07: RynthNav's route through a dungeon (the Town Network) isn't the quest's dungeon.
            ai.Clear(); nav.Clear();
            run.OnWhere(false);
            run.Start(q, _ => false, loot: false);
            run.OnWhere(true);    // mid-route, in a hub
            Check(run.Step == 0 && run.Travelling && !run.DungeonWalk && ai.Count == 0, "route: a dungeon on RynthNav's way is passed through, not walked: " + string.Join(" | ", ai));
            run.OnWhere(false);
            run.OnChat("[RynthNav] goto: arrived");
            Check(ai.Count == 1 && ai[0].StartsWith("dungeon enter") && ai[0].Contains("Hidden"), "route: at the place, the portal the step names is asked for: " + ai[0]);
            Check(QuestRunner.TownNetworkLandblock == 0x0007, "route: the Town Network's landblock is never a quest dungeon");
            run.Stop();

            // A portal step that also wants a stamp still goes in; names RynthNav doesn't know fall back to coordinates.
            var q2 = new GuideQuest { Title = "Stamp Run", StartsAt = "A Renegade Stronghold at 71.4N, 28.6W" };
            q2.Steps.Add(new GuideStep { Text = "Talk to Ollar.", Stamps = new List<string> { "TalkedOllar" } });
            q2.Steps.Add(new GuideStep { Text = "Take the portal to the Fenmalain Vestibule and kill the guard.", Stamps = new List<string> { "GuardDead" } });
            ai.Clear(); nav.Clear();
            run.Start(q2, s => s == "TalkedOllar", loot: false);
            Check(run.Step == 0 && nav.Count == 1 && nav[0] == "go 71.4N, 28.6W",
                "start: step 1 even with its stamp held, to the coordinates its start place gives: " + string.Join(" | ", nav));
            run.Next();
            run.OnChat("[RynthNav] none of these is a place on the map: Fenmalain Vestibule");
            Check(run.Travelling && nav[^1] == "go 71.4N, 28.6W", "names unknown: the last coordinates the quest gave: " + string.Join(" | ", nav));
            run.OnChat("[RynthNav] goto: arrived");
            Check(ai.Exists(l => l.StartsWith("dungeon enter") && l.Contains("Fenmalain")), "a stamp step through a portal goes in too: " + string.Join(" | ", ai));
            run.Stop();

            // In by the step's own place: its coordinates are the way in, so the dungeon is patrolled; a portal on into
            // another dungeon walks the step again there (11-07 17:29, Abayar's Laboratory).
            var q3 = new GuideQuest { Title = "Lab Run" };
            q3.Steps.Add(new GuideStep { Text = "Go to Abayar's Laboratory at 92.1N, 46.3W and find the notes.", Coords = new List<string> { "92.1N, 46.3W" }, Items = new List<string> { "Abayar's Research Notes" } });
            ai.Clear(); nav.Clear();
            run.OnWhere(false, 0);
            run.Start(q3, _ => false, loot: false);
            run.OnChat("[RynthNav] goto: arrived");
            run.OnWhere(true, 0x018E);
            ai.RemoveAll(l => l.StartsWith("have ") || l.StartsWith("talk "));
            Check(ai.Count > 0 && ai[^1] == "dungeon patrol", "entrance: the step's outdoor place inside is a patrol, not a walk to it: " + string.Join(" | ", ai));
            int before = ai.Count;
            run.OnWhere(true, 0x01A0);
            Check(ai.Count == before + 2 && ai[before] == "dungeon stop" && ai[before + 1] == "dungeon patrol", "another dungeon: the walk starts again there: " + string.Join(" | ", ai));
            Check(QuestRunner.SameCoords("92.1N, 46.3W", "92.2N, 46.3W") && !QuestRunner.SameCoords("92.1N, 46.3W", "12.3S, 45.6E"), "same place: within half a unit");
            run.Stop();

            // The Female Tusker Tusk Quest as the wiki has it (21:29 on 11-07): no loot listed, "Head to the Tusker
            // Burrow", "dropped by Female Tuskers at the end of the dungeon", the tusk only in the rewards.
            var tusk = new GuideQuest { Title = "Female Tusker Tusk Quest", StartsWith = "Brighteyes, the Tailor", StartsAt = "Oolutanga's Refuge at 2.2N, 95.6E" };
            tusk.Steps.Add(new GuideStep { Text = "Head to the Tusker Burrow at 2.0N, 97.9E.", Coords = new List<string> { "2.0N, 97.9E" } });
            tusk.Steps.Add(new GuideStep { Text = "The tusk is dropped by Female Tuskers at the end of the dungeon (see map).", Stamps = new List<string> { "TuskFemalePickup" } });
            tusk.Steps.Add(new GuideStep { Text = "When you have the tusk, bring it back to Brighteyes to receive your rewards." });
            tusk.Rewards.Add("For Hand in Female Tusker Tusk:");
            tusk.Rewards.Add("Aphus Lassel Portal Gem");
            Check(QuestNames.RewardItems(tusk).Count == 1 && QuestNames.RewardItems(tusk)[0] == "Female Tusker Tusk", "tusk: the rewards name the item handed in");
            Check(!QuestNames.IsTalk(tusk, tusk.Steps[1]), "tusk: \"dropped by Female Tuskers\" is nobody to talk to");
            Check(QuestRunner.GoesIn(tusk, 0), "tusk: \"Head to the Tusker Burrow\" goes in (a burrow; the next step is in the dungeon)");
            ai.Clear(); nav.Clear();
            run.OnWhere(false, 0);
            run.Start(tusk, _ => false, loot: true);
            Check(ai.Contains("add Female Tusker Tusk") && ai.Exists(l => l.StartsWith("profile Female Tusker Tusk Quest|Female Tusker Tusk")),
                "tusk: its loot and its profile: " + string.Join(" | ", ai));
            run.OnChat("[RynthNav] goto: arrived");
            Check(ai[^1].StartsWith("dungeon enter"), "tusk: at the burrow, in by its portal: " + ai[^1]);
            run.Stop();
            var none = new GuideQuest { Title = "Plain Kill" };
            none.Steps.Add(new GuideStep { Text = "Kill ten rats.", Coords = new List<string> { "1.0N, 1.0E" } });
            ai.Clear();
            run.Start(none, _ => false, loot: true);
            Check(ai.Exists(l => l == "profile Plain Kill"), "no items: the quest's profile all the same (base-quest-dynamic): " + string.Join(" | ", ai));
            run.Stop();

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
