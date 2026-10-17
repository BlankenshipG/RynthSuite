using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Dynamic Stamps (Infinite Leaftide only): the NPC hand-ins that put a "Dynamic_N_..." flag on
/// the character, by area: which this character has and which are missing, read from /myquests.
/// The list follows the players' "ExtraDyn" sheet (dynstamps.csv); stamps the character has that
/// the list doesn't are shown at the end, so new NPCs still count.
/// </summary>
internal sealed partial class OracleView
{
    private bool _dynMissingOnly, _dynHaveOnly;
    private string _dynSearch = "";
    private int _dynArea;   // 0 = all areas, else DynamicStampList.Areas[n - 1]

    private void DrawDynamicStamps(UiWindow w)
    {
        QuestFlagStore flags = _c.Flags;
        if (flags.LastReadUtc == DateTime.MinValue)
            w.TextDisabled("Quest flags not read yet (Quests tab, Refresh).");

        List<DynamicStamp> all = DynamicStampList.All;
        int have = 0;
        foreach (DynamicStamp s in all) if (s.Have(flags)) have++;
        List<string> unlisted = DynamicStampList.Unlisted(flags);

        w.ProgressBar(all.Count == 0 ? 0f : (float)have / all.Count, -1f, 0f, $"{have} / {all.Count} listed Dynamic stamps");
        w.Tooltip("Dynamic stamps are one-time quest flags (Dynamic_1_...) from handing items to NPCs on Infinite Leaftide.\n"
                  + "List: the players' ExtraDyn sheet (leaftidewiki Dynamic Stamps). Click a row to print it in chat.");
        w.Text($"{all.Count - have} missing"
               + (unlisted.Count > 0 ? $", plus {unlisted.Count} Dynamic stamp(s) on this character that the list doesn't have." : "."));

        w.Checkbox("Missing only", "dy.missing", _dynMissingOnly, v => { _dynMissingOnly = v; if (v) _dynHaveOnly = false; });
        w.SameLine();
        w.Checkbox("Have only", "dy.have", _dynHaveOnly, v => { _dynHaveOnly = v; if (v) _dynMissingOnly = false; });
        w.SameLine();
        var areaItems = new List<string>(DynamicStampList.Areas.Count + 1) { "All areas" };
        areaItems.AddRange(DynamicStampList.Areas);
        w.Combo("##dyarea", "dy.area", Math.Clamp(_dynArea, 0, areaItems.Count - 1), areaItems, v => _dynArea = v);
        w.InputText("Search", "dy.search", _dynSearch, 64, v => _dynSearch = v ?? "");
        w.Tooltip("NPC, place, reward, area or stamp.");

        string search = _dynSearch.Trim();
        bool filtering = search.Length > 0 || _dynMissingOnly || _dynHaveOnly || _dynArea > 0;

        w.BeginChild("dy.list", 0f, 0f, border: true);
        for (int a = 0; a < DynamicStampList.Areas.Count; a++)
        {
            string area = DynamicStampList.Areas[a];
            if (_dynArea > 0 && _dynArea - 1 != a) continue;

            // Count and filter this area's rows first: a header only for areas with something to show.
            int areaHave = 0, areaTotal = 0;
            var rows = new List<(DynamicStamp S, bool Done)>();
            foreach (DynamicStamp s in all)
            {
                if (s.Area != area) continue;
                bool done = s.Have(flags);
                areaTotal++;
                if (done) areaHave++;
                if (_dynMissingOnly && done) continue;
                if (_dynHaveOnly && !done) continue;
                if (search.Length > 0 && !Matches(s, search)) continue;
                rows.Add((s, done));
            }
            if (rows.Count == 0) continue;

            if (!w.CollapsingHeader($"{area}  ({areaHave}/{areaTotal})", "dy.area." + a, defaultOpen: filtering)) continue;
            foreach ((DynamicStamp s, bool done) in rows)
            {
                // Four ops a row (mark, SameLine, one line, a note tooltip only when there is one):
                // all 243 rows open must stay under the engine's 1500 ops per window.
                w.TextColored(done ? UiColors.Green : UiColors.Red, done ? "have" : "need");
                w.SameLine(50f);
                DynamicStamp row = s;
                string line = Cut(s.Label, 30)
                              + (s.Place.Length > 0 && s.Place != s.Label ? "  -  " + Cut(s.Place, 26) : "")
                              + (s.Reward.Length > 0 ? "  -  " + Cut(s.Reward, 36) : "");
                w.Selectable(line, "dy.s." + s.Stamp, false, () => PrintStamp(row, done));
                if (s.Note.Length > 0) w.Tooltip(s.Note);
            }
        }

        if (unlisted.Count > 0 && _dynArea == 0 && !_dynMissingOnly
            && w.CollapsingHeader($"Not in the list ({unlisted.Count})", "dy.unlisted", defaultOpen: false))
        {
            w.TextDisabled("Dynamic stamps on this character the sheet doesn't have yet:");
            foreach (string f in unlisted)
                if (search.Length == 0 || f.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                    w.BulletText(f);
        }
        w.EndChild();
    }

    private static bool Matches(DynamicStamp s, string q) =>
        s.Npc.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
        || s.Place.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
        || s.Reward.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
        || s.Area.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
        || s.Stamp.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Chat: the NPC, its stamp and whether this character has it, where, the reward and
    /// any coordinates (RynthChat makes those a RynthNav arrow link).</summary>
    private void PrintStamp(DynamicStamp s, bool done)
    {
        string where = s.Place.Length > 0 ? $"{s.Area}, {s.Place}" : s.Area;
        string details = $"{where}" + (s.Reward.Length > 0 ? $" - gives {s.Reward}" : "") + (s.Note.Length > 0 ? $" - {s.Note}" : "");
        PrintEntry(s.Label, s.Stamp, done ? "have" : "missing", details, "");
    }
}
