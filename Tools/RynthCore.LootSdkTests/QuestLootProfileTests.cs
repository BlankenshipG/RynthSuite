using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.Loot.Editing;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

// Quest loot profiles (2026-11-05): base-quest-dynamic.utl plus a Keep rule per quest item, in front.
internal static partial class Program
{
    // The player's template as he made it (no rules, the salvage combine settings), and one with a rule.
    private const string EmptyTemplate = "UTL\n1\n0\nSalvageCombine\n0\n1\n1-6, 7-8, 9, 10\n0\n0\n";
    private const string RareTemplate = "UTL\n1\n1\n(*) Rare\n\n0;1;12\n18\n23308\n218103850\nSalvageCombine\n0\n1\n1-6, 7-8, 9, 10\n0\n0\n";

    private static void TestQuestLootProfiles()
    {
        Console.WriteLine("\n-- Quest loot profiles --");
        bool M(string pattern, string name) => Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase);

        // Patterns: the exact name, any case; a wiki note in brackets is optional.
        string p = QuestLootProfiles.PatternFor("Vial of The Architect's Blood");
        Check(M(p, "Vial of The Architect's Blood") && M(p, "vial of the architect's blood") && !M(p, "Vial of The Architect's Blood x2") && !M(p, "Empty Vial of The Architect's Blood"), "pattern: exact name: " + p);
        string n = QuestLootProfiles.PatternFor("Shard of Harraag's Dagger (Quest Item)");
        Check(M(n, "Shard of Harraag's Dagger") && M(n, "Shard of Harraag's Dagger (Quest Item)") && !M(n, "Shard of Harraag's Dagger (Other)"), "pattern: a note in brackets is optional: " + n);
        Check(M(QuestLootProfiles.PatternFor("Pyreal Mote [x5]."), "Pyreal Mote [x5].") && !M(QuestLootProfiles.PatternFor("A.B"), "AxB"), "pattern: regex characters escaped");

        // Build: the quest's rules first, then the template's; the template itself unchanged; duplicates once.
        VTankLootProfile rare = VTankLootParser.LoadFromText(RareTemplate);
        VTankLootProfile q = QuestLootProfiles.Build(rare, new[] { "Shiny Red Stone", "Vial of The Architect's Blood", "shiny red stone", " " });
        Eq(q.Rules.Count, 3, "build: two quest rules and the template's one");
        Check(q.Rules[0].Name == "Quest: Shiny Red Stone" && q.Rules[1].Name == "Quest: Vial of The Architect's Blood" && q.Rules[2].Name == "(*) Rare", "build: quest rules in front, in order");
        Check(q.Rules[0].Action == VTankLootAction.Keep && q.Rules[0].Conditions.Count == 1 && q.Rules[0].Conditions[0].NodeType == VTankNodeTypes.StringValueMatch
              && q.Rules[0].Conditions[0].DataLines.SequenceEqual(new[] { "^Shiny Red Stone$", "1" }), "build: Keep, the name (string key 1)");
        Eq(rare.Rules.Count, 1, "build: the template is not changed");
        Check(q.SalvageCombine != null && q.SalvageCombine.DefaultBands == "1-6, 7-8, 9, 10", "build: the template's salvage combine kept");

        // The file parses back the same, and building from a quest profile doesn't pile up quest rules.
        string text = VTankLootWriter.Serialize(q);
        VTankLootProfile back = VTankLootParser.LoadFromText(text);
        Check(back.Rules.Count == 3 && VTankLootWriter.Serialize(back) == text, "file: round-trips");
        Eq(QuestLootProfiles.Build(back, new[] { "Other Item" }).Rules.Count, 2, "build: a template saved from a quest profile loses its old quest rules");
        Eq(QuestLootProfiles.Build(VTankLootParser.LoadFromText(EmptyTemplate), new[] { "A" }).Rules.Count, 1, "build: the empty template gives the quest's rules only");
        Eq(QuestLootProfiles.Build(null, new[] { "A", "B" }).Rules.Count, 2, "build: no template, the quest's rules only");

        // File names.
        Eq(QuestLootProfiles.FileNameFor("Aerbax's Prodigal Human"), "Aerbax's Prodigal Human.utl", "file name: kept");
        Eq(QuestLootProfiles.FileNameFor("\"Pistol\" Pete: Trophy/Hunts?"), "_Pistol_ Pete_ Trophy_Hunts_.utl", "file name: characters a file can't have");
        Eq(QuestLootProfiles.FileNameFor("  "), "quest.utl", "file name: an empty title");

        // Write: to <folder>\Quests from <folder>\base-quest-dynamic.utl.
        string dir = Path.Combine(Path.GetTempPath(), "QuestLootProfileTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = QuestLootProfiles.Write(dir, "Mistreated Virindi", new[] { "Shiny Red Stone" }, out int rules, out string note);
            Check(File.Exists(path) && rules == 1 && note.Contains("base-quest-dynamic.utl"), "write: no template, said so: " + note);
            File.WriteAllText(Path.Combine(dir, QuestLootProfiles.TemplateFileName), RareTemplate);
            path = QuestLootProfiles.Write(dir, "Mistreated Virindi", new[] { "Shiny Red Stone", "Vial" }, out rules, out note);
            VTankLootProfile w = VTankLootParser.Load(path);
            Check(path == Path.Combine(dir, "Quests", "Mistreated Virindi.utl") && rules == 2 && note == "" && w.Rules.Count == 3 && w.Rules[2].Name == "(*) Rare",
                "write: the template's rules after the quest's, built again over the last one");
            Check(QuestLootProfiles.IsQuestProfile(path, dir) && !QuestLootProfiles.IsQuestProfile(Path.Combine(dir, "Base.utl"), dir) && !QuestLootProfiles.IsQuestProfile("", dir),
                "a quest profile is one in the Quests folder");
            Check(!Directory.GetFiles(Path.Combine(dir, "Quests"), "*.tmp").Any(), "write: no temp file left");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
