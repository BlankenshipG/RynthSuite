// Quests an NPC gives (2026-11-08, Silentkelpie: "when one clicks on a npc and they talk back check to see if
// their text is the start of a quest and ask if you want to do that quest"). An NPC talking to the character
// ("Xsao Lann tells you, \"...\"", "Warden says, \"...\"") is matched against the wiki guide's quests by who
// they start with (GuideQuest.StartsWith); the quests it starts are offered (the RynthOracle quest offer
// window), the ones whose words the NPC's text shares first. Host-free: the tests drive it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

internal static class QuestOffers
{
    /// <summary>Quests one offer lists at most.</summary>
    public const int MaxQuests = 4;

    // "Xsao Lann tells you, "..."", "Warden says, "..."" (an NPC's Tell and Say emotes).
    private static readonly Regex NpcLineRx = new(
        @"^(?<name>[A-Z\""][^,\""]{1,60}?)\s+(?:tells\s+you|says),\s*\""(?<text>.*)\""\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>An NPC talking to the character: who and what. False for anything else (and for "You say").</summary>
    public static bool TryNpcLine(string? line, out string name, out string text)
    {
        name = text = "";
        Match m = NpcLineRx.Match((line ?? "").Trim());
        if (!m.Success) return false;
        name = m.Groups["name"].Value.Trim();
        text = m.Groups["text"].Value.Trim();
        if (name.Equals("You", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Length >= 2;
    }

    /// <summary>
    /// The guide's quests <paramref name="npc"/> starts (one of the people its "starts with" names, as the game
    /// writes them: quotes and a note in brackets left off), the ones the NPC's <paramref name="text"/> shares
    /// most words with first (its title, overview and first step).
    /// </summary>
    public static List<GuideQuest> StartedBy(IEnumerable<GuideQuest> quests, string npc, string text)
    {
        string who = Norm(npc);
        var found = new List<(GuideQuest Q, int Score, int Order)>();
        int order = 0;
        if (who.Length < 2) return new List<GuideQuest>();
        foreach (GuideQuest q in quests)
        {
            order++;
            bool starts = false;
            foreach (string person in QuestNames.Split(q.StartsWith))
                if (Norm(person) == who) { starts = true; break; }
            if (!starts) continue;
            found.Add((q, Overlap(text, q), order));
        }
        return found.OrderByDescending(f => f.Score).ThenBy(f => f.Order).Select(f => f.Q).ToList();
    }

    /// <summary>How many of the quest's words (4 letters or more) the NPC's text has.</summary>
    internal static int Overlap(string text, GuideQuest q)
    {
        var said = Words(text);
        if (said.Count == 0) return 0;
        string about = q.Title + " " + q.Overview + " " + (q.Steps.Count > 0 ? q.Steps[0].Text : "");
        int n = 0;
        foreach (string w in Words(about)) if (said.Contains(w)) n++;
        return n;
    }

    private static HashSet<string> Words(string s) =>
        new(Regex.Split(s ?? "", @"[^\p{L}']+").Where(w => w.Length >= 4 && w != "quest"), StringComparer.OrdinalIgnoreCase);

    private static string Norm(string s) =>
        Regex.Replace(Regex.Replace(s ?? "", @"[\""“”]", ""), @"\s+", " ").Trim().ToLowerInvariant();
}
