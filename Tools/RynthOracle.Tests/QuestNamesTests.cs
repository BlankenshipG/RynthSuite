using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;

namespace RynthOracle.Tests
{
    // Who and where a wiki step names (2026-11-07, Silentkelpie: "it should read the quest for names to
    // build a route to if coords are not present, and talk to npc in question. it should also hand quest
    // items back to the npc"). Two quests as the guide has them.
    internal static partial class Program
    {
        private static GuideStep Step(string text, params string[] items) => new() { Text = text, Items = new List<string>(items) };

        private static void QuestNamesTests()
        {
            var lost = new GuideQuest { Title = "Lost Pet", StartsWith = "Xsao Lann", StartsAt = "1.5N, 71.6W in Fort Tethana" };
            lost.Steps.Add(Step("Talk to Xsao Lann in Fort Tethana."));
            lost.Steps.Add(new GuideStep { Text = "Run to Creepy Canyons at 4.6N, 89.5W.", Coords = new List<string> { "4.6N, 89.5W" } });
            lost.Steps.Add(Step("Kill Darling and loot Darling's Collar (Quest Item).", "Darling's Collar (Quest Item)"));
            lost.Steps.Add(Step("Return the collar to Xsao for your rewards."));
            lost.Loot.Add("Darling's Collar (Quest Item)");

            string J(List<string> l) => string.Join(" || ", l);
            Check(J(QuestNames.Candidates(lost, lost.Steps[0], firstStep: true)) == "Xsao Lann || Fort Tethana || 1.5N, 71.6W in Fort Tethana",
                "names: 'Talk to Xsao Lann in Fort Tethana': the person, the place, then where the quest starts (not split at its comma): " + J(QuestNames.Candidates(lost, lost.Steps[0], true)));
            Check(QuestNames.IsTalk(lost, lost.Steps[0]) && !QuestNames.IsHandIn(lost.Steps[0]), "names: 'Talk to': talk, no hand-in");
            Check(J(QuestNames.Candidates(lost, lost.Steps[2])) == "Darling", "names: 'Kill Darling and loot Darling's Collar': the item's name isn't a name");
            Check(!QuestNames.IsTalk(lost, lost.Steps[2]) && !QuestNames.IsHandIn(lost.Steps[2]), "names: a kill step talks to nobody");
            Check(J(QuestNames.Candidates(lost, lost.Steps[3])) == "Xsao Lann", "names: 'Return the collar to Xsao': the quest's person in full");
            Check(QuestNames.IsHandIn(lost.Steps[3]) && J(QuestNames.HandInItems(lost, 3)) == "Darling's Collar (Quest Item)",
                "names: 'Return the collar': a hand-in of the collar got before it");
            Check(QuestNames.HandInItems(lost, 1).Count == 0, "names: before the collar is looted there is nothing to hand in");

            var note = new GuideQuest { Title = "Note of Congratulation", StartsWith = "Nasun ibn Tifar / Ahyara", StartsAt = "North Yaraq Outpost / East Yaraq Outpost" };
            note.Steps.Add(Step("From Yaraq follow the paths either north to the North Yaraq Outpost or east to the East Yaraq Outpost."));
            note.Steps.Add(Step("In the north outpost you will find Nasun ibn Tifar who will give you a Congratulations (Nasun ibn Tifar) note to be signed by Ahyara in the east outpost.", "Congratulations (Nasun ibn Tifar)"));
            note.Steps.Add(Step("Take the note to Ahyara and she will sign it and hand you Congratulations (Nasun ibn Tifar & Ahyara).", "Congratulations (Nasun ibn Tifar & Ahyara)"));
            note.Steps.Add(new GuideStep { Text = "Head back to Yaraq and find Mara al-Luq, she is in the Cerulian Cove tavern at 21.7S, 1.6W.", Coords = new List<string> { "21.7S, 1.6W" } });
            note.Steps.Add(Step("Hand her each note of congratulation to receive Thank You Note (Ahyara) and Thank You Note (Nasun ibn Tifar)", "Thank You Note (Ahyara)", "Thank You Note (Nasun ibn Tifar)"));
            note.Loot.Add("Congratulations (Nasun ibn Tifar)");

            Check(J(QuestNames.Candidates(note, note.Steps[0], true)) == "North Yaraq Outpost || East Yaraq Outpost",
                "names: 'From Yaraq follow the paths ... to the North Yaraq Outpost': where it goes, not where it starts from: " + J(QuestNames.Candidates(note, note.Steps[0], true)));
            Check(J(QuestNames.Candidates(note, note.Steps[1])) == "Nasun ibn Tifar || Ahyara" && QuestNames.IsTalk(note, note.Steps[1]),
                "names: 'you will find Nasun ibn Tifar': 'ibn' joins the name; talk");
            Check(J(QuestNames.Candidates(note, note.Steps[2])) == "Ahyara",
                "names: a person named only inside an item's name ('Congratulations (Nasun ibn Tifar & Ahyara)') isn't the step's: " + J(QuestNames.Candidates(note, note.Steps[2])));
            Check(QuestNames.IsHandIn(note.Steps[2]) && J(QuestNames.HandInItems(note, 2)) == "Congratulations (Nasun ibn Tifar)",
                "names: 'Take the note to Ahyara ... hand you X': hands over Nasun's note, not the one it gets back");
            Check(J(QuestNames.Candidates(note, note.Steps[3])).Contains("Mara al-Luq"), "names: 'al-' joins the name (Mara al-Luq)");
            Check(QuestNames.Candidates(note, note.Steps[4]).Count == 0 && J(QuestNames.TargetsAt(note, 4)).Contains("Mara al-Luq"),
                "names: 'Hand her each note': nobody named, so the person of the step before");
            Check(QuestNames.IsHandIn(note.Steps[4]) && !QuestNames.IsHandIn(Step("She will hand you a note.")), "names: 'hand you' is getting, not a hand-in");
        }
    }
}
