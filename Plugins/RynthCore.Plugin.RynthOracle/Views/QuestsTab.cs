// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Quests: every flag the character holds, cross-referenced with the master quest list, in one
/// searchable, sortable, paged list (upstream's Quest Flags tab). Click a quest to print its
/// notes and wiki link in chat.
/// </summary>
internal sealed partial class OracleView
{
    private const int QuestPageSize = 40;
    private static readonly string[] QuestSorts = { "Flag", "Name", "Done first", "Not done first", "Ready soonest", "Most solves" };

    private string _questSearch = "";
    private bool _qDone, _qNotDone, _qRepeatable, _qOneTime, _qNew, _qServer;
    private int _questSort;
    private int _questPage;

    private readonly List<Quest> _questRows = new();
    private string _questKey = "";
    private long _questBuiltMs;

    private void DrawQuests(UiWindow w)
    {
        QuestFlagStore flags = _c.Flags;
        if (flags.ServerDisabled)
            w.TextColored(UiColors.Red, "This server has /myquests turned off, so quest flags can't be read.");
        else if (flags.Loading)
            w.TextColored(UiColors.Yellow, "Reading your quest flags...");
        else if (flags.LastReadUtc == DateTime.MinValue)
            w.TextDisabled("Quest flags not read yet. Press Refresh, or type /myquests.");
        else
            w.Text($"{flags.Flags.Count} quest flags on this character, read at {flags.LastReadUtc.ToLocalTime():HH:mm}.");

        w.Button("Refresh", "q.refresh", _c.RequestQuests);
        w.Tooltip("Sends /myquests to the server and reads the reply.");
        w.SameLine();
        QuestCatalog cat = _c.Catalog;
        w.TextDisabled(cat.PackServer.Length > 0
            ? $"{cat.RetailCount} retail quests + {cat.ServerCount} {cat.PackServer} quests"
            : $"{cat.RetailCount} retail quests");

        w.InputText("Search", "q.search", _questSearch, 64, s => { _questSearch = s; _questPage = 0; });
        w.Tooltip("Words to find in the quest's flag or name, e.g. \"kill task\".");
        w.Checkbox("Done", "q.done", _qDone, v => { _qDone = v; _questPage = 0; });
        w.SameLine();
        w.Checkbox("Not done", "q.notdone", _qNotDone, v => { _qNotDone = v; _questPage = 0; });
        w.SameLine();
        w.Checkbox("Repeatable", "q.rep", _qRepeatable, v => { _qRepeatable = v; _questPage = 0; });
        w.SameLine();
        w.Checkbox("One-time", "q.one", _qOneTime, v => { _qOneTime = v; _questPage = 0; });
        w.SameLine();
        w.Checkbox("Not in list", "q.new", _qNew, v => { _qNew = v; _questPage = 0; });
        w.Tooltip("Flags this character holds that no quest list knows.");
        if (_c.Catalog.PackServer.Length > 0)
        {
            w.SameLine();
            w.Checkbox(_c.Catalog.PackServer + " only", "q.server", _qServer, v => { _qServer = v; _questPage = 0; });
            w.Tooltip($"Quests only {_c.Catalog.PackServer} has.");
        }
        w.Combo("Sort", "q.sort", _questSort, QuestSorts, i => { _questSort = Math.Clamp(i, 0, QuestSorts.Length - 1); _questPage = 0; });

        List<Quest> rows = QuestRows();
        w.Text($"{rows.Count} shown");
        w.SameLine(160f);
        _questPage = Pager(w, "q.page", _questPage, rows.Count, QuestPageSize, p => _questPage = p);

        w.BeginChild("q.list", 0f, 0f, border: true);
        w.TextDisabled("Done");
        w.SameLine(50f);
        w.TextDisabled("Quest");
        w.SameLine(370f);
        w.TextDisabled("Ready in");
        w.SameLine(480f);
        w.TextDisabled("Solves");

        DateTime now = _c.UtcNow;
        int start = _questPage * QuestPageSize;
        for (int i = start; i < rows.Count && i < start + QuestPageSize; i++)
        {
            Quest q = rows[i];
            bool done = q.IsComplete(flags);
            DoneMark(w, done);
            w.SameLine(50f);
            string name = q.IsNew ? "(new) " + (q.Name.Length > 0 ? q.Name : q.Flag) : q.DisplayName;
            Quest row = q;
            w.Selectable(Cut(name, 46), "q.r." + q.Flag, false, () => PrintQuest(row), 310f);
            w.Tooltip(q.Flag);
            w.SameLine(370f);
            string status = q.Status(flags, now);
            if (status == "ready" && done) w.TextColored(UiColors.Green, status);
            else w.Text(status);
            string solves = q.SolvesText(flags);
            if (solves.Length > 0)
            {
                w.SameLine(480f);
                w.Text(solves);
            }
        }
        if (rows.Count == 0) w.TextDisabled("No quests match.");
        w.EndChild();
    }

