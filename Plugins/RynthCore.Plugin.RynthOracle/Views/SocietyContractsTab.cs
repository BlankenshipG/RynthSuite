// The Quests tab's Society and Contracts lists (2026-11-21, Silentkelpie: "lay out tabs for quests, kill task,
// socity quests, contracts ... pull a dynamic feed from https://asheron.fandom.com/wiki/Category:Society_Quests
// for socity quests, .../Category:Contracts for contracts"). On every server.
//  - Society: the wiki's society quests by rank (Joining, Initiate, Adept, Knight, Lord, Master), each the wiki
//    quest view with its steps, Run this quest and loot profile. The rank is the quest box's "Society Rank",
//    else the society category the page is in.
//  - Contracts: the AC Community Wiki's contract items: what each is, where it comes from (Go: RynthNav to the
//    person), and the quests it is for (Open; their state on the left). A contract's quest is looked up in the
//    wiki in use, then in the AC Community Wiki's.
using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

internal sealed partial class OracleView
{
    /// <summary>Society ranks in order (the wiki's names).</summary>
    internal static readonly string[] SocietyRanks = { "Joining", "Initiate", "Adept", "Knight", "Lord", "Master" };

    /// <summary>A rank's place in <see cref="SocietyRanks"/>; past the end for any other.</summary>
    internal static int RankOrder(string rank)
    {
        for (int i = 0; i < SocietyRanks.Length; i++)
            if (string.Equals(SocietyRanks[i], rank, StringComparison.OrdinalIgnoreCase)) return i;
        return SocietyRanks.Length;
    }

    /// <summary>A quest's society rank: the wiki in use, else the AC Community Wiki's categories.</summary>
    internal string SocietyRankOf(GuideQuest q)
    {
        string r = _c.Wiki.SocietyRankOf(q);
        if (r.Length == 0 && !_c.WikiIsFandom) r = _c.Fandom.Guide.SocietyRankOf(q);
        return r;
    }

    private void SocietyRows(string[] terms, bool hide)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(GuideQuest q)
        {
            if (!seen.Add(q.Title)) return;
            string rank = SocietyRankOf(q);
            if (rank.Length == 0) return;
            if (!MatchesAll(rank + " " + WikiHay(q), terms)) return;
            GuideRow r = WikiRow(q);
            if (hide && r.Done) return;
            r.Label = rank + ": " + q.Title;
            _gRows.Add(r);
            order[r.Key] = RankOrder(rank);
        }
        foreach (GuideQuest q in _c.Wiki.Quests) Add(q);
        if (!_c.WikiIsFandom)
            foreach (GuideQuest q in _c.Fandom.Guide.Quests) Add(q);
        _gRows.Sort((a, b) =>
        {
            int k = order[a.Key].CompareTo(order[b.Key]);
            return k != 0 ? k : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });
    }

    private GuideRow ContractRow(GuideContract c)
    {
        var r = new GuideRow { Key = "c:" + c.Title, Label = c.Name, C = c };
        // The state of the quest it is for (the first the wiki knows).
        foreach (string t in c.Quests)
            if (_c.FindWiki(t) is GuideQuest q)
            {
                GuideRow qr = WikiRow(q);
                if (qr.Status.Length > 0 && qr.Status != "no stamps") { r.Status = qr.Status; r.Color = qr.Color; r.Done = qr.Done; return r; }
                break;
            }
        if (c.Level.Length > 0) { r.Status = "L" + c.Level; r.Color = UiColors.Grey; }
        return r;
    }

    private void ContractRows(string[] terms, bool hide)
    {
        foreach (GuideContract c in _c.Fandom.Guide.Contracts)
        {
            if (!MatchesAll($"{c.Name} {c.Description} {c.Where} {string.Join(" ", c.Quests)}", terms)) continue;
            GuideRow r = ContractRow(c);
            if (hide && r.Done) continue;
            _gRows.Add(r);
        }
        _gRows.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
    }

    private void DrawContract(UiWindow w, GuideContract c)
    {
        w.TextColored(UiColors.Gold, c.Name);
        if (c.Level.Length > 0) w.TextDisabled("Recommended level " + c.Level);
        if (c.Description.Length > 0) w.TextWrapped(c.Description);
        if (c.Where.Length > 0)
        {
            w.Text("Where: " + c.Where);
            if (c.Npc.Length > 0)
            {
                w.SmallButton("Go to " + Cut(c.Npc, 28), "g.c.go", () => _gMsg = SendRynthNav("go", new[] { c.Npc }));
                w.Tooltip("RynthNav goes to " + c.Npc + ".");
            }
        }
        if (c.Quests.Count > 0)
        {
            w.Spacing();
            w.Text(c.Quests.Count == 1 ? "The quest it is for:" : "The quests it is for:");
            for (int i = 0; i < c.Quests.Count; i++)
            {
                string t = c.Quests[i];
                GuideQuest? q = _c.FindWiki(t);
                if (q == null) { w.TextDisabled("  " + t + " (no quest page on the wiki)"); continue; }
                GuideRow qr = WikiRow(q);
                w.SmallButton("Open##g.c.q" + i, "g.c.q." + i, () => { _gTab = GuideTab.Wiki; _gPage = 0; _gSel = "w:" + q.Title; _gSearch = ""; });
                w.Tooltip("Opens the quest: its steps, Run this quest and its loot profile.");
                w.SameLine();
                w.Text(q.Title);
                if (qr.Status.Length > 0)
                {
                    w.SameLine();
                    w.TextColored(qr.Color, qr.Status);
                }
            }
        }
        w.Spacing();
        w.TextDisabled("From the AC Community Wiki: " + c.Url);
    }
}
