// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
// Tracking, kill tasks, rewards and the quest bonus list merged in from RynthAi's Mini Remote
// Quests tab and UtilityBelt ILT's quest tracker (2026-10-13).
using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Quests: every flag the character holds, cross-referenced with the master quest list, in one
/// searchable, sortable, paged list (upstream's Quest Flags tab). The * column tracks a quest in
/// the Quest Timers window; clicking a quest opens it below the list, with its notes and the
/// rewards editor (UtilityBelt ILT's). On Infinite Leaftide the quest bonus list (/qb, read by
/// RynthILT) follows.
/// </summary>
internal sealed partial class OracleView
{
    private const int QuestPageSize = 40;
    private const int QbShownMax = 150;          // per /qb list on one pass (the rest: narrow with Search)
    private const float QuestListHeightWithPanel = 300f;
    private static readonly string[] QuestSorts = { "Flag", "Name", "Done first", "Not done first", "Ready soonest", "Most solves" };

    // The reward editor's fields, in QuestReward order (two percentages, then counts).
    private static readonly string[] RewardLabels =
        { "XP %", "Lum %", "Coins", "Trinkets", "Medallions", "Yellow powder", "Red powder", "Blue powder", "MMDs" };

    private string _questSearch = "";
    private bool _qDone, _qNotDone, _qRepeatable, _qOneTime, _qNew, _qServer, _qTracked, _qKill;
    private int _questSort;
    private int _questPage;

    private readonly List<Quest> _questRows = new();
    private string _questKey = "";
    private long _questBuiltMs;

    // The quest opened below the list, and its reward editor's text (kept while typing).
    private string _questSel = "";
    private readonly string[] _rewardEdit = new string[RewardLabels.Length];
    private string _rewardMsg = "";

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
        int throttleLeft = flags.ThrottleSecondsLeft(_c.NowMs);
        if (throttleLeft > 0 && !flags.Loading)
            w.TextDisabled($"The server answers one /myquests a minute: Refresh works again in {throttleLeft}s.");

        w.Button("Refresh", "q.refresh", _c.RequestQuests);
        w.Tooltip("Sends /myquests to the server and reads the reply.");
        w.SameLine();
        bool timersShown = _c.QuestTimersWindowVisible();
        w.Button(timersShown ? "Hide quest timers" : "Show quest timers", "q.timers", () => _c.SetQuestTimersWindow(!timersShown));
        w.Tooltip("A small window with the quests you track (the * column) and the time left on each.");
        w.SameLine();
        QuestCatalog cat = _c.Catalog;
        w.TextDisabled(cat.PackServer.Length > 0
            ? $"{cat.RetailCount} retail quests + {cat.ServerCount} {cat.PackServer} quests"
            : $"{cat.RetailCount} retail quests");

        w.InputText("Search", "q.search", _questSearch, 64, s => { _questSearch = s; _questPage = 0; });
        w.Tooltip("Words to find in the quest's flag or name, e.g. \"kill task\". Also filters the quest bonus list.");
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
        w.Checkbox("Tracked", "q.tracked", _qTracked, v => { _qTracked = v; _questPage = 0; });
        w.Tooltip("Only the quests you track (the * column).");
        w.SameLine();
        w.Checkbox("Kill tasks", "q.kill", _qKill, v => { _qKill = v; _questPage = 0; });
        w.Tooltip("Only kill-task counters you hold: kills left, then the reset timer.");
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

        // A fixed-height list when something follows it (the opened quest, the /qb list); else it fills the page.
        IltQuestBonusList? qb = _c.Server.Is(ServerTags.InfiniteLeaftide) ? _c.Ilt.Profile.QuestBonus : null;
        bool selected = _questSel.Length > 0;
        w.BeginChild("q.list", 0f, selected || qb != null ? QuestListHeightWithPanel : 0f, border: true);
        w.TextDisabled("*");
        w.Tooltip("Track: list the quest in the Quest Timers window.");
        w.SameLine(28f);
        w.TextDisabled("Done");
        w.SameLine(72f);
        w.TextDisabled("Quest");
        w.SameLine(370f);
        w.TextDisabled("Ready in");
        w.SameLine(470f);
        w.TextDisabled("Solves");
        w.SameLine(530f);
        w.TextDisabled("Rewards");
        w.Tooltip("What you recorded for the quest (click it to edit): %XP, %Lum, Coins, Trinkets, Medallions, Yellow/Red/Blue powder, MMDs (D), at Level.");

