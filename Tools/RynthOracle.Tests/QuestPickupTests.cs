using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;

namespace RynthOracle.Tests
{
    // The Acid Axe Quest (2026-11-08, Silentstorm at 09:01: "while it built the quest it and ran to the right spot it
    // still missed talking to the 1st guy and it didn't loot the item off the ground"): the run went to Suntik Village
    // and stayed on step 1 (a travel step has no stamp), so the axe and the Warden never came.
    internal static partial class Program
    {
        private static void QuestPickupTests()
        {
            var q = new GuideQuest { Title = "Acid Axe Quest", StartsWith = "Messenger's Corpse", StartsAt = "26.9N, 7.7E" };
            q.Steps.Add(new GuideStep
            {
                Text = "Travel to Suntik Village at 16.2N 4.3E.",
                Notes = new List<string> { "Route 1: Take the Suntik portal in the west wing of the Facility Hub.", "Route 2: Go to Zaikhal and head northeast out of town to 16.9N 3.5E, a section of the hill that can be climbed." },
                Coords = new List<string> { "16.2N, 4.3E", "16.9N, 3.5E" },
            });
            q.Steps.Add(new GuideStep
            {
                Text = "Enter the blacksmith building and grab the Acid Axe on the ground next to the body of the dead blacksmith. If you want multiple axes simply wait for it to reappear. (Respawn| 5 minutes)",
                Items = new List<string> { "Acid Axe" },
            });
            q.Steps.Add(new GuideStep { Text = "Show it to Warden (Suntik) for a one time xp bonus.", Stamps = new List<string> { "fachubsuntik" } });
            q.Steps.Add(new GuideStep { Text = "Bring Acid Axe to an Ivory Crafter for 37,500 experience.", Stamps = new List<string> { "AcidAxeTurnedIn" } });
            q.Loot.Add("Acid Axe");

            Check(string.Join(" | ", QuestRunner.Places(q.Steps[0])) == "16.2N, 4.3E", "acid axe: the step's own place, not the climb its Route 2 note names: " + string.Join(" | ", QuestRunner.Places(q.Steps[0])));
            Check(QuestRunner.TravelOnly(q, 0), "acid axe: \"Travel to Suntik Village\" is done by getting there");
            Check(!QuestRunner.GoesIn(q.Steps[1]) && !QuestRunner.GoesIn(q, 1) && QuestRunner.IntoBuilding(q.Steps[1].Text), "acid axe: entering the blacksmith building is no portal");
            Check(QuestRunner.IsPickup(q.Steps[1]), "acid axe: \"grab the Acid Axe on the ground\" picks it up");
            Check(QuestNames.IsHandIn(q.Steps[2]), "acid axe: \"Show it to Warden\" hands it over");

            var nav = new List<string>(); var ai = new List<string>(); var said = new List<string>();
            var run = new QuestRunner(l => { nav.Add(l); return true; }, l => { ai.Add(l); return true; }, said.Add);
            run.OnWhere(false, 0);
            run.Start(q, _ => false, loot: true);
            Check(nav.Count == 1 && nav[0] == "go 16.2N, 4.3E", "acid axe: to Suntik Village: " + string.Join(" | ", nav));
            run.OnChat("[RynthNav] goto: arrived");
            Check(run.Step == 1, "acid axe: there, step 1 is done (it stayed on it): step " + (run.Step + 1));
            Check(nav.Count == 1, "acid axe: the axe is picked up where the run is, no trip to the climb or elsewhere: " + string.Join(" | ", nav));
            Check(ai.Count > 0 && ai[^1] == "have Acid Axe", "acid axe: RynthAi is asked whether the axe is carried (else it picks it up): " + string.Join(" | ", ai));
            Check(!ai.Exists(l => l.StartsWith("dungeon enter")), "acid axe: no portal taken for the building: " + string.Join(" | ", ai));
            run.OnChat("[RynthAi] Quest: got Acid Axe.");
            Check(run.Step == 2, "acid axe: got it: on to the Warden: step " + (run.Step + 1));
            Check(nav.Count == 2 && nav[1].StartsWith("go Warden"), "acid axe: to the Warden: " + string.Join(" | ", nav));
            run.OnChat("[RynthNav] goto: arrived");
            Check(ai[^1].StartsWith("handin Warden") && ai[^1].EndsWith("| Acid Axe"), "acid axe: shown to the Warden (given; he hands it back): " + ai[^1]);
            run.Stop();

            // Picked up by hand before the step: RynthAi says it is carried, and the run goes on.
            ai.Clear(); nav.Clear();
            run.Start(q, _ => false, loot: false);
            run.OnChat("[RynthNav] goto: arrived");
            run.OnChat("[RynthAi] Quest: got Acid Axe (already in your pack).");
            Check(run.Step == 2, "acid axe: already carried: the step is done: step " + (run.Step + 1));
            run.Stop();

            // A got line for another item, or a stamped step with items: the stamp still decides.
            var k = new GuideQuest { Title = "Key Run" };
            k.Steps.Add(new GuideStep { Text = "Pick up the Old Key on the table at 1.0N, 1.0E.", Coords = new List<string> { "1.0N, 1.0E" }, Items = new List<string> { "Old Key", "Old Note" } });
            k.Steps.Add(new GuideStep { Text = "Kill the rat and loot the Rat Tail.", Items = new List<string> { "Rat Tail" }, Stamps = new List<string> { "RatTail" } });
            ai.Clear(); nav.Clear();
            run.Start(k, _ => false, loot: false);
            Check(ai.Count == 1 && ai[0] == "have Old Key | Old Note", "items: asked at the start too (picked up earlier): " + string.Join(" | ", ai));
            run.OnChat("[RynthAi] Quest: got Old Key.");
            Check(run.Step == 0, "items: one of two: not done yet");
            run.OnChat("[RynthAi] Quest: got Old Note (Quest Item).");
            Check(run.Step == 1 && nav.Contains("stop"), "items: both: done, RynthNav stopped on its way: " + string.Join(" | ", nav));
            run.OnChat("[RynthAi] Quest: got Rat Tail.");
            Check(run.Step == 2 || !run.Active, "items: the last item got ends a step with a stamp too (2026-11-10)");
            run.Stop();

            // 2026-11-10: "when a quest pickups the last loot item it should move to the next step vs keep fighting
            // even if the dungeon". The loot step inside, then the hand-in outside: the walk stops, RynthNav goes.
            var jaw = new GuideQuest { Title = "Jaw Run" };
            jaw.Steps.Add(new GuideStep { Text = "Enter the Eater Pit at 10.0N, 10.0E.", Coords = new List<string> { "10.0N, 10.0E" } });
            jaw.Steps.Add(new GuideStep { Text = "Kill Abhorrent Eaters and loot an Abhorrent Eater Jaw.", Items = new List<string> { "Abhorrent Eater Jaw" }, Stamps = new List<string> { "EaterJaw" } });
            jaw.Steps.Add(new GuideStep { Text = "Return to Xsao Lann at 20.0N, 20.0E and hand her the jaw.", Coords = new List<string> { "20.0N, 20.0E" } });
            Check(QuestRunner.HappensOutside(jaw, 2) && !QuestRunner.HappensOutside(jaw, 1), "outside: the return, not the loot step");
            ai.Clear(); nav.Clear();
            run.OnWhere(false, 0);
            run.Start(jaw, _ => false, loot: false);
            run.OnChat("[RynthNav] goto: arrived");
            run.OnWhere(true, 0x1234);
            Check(run.Step == 1 && ai.Exists(l => l.StartsWith("dungeon patrol")), "in: patrolling for the jaw: " + string.Join(" | ", ai));
            nav.Clear();
            run.OnChat("[RynthAi] Quest: got Abhorrent Eater Jaw.");
            Check(run.Step == 2, "the jaw got: on to the return");
            Check(ai[^1] == "dungeon stop" || ai.Contains("dungeon stop"), "the dungeon walk stops: " + string.Join(" | ", ai));
            Check(nav.Count > 0 && nav[^1] == "go 20.0N, 20.0E", "RynthNav goes to Xsao: " + string.Join(" | ", nav));
            run.Stop();

            // 2026-11-10: "the route isn't going in the portal like it should". A travel step to the portal's spot,
            // the next step inside: the portal right there is taken, and getting in ends the step.
            var pit = new GuideQuest { Title = "Pit Run" };
            pit.Steps.Add(new GuideStep { Text = "Travel to 30.0N, 30.0E.", Coords = new List<string> { "30.0N, 30.0E" } });
            pit.Steps.Add(new GuideStep { Text = "Kill the Pit Lord and loot the Pit Key.", Items = new List<string> { "Pit Key" } });
            ai.Clear(); nav.Clear();
            run.OnWhere(false, 0);
            run.Start(pit, _ => false, loot: false);
            run.OnChat("[RynthNav] goto: arrived");
            Check(run.Step == 0 && ai.Exists(l => l.StartsWith("dungeon enter near")), "at the spot: the portal right there: " + string.Join(" | ", ai));
            run.OnWhere(true, 0x2222);
            Check(run.Step == 1, "in: the travel step is done");
            run.Stop();
            // No portal there: being there is the step, as before.
            ai.Clear(); nav.Clear();
            run.OnWhere(false, 0);
            run.Start(pit, _ => false, loot: false);
            run.OnChat("[RynthNav] goto: arrived");
            run.OnChat("[RynthAi] Quest: no portal right here.");
            Check(run.Step == 1, "no portal: the travel step is done");
            run.Stop();
        }
    }
}
