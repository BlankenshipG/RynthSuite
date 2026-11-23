using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

// One hand-in for every view under Quests (2026-11-11, Silentkelpie: "make sure all tabs under quests support the
// same function to hand the quest item in after quest completion step"). The wiki guide's quests (All, Story,
// Dailies, Weekly, QB, ILT Wiki), the master list's quests in the guide and the flag list, and the Legendary
// rows all get "Go and hand in": the quest run from its hand-in step (RynthNav to the person, then RynthAi's
// "questloot handin" of the quest's items there, through the give queue), and "Hand in here" when you're
// standing by them. A quest whose wiki page has no hand-in step but whose rewards are for handing something in
// ("For Hand in Female Tusker Tusk:") hands it to the person it starts with. The Dynamic Stamps (the flag list
// and the guide's Stamps tab) use Go and hand in's Parchment (DynamicStampsTab.cs); the Temple guardians,
// RynthILT's hand-in; the markers have nothing to hand in.
internal sealed partial class OracleView
{
    /// <summary>The wiki quest a master-list quest or a Legendary row is: one of its stamps is the flag, or the same title.</summary>
    private GuideQuest? GuideFor(string flag, params string[] titles)
    {
        List<GuideQuest> quests = _c.Wiki.Quests;
        if (quests.Count == 0) return null;
        string key = flag.Length > 0 ? GuideStamp.Key(flag) : "";
        if (key.Length > 0)
            foreach (GuideQuest q in quests)
                foreach (string st in q.AllStamps())
                    if (GuideStamp.Key(st) == key) return q;
        foreach (string t in titles)
            if (t.Length > 0 && _c.FindWiki(t) is GuideQuest byTitle) return byTitle;
        return null;
    }

    /// <summary>
    /// What hands the quest in: the quest itself and its last hand-in step, or a one-step quest made from its
    /// rewards' items and the person it starts with. Null when neither is known. Internal for the tests.
    /// </summary>
    internal static (GuideQuest Quest, int Step)? HandInOf(GuideQuest q)
    {
        for (int i = q.Steps.Count - 1; i >= 0; i--)
            if (QuestNames.IsHandIn(q.Steps[i])) return (q, i);
        List<string> items = QuestNames.RewardItems(q);
        foreach (string it in q.Loot)
            if (!items.Exists(x => x.Equals(it, StringComparison.OrdinalIgnoreCase))) items.Add(it);
        string who = q.StartsWith.Trim();
        if (items.Count == 0 || who.Length == 0) return null;
        var hand = new GuideQuest { Title = q.Title, Url = q.Url, StartsWith = q.StartsWith, StartsAt = q.StartsAt };
        hand.Loot.AddRange(items);
        hand.Steps.Add(new GuideStep { Text = $"Return to {who} and hand in the {string.Join(", ", items)}." });
        return (hand, 0);
    }

    /// <summary>"Go and hand in" and "Hand in here" for <paramref name="q"/> (nothing known: says so). <paramref name="id"/> keeps the ids apart.</summary>
    private void DrawHandIn(UiWindow w, GuideQuest? q, string id, Action<string> say)
    {
        if (q == null)
        {
            w.TextDisabled("Hand-in: no wiki guide for this quest, so who takes the items isn't known.");
            return;
        }
        if (HandInOf(q) is not { } h)
        {
            w.TextDisabled("Hand-in: the wiki's steps don't hand anything in.");
            return;
        }
        List<string> who = QuestNames.TargetsAt(h.Quest, h.Step);
        List<string> items = QuestNames.HandInItems(h.Quest, h.Step);
        string whoText = who.Count > 0 ? string.Join(" or ", who) : "the person of the step before";
        string itemText = items.Count > 0 ? string.Join(", ", items) : "the quest's items";
        QuestRunner? run = _c.Runner;
        bool runningThis = run?.Quest is GuideQuest rq && rq.Title == h.Quest.Title && rq.Steps.Count == h.Quest.Steps.Count;
        w.Button("Go and hand in", id + ".handin.go", () =>
        {
            if (_c.Runner == null) { say("The quest run isn't ready."); return; }
            if (!_c.InWorld) { say("Log in first."); return; }
            if (runningThis) _c.Runner.GoTo(h.Step);
            else _c.Runner.Start(h.Quest, st => StateOf(h.Quest, st) == StampState.Done, loot: false, fromStep: h.Step, society: Standing());
            say($"Handing {itemText} to {whoText}: RynthNav goes there, RynthAi gives them over (step {h.Step + 1}).");
        });
        w.Tooltip($"Step {h.Step + 1}: {h.Quest.Steps[h.Step].Text}\nRynthNav goes to {whoText} (outdoors, a dungeon or the Bur), then RynthAi hands over:\n{itemText}");
        w.SameLine();
        w.Button("Hand in here", id + ".handin.here", () =>
        {
            if (who.Count == 0) { say("The step doesn't name who takes them: use Go and hand in."); return; }
            string line = "handin " + string.Join(" || ", who) + (items.Count > 0 ? " | " + string.Join(" | ", items.ConvertAll(i => i.Replace('|', '/'))) : "");
            say(_c.SendAi("questloot", line) ? $"RynthAi hands {itemText} to {whoText} near you." : "RynthAi isn't loaded (it does the give).");
        });
        w.Tooltip($"You're standing by them: RynthAi gives {itemText} to {whoText} (within 60 m).");
    }
}
