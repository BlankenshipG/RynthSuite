using System;
using System.Collections.Generic;
using RynthCore.Loot.Editing;

namespace RynthCore.Plugin.RynthAi;

// Quest loot profiles (2026-11-05): a loot profile per wiki quest, built from LootProfiles\base-quest-dynamic.utl
// and the quest's items (RynthCore.LootSdk QuestLootProfiles). RynthOracle's quest guide sends them through the
// "questloot" broker action, as it does the quest loot list:
//   "build <quest>|<item>|<item>..."    writes LootProfiles\Quests\<quest>.utl
//   "profile <quest>|<item>|<item>..."  writes it and makes it the loot profile in use (Run this quest)
//   "profile off"                       back to the loot profile that was in use before
// "/ra questloot profile off" does the last by hand. Pump thread.
public sealed partial class RynthAiPlugin
{
    // The loot profile in use before a quest's profile took over ("" for none); null while no quest profile is in use.
    private string? _questProfilePrevious;
    private string _questProfileQuest = "";
    /// <summary>The loot profile folder (RynthAi's LootProfiles); the tests point it at a temp folder.</summary>
    internal static string QuestLootFolder = Loot.LootEditorBridge.LootFolder;

    /// <summary>A quest loot profile verb's answer, or null when <paramref name="line"/> isn't one (the quest loot list's).</summary>
    private (bool Ok, string Text)? TryQuestLootProfile(string line)
    {
        line = (line ?? "").Trim();
        int sp = line.IndexOf(' ');
        string verb = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
        string rest = sp < 0 ? "" : line.Substring(sp + 1).Trim();
        if (verb is not ("build" or "profile")) return null;

        if (verb == "profile" && (rest.Length == 0 || rest.Equals("off", StringComparison.OrdinalIgnoreCase)))
            return rest.Length == 0 ? QuestProfileStatus() : QuestProfileOff();

        string[] parts = rest.Split('|');
        string quest = parts[0].Trim();
        var items = new List<string>();
        for (int i = 1; i < parts.Length; i++)
            if (parts[i].Trim().Length > 0) items.Add(parts[i].Trim());
        if (quest.Length == 0 || items.Count == 0)
            return (false, $"[RynthAi] questloot {verb} <quest>|<item>|<item>... (RynthOracle's quest guide sends these)");

        string path;
        int rules;
        string note;
        try { path = QuestLootProfiles.Write(QuestLootFolder, quest, items, out rules, out note); }
        catch (Exception ex) { return (false, $"[RynthAi] Quest loot profile for {quest}: not written ({ex.Message})."); }
        string file = System.IO.Path.GetFileName(path);
        string extra = note.Length > 0 ? $" ({note})" : "";
        if (verb == "build")
            return (true, $"[RynthAi] Quest loot profile written: Quests\\{file}, {rules} quest item(s){extra}.");

        var settings = _dashboard?.Settings;
        if (settings == null) return (false, "[RynthAi] Quest loot profile: RynthAi's settings aren't loaded yet.");
        string current = (settings.CurrentLootPath ?? "").Trim().Trim('"');
        // The first quest profile remembers what was in use; switching quests keeps that.
        if (_questProfilePrevious == null && !QuestLootProfiles.IsQuestProfile(current, QuestLootFolder))
            _questProfilePrevious = current;
        _questProfilePrevious ??= "";
        _questProfileQuest = quest;
        settings.CurrentLootPath = path;
        string back = _questProfilePrevious.Length > 0 ? System.IO.Path.GetFileName(_questProfilePrevious) : "no loot profile";
        return (true, $"[RynthAi] Loot profile for {quest}: Quests\\{file} ({rules} quest item(s) and {QuestTemplateName()}){extra}. "
                      + $"Back to {back} when the run ends, or /ra questloot profile off.");
    }

    private (bool Ok, string Text) QuestProfileOff()
    {
        var settings = _dashboard?.Settings;
        if (settings == null) return (false, "[RynthAi] Quest loot profile: RynthAi's settings aren't loaded yet.");
        string current = (settings.CurrentLootPath ?? "").Trim().Trim('"');
        if (_questProfilePrevious == null)
        {
            return QuestLootProfiles.IsQuestProfile(current, QuestLootFolder)
                ? (false, $"[RynthAi] The loot profile is a quest's ({System.IO.Path.GetFileName(current)}), but which one was in use before isn't known (a restart since): pick one in the dashboard.")
                : (true, "[RynthAi] No quest loot profile is in use.");
        }
        string previous = _questProfilePrevious;
        string quest = _questProfileQuest;
        _questProfilePrevious = null;
        _questProfileQuest = "";
        // Only put it back while the quest's profile is still the one in use (the player may have picked another).
        if (!QuestLootProfiles.IsQuestProfile(current, QuestLootFolder))
            return (true, $"[RynthAi] Quest loot profile for {quest} ended; the loot profile in use ({System.IO.Path.GetFileName(current)}) was picked since, so it stays.");
        settings.CurrentLootPath = previous;
        return (true, previous.Length > 0
            ? $"[RynthAi] Quest loot profile for {quest} ended: back to {System.IO.Path.GetFileName(previous)}."
            : $"[RynthAi] Quest loot profile for {quest} ended: no loot profile, as before.");
    }

    private (bool Ok, string Text) QuestProfileStatus()
    {
        string current = (_dashboard?.Settings?.CurrentLootPath ?? "").Trim().Trim('"');
        if (_questProfilePrevious != null)
            return (true, $"[RynthAi] Quest loot profile in use: {System.IO.Path.GetFileName(current)} ({_questProfileQuest}); /ra questloot profile off goes back.");
        return (true, $"[RynthAi] No quest loot profile in use. RynthOracle's Run this quest builds one from {QuestTemplateName()} (/ro lootprofiles builds every quest's).");
    }

    private static string QuestTemplateName() => System.IO.Path.GetFileNameWithoutExtension(QuestLootProfiles.TemplateFileName);
}