        DateTime now = _c.UtcNow;
        bool canTrack = _c.Book.Loaded;
        int start = _questPage * QuestPageSize;
        for (int i = start; i < rows.Count && i < start + QuestPageSize; i++)
        {
            Quest q = rows[i];
            string flag = q.Flag;
            bool tracked = _c.Book.IsTracked(flag);
            w.ToggleButton("*", "q.t." + flag, tracked, () => { if (canTrack) _c.Book.ToggleTracked(flag); }, small: true);
            w.SameLine(28f);
            bool done = q.IsComplete(flags);
            DoneMark(w, done);
            w.SameLine(72f);
            Quest row = q;
            w.Selectable(Cut(q.Label, 44), "q.r." + flag, flag == _questSel, () => SelectQuest(row), 290f);
            w.Tooltip(flag);
            w.SameLine(370f);
            TimerText(w, q, flags, now, done);
            string solves = q.SolvesText(flags);
            if (solves.Length > 0)
            {
                w.SameLine(470f);
                w.Text(solves);
            }
            string reward = _c.Book.RewardText(flag);
            if (reward.Length > 0)
            {
                w.SameLine(530f);
                w.Text(Cut(reward, 18));
                if (reward.Length > 18) w.Tooltip(reward);
            }
        }
        if (rows.Count == 0) w.TextDisabled(_qTracked ? "No tracked quests match. Tick * on a quest to track it." : "No quests match.");
        w.EndChild();

