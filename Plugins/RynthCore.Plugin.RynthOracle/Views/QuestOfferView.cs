using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

// The quest offer (2026-11-08): an NPC who starts quests in the wiki guide talks to the character, and a small
// window asks whether to run one (QuestOffers). Not for a quest being run, a one-time quest already done, one
// still on its timer, or one turned down this session ("Not now"); the same NPC is asked about once in
// OfferRepeatMs.
internal sealed partial class OracleView
{
    private const long OfferRepeatMs = 10 * 60 * 1000;
    private readonly Dictionary<string, long> _offeredAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offerDeclined = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GuideQuest> _offer = new();
    private string _offerNpc = "", _offerText = "";

    /// <summary>Asks the plugin to show or hide the offer window, and to open the main window.</summary>
    public Action<bool>? SetOfferWindow;
    public Action? ShowMainWindow;

    /// <summary>
    /// An NPC's line: when it starts quests the character can do now, they are offered (true: show the window).
    /// </summary>
    internal bool OfferFor(string npc, string text, long now)
    {
        if (_offeredAt.TryGetValue(npc, out long at) && now - at < OfferRepeatMs) return false;
        var list = new List<GuideQuest>();
        foreach (GuideQuest q in QuestOffers.StartedBy(_c.Wiki.Quests, npc, text))
        {
            if (_c.Runner?.Quest?.Title == q.Title || _offerDeclined.Contains(q.Title)) continue;
            if (WikiWait(q) > TimeSpan.Zero) continue;   // on its timer
            var stamps = new List<string>(q.AllStamps());
            if (q.RepeatSec == 0 && stamps.Count > 0 && stamps.TrueForAll(st => StateOf(q, st) == StampState.Done)) continue;   // one-time, done
            list.Add(q);
            if (list.Count >= QuestOffers.MaxQuests) break;
        }
        if (list.Count == 0) return false;
        _offeredAt[npc] = now;
        _offer.Clear();
        _offer.AddRange(list);
        _offerNpc = npc;
        _offerText = text;
        _c.Print($"{npc} starts {string.Join(", ", list.ConvertAll(q => q.Title))}: the quest offer window asks whether to run it.");
        return true;
    }

    internal IReadOnlyList<GuideQuest> Offered => _offer;

    public void DrawOffer(UiWindow w)
    {
        if (_offer.Count == 0)
        {
            w.TextDisabled("No quest offered.");
            return;
        }
        w.TextColored(UiColors.Gold, _offer.Count == 1 ? $"{_offerNpc} starts a quest" : $"{_offerNpc} starts {_offer.Count} quests");
        if (_offerText.Length > 0) w.TextWrapped("\"" + Cut(_offerText, 160) + "\"");
        w.Separator();
        for (int i = 0; i < _offer.Count; i++)
        {
            GuideQuest q = _offer[i];
            w.TextColored(UiColors.Yellow, q.Title);
            var facts = new List<string> { RepeatText(q) };
            if (q.Level.Length > 0) facts.Add("Level " + q.Level);
            if (q.Steps.Count > 0) facts.Add($"{q.Steps.Count} step(s)");
            w.TextDisabled(string.Join("  |  ", facts));
            w.Button("Run this quest", $"offer.run.{i}", () =>
            {
                _c.Runner?.Start(q, st => StateOf(q, st) == StampState.Done, loot: true);
                CloseOffer();
            });
            w.Tooltip("Yes: Run this quest (RynthNav walks its steps, RynthAi talks, loots and picks up its items).");
            w.SameLine();
            w.Button("Show", $"offer.show.{i}", () =>
            {
                ShowGuide(GuideTab.Wiki, "w:" + q.Title);
                ShowMainWindow?.Invoke();
            });
            w.Tooltip("Open it in the quest guide (its steps), without running it.");
        }
        w.Separator();
        w.Button("Not now", "offer.no", () =>
        {
            foreach (GuideQuest q in _offer) _offerDeclined.Add(q.Title);
            CloseOffer();
        });
        w.Tooltip("No: not asked about these quests again this session.");
    }

    private void CloseOffer()
    {
        _offer.Clear();
        SetOfferWindow?.Invoke(false);
    }
}