    private void PrintQuest(Quest q)
    {
        string status = q.IsComplete(_c.Flags) ? q.Status(_c.Flags, _c.UtcNow) : "not done";
        PrintEntry(q.IsNew ? (q.Name.Length > 0 ? q.Name : "Not in the quest list") : q.DisplayName, q.Flag, status, q.Details(), q.Url);
    }

    /// <summary>The filtered, sorted rows; rebuilt when the filter or data changed (or every 5 s for timers).</summary>
    private List<Quest> QuestRows()
    {
        string key = $"{_questSearch}|{_qDone}{_qNotDone}{_qRepeatable}{_qOneTime}{_qNew}{_qServer}|{_questSort}|{_c.Flags.Revision}|{_c.Catalog.Revision}";
        bool timeSorted = _questSort == 4;
        if (key == _questKey && (!timeSorted || _c.NowMs - _questBuiltMs < 5000)) return _questRows;
        _questKey = key;
        _questBuiltMs = _c.NowMs;

        QuestFlagStore flags = _c.Flags;
        string[] terms = _questSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _questRows.Clear();
        foreach (Quest q in _c.Catalog.Quests)
        {
            if (_qNew && !q.IsNew) continue;
            if (_qServer && q.Server.Length == 0) continue;
            if (_qDone != _qNotDone && q.IsComplete(flags) != _qDone) continue;
            if (_qRepeatable != _qOneTime && q.IsRepeatable(flags) != _qRepeatable) continue;
            if (terms.Length > 0)
            {
                string hay = q.Flag + " " + q.DisplayName;
                bool all = true;
                foreach (string t in terms)
                    if (hay.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) { all = false; break; }
                if (!all) continue;
            }
            _questRows.Add(q);
        }

        DateTime now = _c.UtcNow;
        Comparison<Quest> byFlag = (a, b) => string.CompareOrdinal(a.Flag, b.Flag);
        switch (_questSort)
        {
            case 1:
                _questRows.Sort((a, b) => Then(string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase), a, b));
                break;
            case 2:
                _questRows.Sort((a, b) => Then(b.IsComplete(flags).CompareTo(a.IsComplete(flags)), a, b));
                break;
            case 3:
                _questRows.Sort((a, b) => Then(a.IsComplete(flags).CompareTo(b.IsComplete(flags)), a, b));
                break;
            case 4:
                _questRows.Sort((a, b) => Then(ReadyKey(a, flags, now).CompareTo(ReadyKey(b, flags, now)), a, b));
                break;
            case 5:
                _questRows.Sort((a, b) => Then(Solves(b, flags).CompareTo(Solves(a, flags)), a, b));
                break;
            default:
                _questRows.Sort(byFlag);
                break;
        }
        return _questRows;

        static int Then(int c, Quest a, Quest b) => c != 0 ? c : string.CompareOrdinal(a.Flag, b.Flag);
    }

    /// <summary>Held repeatables by time left (ready ones first); everything else after them.</summary>
    private static double ReadyKey(Quest q, QuestFlagStore flags, DateTime now)
    {
        if (!flags.TryGet(q.Flag, out QuestFlag f) || !q.IsRepeatable(flags) || f.AtSolveLimit) return double.MaxValue;
        return Math.Max(0, f.NextAvailableTime(now).TotalSeconds);
    }

    private static int Solves(Quest q, QuestFlagStore flags) =>
        flags.TryGet(q.Flag, out QuestFlag f) && q.IsRepeatable(flags) ? f.Solves : 0;
}