        if (selected) DrawSelectedQuest(w);
        if (qb != null) DrawQuestBonus(w, qb);
    }

    /// <summary>The timer column: "ready" (green once done), a countdown, "N left" (yellow) or done (grey).</summary>
    private static void TimerText(UiWindow w, Quest q, QuestFlagStore flags, DateTime now, bool done)
    {
        QuestTimerState s = QuestTimer.State(q, flags, now, out string text);
        switch (s)
        {
            case QuestTimerState.Ready when done: w.TextColored(UiColors.Green, text); break;
            case QuestTimerState.InProgress: w.TextColored(UiColors.Yellow, text); break;
            case QuestTimerState.Done: w.TextColored(UiColors.Grey, text); break;
            default: w.Text(text); break;
        }
    }

    // ── The opened quest and its rewards ──────────────────────────────────

    private void SelectQuest(Quest q)
    {
        if (_questSel == q.Flag) { _questSel = ""; return; }   // a second click closes it
        _questSel = q.Flag;
        _rewardMsg = "";
        _questStopIdx = 0;
        _questNavStatus = "";
        LoadRewardEdit(q.Flag);
    }

    // ── Go to: the coordinates in the opened quest's text, through RynthNav ──

    private string _questStopsFor = "";
    private List<NavTargets.Stop> _questStops = new();
    private int _questStopIdx;
    private string _questNavStatus = "";

    /// <summary>The opened quest's stops (coordinates with the step they belong to), worked out once per quest.</summary>
    private List<NavTargets.Stop> QuestStops(Quest q)
    {
        if (_questStopsFor != q.Flag)
        {
            _questStopsFor = q.Flag;
            _questStops = NavTargets.Stops(q.Info + " - " + q.Hint);
        }
        return _questStops;
    }

    /// <summary>
    /// The opened quest's coordinates: one is shown with Go / Route / Arrow; several get a picker
    /// first (picking one plans its route, as clicking a Dynamic Stamp does). Nothing without any.
    /// </summary>
    private void DrawQuestGoTo(UiWindow w, Quest q)
    {
        List<NavTargets.Stop> stops = QuestStops(q);
        if (stops.Count == 0) return;
        _questStopIdx = Math.Clamp(_questStopIdx, 0, stops.Count - 1);
        w.SeparatorText(stops.Count == 1 ? "Go to" : $"Go to ({stops.Count} places in this quest)");
        if (stops.Count == 1)
            w.Text(Cut(stops[0].Display, 80));
        else
        {
            var items = new List<string>(stops.Count);
            for (int i = 0; i < stops.Count; i++) items.Add($"{i + 1}. {Cut(stops[i].Display, 70)}");
            w.Combo("##q.nav.pick", "q.nav.pick", _questStopIdx, items, i =>
            {
                _questStopIdx = Math.Clamp(i, 0, stops.Count - 1);
                _questNavStatus = SendRynthNav("route", new[] { stops[_questStopIdx].Target });
            });
            w.Tooltip("The places this quest's walkthrough gives coordinates for, in its order. Picking one plans RynthNav's route to it.");
        }
        NavTargets.Stop stop = stops[_questStopIdx];
        w.Button("Go (auto nav)", "q.nav.go", () => _questNavStatus = SendRynthNav("go", new[] { stop.Target }));
        w.Tooltip("RynthNav plans the route (walking, portals, recalls, a dungeon's entrance) and walks it.");
        w.SameLine();
        w.Button("Route", "q.nav.route", () => _questNavStatus = SendRynthNav("route", new[] { stop.Target }));
        w.Tooltip("Only plan it: RynthNav prints the steps in chat.");
        w.SameLine();
        w.Button("Arrow", "q.nav.arrow", () => _questNavStatus = SendRynthNav("arrow", new[] { stop.Target }));
        w.Tooltip("Only point RynthNav's arrow at it.");
        if (_questNavStatus.Length > 0) w.TextDisabled(_questNavStatus);
    }

    /// <summary>Fills the editor from what is recorded (blank fields for zero).</summary>
    private void LoadRewardEdit(string flag)
    {
        Array.Fill(_rewardEdit, "");
        if (!_c.Book.TryGetReward(flag, out QuestReward r)) return;
        _rewardEdit[0] = r.XpPercent > 0 ? QuestReward.Percent(r.XpPercent) : "";
        _rewardEdit[1] = r.LumPercent > 0 ? QuestReward.Percent(r.LumPercent) : "";
        int[] counts = { r.Coins, r.Trinkets, r.Medallions, r.YellowPowder, r.RedPowder, r.BluePowder, r.Mmd };
        for (int i = 0; i < counts.Length; i++) _rewardEdit[2 + i] = counts[i] > 0 ? counts[i].ToString() : "";
    }

    private void DrawSelectedQuest(UiWindow w)
    {
        Quest q = _c.Catalog.TryGet(_questSel, out Quest found) ? found : new Quest { Flag = _questSel, IsNew = true };
        QuestFlagStore flags = _c.Flags;
        w.SeparatorText(Cut(q.Label, 60));
        w.TextDisabled("Flag");
        w.SameLine(90f);
        w.Text(q.Flag);
        w.TextDisabled("Status");
        w.SameLine(90f);
        TimerText(w, q, flags, _c.UtcNow, q.IsComplete(flags));
        if (flags.TryGet(q.Flag, out QuestFlag f))
        {
            w.SameLine(260f);
            w.TextDisabled(f.MaxSolves > 0 ? $"solves {f.Solves} of {f.MaxSolves}" : $"solves {f.Solves}");
        }
        string details = q.Details();
        if (details.Length > 0) w.TextWrapped(Cut(details, 600));
        DrawQuestGoTo(w, q);

        bool loaded = _c.Book.Loaded;
        bool tracked = _c.Book.IsTracked(q.Flag);
        string flag = q.Flag;
        w.Button(tracked ? "Untrack" : "Track", "q.sel.track", () => { if (loaded) _c.Book.ToggleTracked(flag); });
        w.Tooltip("List this quest in the Quest Timers window.");
        w.SameLine();
        Quest print = q;
        w.Button("Print in chat", "q.sel.print", () => PrintQuest(print));
        w.SameLine();
        w.Button("Close", "q.sel.close", () => _questSel = "");

        w.SeparatorText("Rewards");
        if (!loaded)
        {
            w.TextDisabled("Waiting for the character's name before rewards can be saved.");
            return;
        }
        int level = _c.Book.TryGetReward(flag, out QuestReward saved) && saved.RecordedLevel > 0 ? saved.RecordedLevel : _c.Character.Level;
        w.TextDisabled($"Level: {level} (XP % and Lum % are for this level)");
        for (int i = 0; i < RewardLabels.Length; i++)
        {
            int field = i;
            w.InputText(RewardLabels[i], "q.rw." + i, _rewardEdit[i], 16, s => _rewardEdit[field] = s);
        }
        w.Button("Save rewards", "q.rw.save", () => SaveReward(flag));
        w.SameLine();
        w.Button("Clear", "q.rw.clear", () =>
        {
            _c.Book.SetReward(flag, null);
            LoadRewardEdit(flag);
            _rewardMsg = "Cleared.";
        });
        if (_rewardMsg.Length > 0)
        {
            w.SameLine();
            w.TextDisabled(_rewardMsg);
        }
    }

    /// <summary>Parses the editor (UB's rules: percent 0..1000, counts 0 and up) and stores the entry.</summary>
    private void SaveReward(string flag)
    {
        var r = new QuestReward();
        if (!QuestReward.TryParsePercent(_rewardEdit[0], out r.XpPercent)) { _rewardMsg = "XP % isn't a number."; return; }
        if (!QuestReward.TryParsePercent(_rewardEdit[1], out r.LumPercent)) { _rewardMsg = "Lum % isn't a number."; return; }
        var counts = new int[7];
        for (int i = 0; i < counts.Length; i++)
        {
            if (QuestReward.TryParseCount(_rewardEdit[2 + i], out counts[i])) continue;
            _rewardMsg = RewardLabels[2 + i] + " isn't a whole number.";
            return;
        }
        r.Coins = counts[0];
        r.Trinkets = counts[1];
        r.Medallions = counts[2];
        r.YellowPowder = counts[3];
        r.RedPowder = counts[4];
        r.BluePowder = counts[5];
        r.Mmd = counts[6];

        // The percentages belong to the level they were noted at: keep the old level unless they changed.
        bool had = _c.Book.TryGetReward(flag, out QuestReward old);
        bool percentChanged = !had || old.XpPercent != r.XpPercent || old.LumPercent != r.LumPercent;
        if (r.XpPercent > 0 || r.LumPercent > 0)
            r.RecordedLevel = !percentChanged && old.RecordedLevel > 0 ? old.RecordedLevel : _c.Character.Level;

        _c.Book.SetReward(flag, r);
        LoadRewardEdit(flag);
        _rewardMsg = r.IsEmpty ? "Cleared." : "Saved: " + r.ShortSummary();
    }

    private void PrintQuest(Quest q)
    {
        string status = q.IsComplete(_c.Flags) ? q.Status(_c.Flags, _c.UtcNow) : "not done";
        PrintEntry(q.IsNew ? (q.Name.Length > 0 ? q.Name : "Not in the quest list") : q.DisplayName, q.Flag, status, q.Details(), q.Url);
        if (_c.Book.TryGetReward(q.Flag, out QuestReward r)) _c.Print("Rewards: " + r.LongSummary());
    }

    // ── Quest bonus (/qb, Infinite Leaftide through RynthILT) ──────────────

    private void DrawQuestBonus(UiWindow w, IltQuestBonusList qb)
    {
        string count = qb.Count >= 0 ? $" - count {TimeText.Number(qb.Count)}" : "";
        if (!w.CollapsingHeader($"Quest bonus (/qb){count}", "q.qb")) return;
        if (qb.Disabled)
        {
            w.TextDisabled("/qb is turned off on this server.");
            return;
        }
        w.Button("Load /qb list", "q.qb.load", () =>
        {
            if (!_c.Send("/ilt qb")) _c.Print("Log in first.");
        });
        w.Tooltip("RynthILT asks the server for your quest bonus list (the server allows once a minute).");
        w.SameLine();
        w.TextDisabled(qb.Generation == 0 && qb.Status == "not loaded" ? "Not loaded yet this session." : qb.Status);

        string[] terms = _questSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        QbList(w, "Incomplete", "q.qb.todo", qb.Incomplete, terms, UiColors.Yellow);
        QbList(w, "Completed", "q.qb.done", qb.Completed, terms, UiColors.Green);
    }

    private static void QbList(UiWindow w, string label, string id, IReadOnlyList<string> items, string[] terms, uint color)
    {
        var shown = new List<string>();
        foreach (string s in items)
        {
            bool all = true;
            foreach (string t in terms)
                if (s.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) { all = false; break; }
            if (all) shown.Add(s);
        }
        string counts = terms.Length > 0 ? $"{shown.Count} of {items.Count}" : items.Count.ToString();
        if (!w.CollapsingHeader($"{label} ({counts})", id)) return;
        for (int i = 0; i < shown.Count && i < QbShownMax; i++) w.TextColored(color, shown[i]);
        if (shown.Count > QbShownMax) w.TextDisabled($"... and {shown.Count - QbShownMax} more. Narrow them with Search.");
        if (shown.Count == 0) w.TextDisabled(items.Count == 0 ? "None." : "None match the search.");
    }

    // ── Rows ──────────────────────────────────────────────────────────────

    /// <summary>The filtered, sorted rows; rebuilt when the filter or data changed (or every 5 s for timers).</summary>
    private List<Quest> QuestRows()
    {
        string key = $"{_questSearch}|{_qDone}{_qNotDone}{_qRepeatable}{_qOneTime}{_qNew}{_qServer}{_qTracked}{_qKill}|{_questSort}"
                     + $"|{_c.Flags.Revision}|{_c.Catalog.Revision}|{(_qTracked ? _c.Book.Revision : 0)}";
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
            if (_qTracked && !_c.Book.IsTracked(q.Flag)) continue;
            if (_qKill && !(flags.TryGet(q.Flag, out QuestFlag kf) && QuestTimer.IsKillTask(kf))) continue;
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
    private static double ReadyKey(Quest q, QuestFlagStore flags, DateTime now) =>
        flags.TryGet(q.Flag, out _) && (q.IsRepeatable(flags) || flags.TryGet(q.Flag, out QuestFlag f) && QuestTimer.IsKillTask(f))
            ? QuestTimer.SortKey(q, flags, now)
            : double.MaxValue;

    private static int Solves(Quest q, QuestFlagStore flags) =>
        flags.TryGet(q.Flag, out QuestFlag f) && q.IsRepeatable(flags) ? f.Solves : 0;
}
