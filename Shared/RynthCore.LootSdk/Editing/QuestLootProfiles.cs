using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RynthCore.Loot.VTank;

namespace RynthCore.Loot.Editing;

/// <summary>
/// Quest loot profiles (2026-11-05): a loot profile per wiki quest, built from the player's template
/// (LootProfiles\base-quest-dynamic.utl) and the quest's items (RynthOracle's quest guide: the
/// {{Itemlink}} items its steps kill or loot for). Each is the template's rules with one Keep rule
/// per quest item in front ("Quest: Shiny Red Stone", the exact name), so the quest's items are kept
/// before anything the template does with them. Written to LootProfiles\Quests\&lt;quest&gt;.utl and
/// built again each time (an edit to the template reaches every quest's profile). Host-free.
/// </summary>
public static class QuestLootProfiles
{
    /// <summary>The template's file name, in the loot profile folder.</summary>
    public const string TemplateFileName = "base-quest-dynamic.utl";
    /// <summary>The quest profiles' folder, under the loot profile folder.</summary>
    public const string FolderName = "Quests";
    /// <summary>The start of a quest item's rule name (a template rule never gets one).</summary>
    public const string RulePrefix = "Quest: ";
    /// <summary>Items one profile takes at most.</summary>
    public const int MaxItems = 64;

    /// <summary>The quest's profile file name: its title made safe for a file, ".utl".</summary>
    public static string FileNameFor(string questTitle)
    {
        var sb = new StringBuilder();
        char[] bad = Path.GetInvalidFileNameChars();
        foreach (char ch in (questTitle ?? "").Trim())
        {
            if (ch < ' ' || Array.IndexOf(bad, ch) >= 0 || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
                sb.Append('_');
            else sb.Append(ch);
        }
        string name = sb.ToString().Trim().TrimEnd('.');
        if (name.Length == 0) name = "quest";
        if (name.Length > 120) name = name.Substring(0, 120).TrimEnd();
        return name + ".utl";
    }

    /// <summary>
    /// The item's name rule pattern: the exact name, any case. A wiki note in brackets at the end
    /// ("Shard of Harraag's Dagger (Quest Item)") may or may not be part of the name in the game, so
    /// the name matches with and without it.
    /// </summary>
    public static string PatternFor(string item)
    {
        string name = OneLine(item);
        int open = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && name.EndsWith(")", StringComparison.Ordinal))
        {
            string head = name.Substring(0, open);
            string note = name.Substring(open);
            return "^" + LootItemRules.EscapeText(head) + "(" + LootItemRules.EscapeText(note) + ")?$";
        }
        return LootItemRules.ExactPattern(name);
    }

    /// <summary>The template's rules with a Keep rule per item in front (the template is not changed).</summary>
    public static VTankLootProfile Build(VTankLootProfile? template, IEnumerable<string> items)
    {
        VTankLootProfile p = template != null
            ? VTankLootParser.LoadFromText(VTankLootWriter.Serialize(template))   // a copy
            : new VTankLootProfile();
        p.FileVersion = Math.Max(1, p.FileVersion);
        p.Rules.RemoveAll(r => r.Name.StartsWith(RulePrefix, StringComparison.Ordinal));   // a template saved from a quest profile

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<VTankLootRule>();
        foreach (string raw in items)
        {
            string item = OneLine(raw);
            if (item.Length == 0 || !seen.Add(item)) continue;
            if (rules.Count >= MaxItems) break;
            var rule = new VTankLootRule
            {
                Name = RulePrefix + item,
                CustomExpression = "",
                Priority = 0,
                Action = VTankLootAction.Keep,
            };
            rule.Conditions.Add(new VTankLootCondition(VTankNodeTypes.StringValueMatch, "0", new[] { PatternFor(item), "1" }));   // key 1: the name
            rules.Add(rule);
        }
        p.Rules.InsertRange(0, rules);
        p.DeclaredRuleCount = p.Rules.Count;
        return p;
    }

    /// <summary>
    /// Builds the quest's profile and writes it to <paramref name="lootFolder"/>\Quests. The template is
    /// <paramref name="lootFolder"/>\base-quest-dynamic.utl (none: the quest's rules only, and
    /// <paramref name="note"/> says so). Returns the file written.
    /// </summary>
    public static string Write(string lootFolder, string questTitle, IReadOnlyList<string> items, out int rules, out string note)
    {
        note = "";
        VTankLootProfile? template = null;
        string templatePath = Path.Combine(lootFolder, TemplateFileName);
        if (File.Exists(templatePath)) template = VTankLootParser.Load(templatePath);
        else note = $"no {TemplateFileName} in the loot profile folder: the quest's items only";

        VTankLootProfile p = Build(template, items);
        rules = 0;
        foreach (VTankLootRule r in p.Rules)
            if (r.Name.StartsWith(RulePrefix, StringComparison.Ordinal)) rules++;

        string folder = Path.Combine(lootFolder, FolderName);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FileNameFor(questTitle));
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, VTankLootWriter.Serialize(p), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>True when <paramref name="path"/> is a quest profile (in the Quests folder).</summary>
    public static bool IsQuestProfile(string? path, string lootFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string dir = Path.GetFullPath(Path.GetDirectoryName(path.Trim().Trim('"')) ?? "");
            string quests = Path.GetFullPath(Path.Combine(lootFolder, FolderName));
            return string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), quests.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string OneLine(string? s) => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
}
