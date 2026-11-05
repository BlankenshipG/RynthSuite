using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

// The quest loot list (2026-11-01): items to pick up by name, on top of the loot profile, for a quest being
// run. RynthOracle's "Run this quest" / "Loot map" puts the wiki quest's items here (the {{Itemlink}} items
// its steps kill or loot for) through the plugin-command broker ("questloot", "add <name>") and takes them
// off when the run ends; "/ra questloot add|remove|clear|list|on|off" does the same by hand. Kept for the
// session only (the loot profile is where lasting rules go). Checked first in ClassifyLootItem
// (CorpseOpenController.cs), so corpses and the ground both take them. Pump thread.
internal static class QuestLootList
{
    public const string RuleLabel = "quest loot";
    public const int MaxItems = 64;

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list applies (on by default; "/ra questloot off" pauses it without forgetting it).</summary>
    public static bool Enabled = true;

    public static int Count => Names.Count;
    public static IEnumerable<string> Items => Names;

    /// <summary>The item is on the list (its exact name, any case) and the list is on.</summary>
    public static bool Wants(string? itemName) =>
        Enabled && Names.Count > 0 && !string.IsNullOrWhiteSpace(itemName) && Names.Contains(itemName.Trim());

    public static void Clear() => Names.Clear();

    /// <summary>"add Shiny Red Stone", "remove X", "clear", "list", "on", "off": what to say, and whether it worked.</summary>
    public static (bool Ok, string Text) Apply(string line)
    {
        line = (line ?? "").Trim();
        int sp = line.IndexOf(' ');
        string verb = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
        string name = sp < 0 ? "" : line.Substring(sp + 1).Trim().Trim('"').Trim();
        switch (verb)
        {
            case "add":
                if (name.Length == 0) return (false, "[RynthAi] /ra questloot add <exact item name>");
                if (Names.Count >= MaxItems && !Names.Contains(name)) return (false, $"[RynthAi] The quest loot list is full ({MaxItems} items): /ra questloot clear.");
                return Names.Add(name) ? (true, $"[RynthAi] Quest loot: picking up '{name}' ({Names.Count} on the list).") : (true, $"[RynthAi] Quest loot: '{name}' is on the list already.");
            case "remove":
            case "del":
                return Names.Remove(name) ? (true, $"[RynthAi] Quest loot: '{name}' off the list ({Names.Count} left).") : (false, $"[RynthAi] Quest loot: '{name}' isn't on the list.");
            case "clear":
                int n = Names.Count;
                Names.Clear();
                return (true, $"[RynthAi] Quest loot list cleared ({n} items).");
            case "on":
            case "off":
                Enabled = verb == "on";
                return (true, $"[RynthAi] Quest loot list {(Enabled ? "on" : "off")} ({Names.Count} items).");
            case "":
            case "list":
                return (true, Names.Count == 0
                    ? "[RynthAi] The quest loot list is empty. /ra questloot add <name> (RynthOracle's Run this quest fills it)."
                    : $"[RynthAi] Quest loot ({(Enabled ? "on" : "off")}): " + string.Join(", ", Names));
            default:
                return (false, "[RynthAi] /ra questloot add <name> | remove <name> | clear | list | on | off | profile [off]");
        }
    }
}
