// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
// Tracking, kill tasks, rewards and the quest bonus list merged in from RynthAi's Mini Remote
// Quests tab and UtilityBelt ILT's quest tracker (2026-10-13). The Legendary, Markers and Dynamic
// Stamps tabs and RynthILT's Temple guardians merged in as tracker rows (2026-10-18).
using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// What a row of the merged Quests list is. Every kind but Quest is a tracker: Legendary Key quests
/// and exploration markers everywhere, Dynamic Stamps and the Temple guardians on Infinite Leaftide.
/// </summary>
internal enum QuestKind { Quest, Legendary, Marker, Dynamic, Guardian }

/// <summary>
/// Quests: every flag the character holds, cross-referenced with the master quest list, plus the
/// trackers (Legendary Key quests, exploration markers, Dynamic Stamps, Temple guardians), in one
/// searchable, sortable, paged list. Each kind shows only on the servers it belongs to, and
/// "Trackers only" narrows the list to the trackers with their status above it. The * column tracks
/// a quest in the Quest Timers window; clicking a row opens it below the list: a quest's notes and
/// rewards editor, a tracker's details, and RynthNav's Go / Route / Arrow for any coordinates in
/// its notes. On Infinite Leaftide the quest bonus list (/qb, read by RynthILT) follows.
/// </summary>
internal sealed partial class OracleView
{
    private const int QuestPageSize = 40;
    private const int QbShownMax = 150;          // per /qb list on one pass (the rest: narrow with Search)
    private const float QuestListHeightWithPanel = 300f;
    private static readonly string[] QuestSorts = { "Flag", "Name", "Done first", "Not done first", "Ready soonest", "Most solves" };
    /// <summary>The kinds' names in <see cref="QuestKind"/> order (the Show filter).</summary>
    private static readonly string[] KindNames = { "Quests", "Legendary", "Markers", "Dynamic Stamps", "Guardians" };
    /// <summary>The guardians' last answer is offered for this long (RynthILT's Mini Remote line does the same).</summary>
    private const long GuardianAnswerShownSec = 600;

    // The reward editor's fields, in QuestReward order (two percentages, then counts).
    private static readonly string[] RewardLabels =
        { "XP %", "Lum %", "Coins", "Trinkets", "Medallions", "Yellow powder", "Red powder", "Blue powder", "MMDs" };

    private string _questSearch = "";
    private bool _qDone, _qNotDone, _qRepeatable, _qOneTime, _qNew, _qServer, _qTracked, _qKill;
    // Trackers only: the list keeps only tracker rows, with the trackers' status above it.
    private bool _qTrackers;
    // The Show filter: one kind, or null for every kind this server has.
    private QuestKind? _qKind;
    private int _questSort;
    private int _questPage;
    private int _dynArea;   // 0 = all areas, else DynamicStampList.Areas[n - 1] (Show: Dynamic Stamps)

    private readonly List<QuestRow> _questRows = new();
    private string _questKey = "";
    private long _questBuiltMs;

    // The row opened below the list: a quest (_questSel), a Dynamic Stamp (_dynNav) or another tracker (_selKey).
    private QuestKind _selKind;
    private string _selKey = "";
    private string _questSel = "";
    private readonly string[] _rewardEdit = new string[RewardLabels.Length];
    private string _rewardMsg = "";

    /// <summary>One row of the merged list: its kind, a unique key, and the entry it shows.</summary>
    private sealed class QuestRow
    {
        public QuestKind Kind;
        /// <summary>Unique across kinds: the quest flag, or "lg:", "mk:", "dy:", "gd:" and the tracker's own key.</summary>
        public string Key = "";
        public string Label = "";
        /// <summary>The tracker's group: a Legendary area, a marker's location, a stamp's area, a guardian's NPC.</summary>
        public string Group = "";
        /// <summary>Its place in its own list (the default order of tracker rows).</summary>
        public int Order;
        public bool Done;
        public Quest? Q;
        public LegendaryQuest? L;
        public Marker? M;
        public DynamicStamp? D;
        public IltGuardianRow? G;
    }

    /// <summary>
    /// The kinds this server shows: quests, Legendary quests and markers everywhere; Dynamic Stamps
    /// and the Temple guardians only on Infinite Leaftide. Internal for the tests.
    /// </summary>
    internal List<QuestKind> AvailableKinds()
    {
        var kinds = new List<QuestKind> { QuestKind.Quest, QuestKind.Legendary, QuestKind.Marker };
        if (_c.Server.Is(ServerTags.InfiniteLeaftide))
        {
            kinds.Add(QuestKind.Dynamic);
            kinds.Add(QuestKind.Guardian);
        }
        return kinds;
    }

    /// <summary>A kind is listed: this server has it, the Show filter allows it, and Trackers only doesn't hide it.</summary>
    private bool KindListed(QuestKind k, List<QuestKind> kinds)
        => kinds.Contains(k) && (_qKind == null || _qKind == k) && !(_qTrackers && k == QuestKind.Quest);

    /// <summary>
    /// Opens the Quests tab on the trackers: one kind (/ro legendary, /ro dynamic, /ro markers,
    /// /ro guardians) or all of them (null). The old tabs' commands land here.
    /// </summary>
    public void ShowTrackers(QuestKind? kind)
    {
        _qTrackers = kind != QuestKind.Quest;
        _qKind = kind;
        _questPage = 0;
        Tab = TabQuests;
    }

    private void DrawQuests(UiWindow w)
    {
        QuestFlagStore flags = _c.Flags;
        RefreshAfterHandIn(flags);
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
        w.Tooltip("Sends /myquests to the server and reads the reply (every quest, Legendary quest, marker and stamp).");
        w.SameLine();
        bool timersShown = _c.QuestTimersWindowVisible();
        w.Button(timersShown ? "Hide quest timers" : "Show quest timers", "q.timers", () => _c.SetQuestTimersWindow(!timersShown));
        w.Tooltip("A small window with the quests you track (the * column) and the time left on each.");
        w.SameLine();
        QuestCatalog cat = _c.Catalog;
        w.TextDisabled(cat.PackServer.Length > 0
            ? $"{cat.RetailCount} retail quests + {cat.ServerCount} {cat.PackServer} quests"
            : $"{cat.RetailCount} retail quests");

        // A Show filter left on a kind this server doesn't have (another server's character): all kinds.
        List<QuestKind> kinds = AvailableKinds();
        if (_qKind is QuestKind chosen && !kinds.Contains(chosen)) _qKind = null;
        bool ilt = kinds.Contains(QuestKind.Dynamic);

        w.InputText("Search", "q.search", _questSearch, 64, s => { _questSearch = s; _questPage = 0; });
        w.Tooltip("Words to find in a row: a quest's flag or name; a tracker's NPC, place, area, reward or notes. Also filters the quest bonus list.");
        w.Checkbox("Done", "q.done", _qDone, v => { _qDone = v; _questPage = 0; });
        w.Tooltip("Done quests; Legendary quests and stamps you have, markers found, guardians on cooldown.");
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

        w.Checkbox("Trackers only", "q.trackers", _qTrackers, v =>
        {
            _qTrackers = v;
            if (v && _qKind == QuestKind.Quest) _qKind = null;
            _questPage = 0;
        });
        w.Tooltip("Only the trackers, with their status above the list: Legendary Key quests, exploration markers"
                  + (ilt ? ", Dynamic Stamps and the Temple guardians." : ".")
                  + (ilt ? "" : "\nDynamic Stamps and the Temple guardians show on Infinite Leaftide."));
        w.SameLine();
        var kindItems = new List<string>(kinds.Count + 1) { "Everything" };
        foreach (QuestKind k in kinds) kindItems.Add(KindNames[(int)k]);
        int kindIndex = _qKind is QuestKind sel ? kinds.IndexOf(sel) + 1 : 0;
        w.Combo("Show", "q.kind", kindIndex, kindItems, i =>
        {
            _qKind = i <= 0 || i > kinds.Count ? null : kinds[i - 1];
            if (_qKind == QuestKind.Quest) _qTrackers = false;
            _questPage = 0;
        });
        w.Tooltip("One kind of row, or everything this server has.");
        if (_qKind == QuestKind.Dynamic)
        {
            w.SameLine();
            var areaItems = new List<string>(DynamicStampList.Areas.Count + 1) { "All areas" };
            areaItems.AddRange(DynamicStampList.Areas);
            w.Combo("##dyarea", "dy.area", Math.Clamp(_dynArea, 0, areaItems.Count - 1), areaItems, v => { _dynArea = v; _questPage = 0; });
        }
        w.Combo("Sort", "q.sort", _questSort, QuestSorts, i => { _questSort = Math.Clamp(i, 0, QuestSorts.Length - 1); _questPage = 0; });

        if (_qTrackers || _qKind is QuestKind k2 && k2 != QuestKind.Quest) DrawTrackerSummary(w, kinds);

        List<QuestRow> rows = QuestRows(kinds);
        w.Text($"{rows.Count} shown");
        w.SameLine(160f);
        _questPage = Pager(w, "q.page", _questPage, rows.Count, QuestPageSize, p => _questPage = p);

        // A fixed-height list when something follows it (the opened row, the /qb list); else it fills the page.
        IltQuestBonusList? qb = ilt && !_qTrackers ? _c.Ilt.Profile.QuestBonus : null;
        bool selected = HasSelection();
        w.BeginChild("q.list", 0f, selected || qb != null ? QuestListHeightWithPanel : 0f, border: true);
        w.TextDisabled("*");
        w.Tooltip("Track: list the quest in the Quest Timers window. Trackers show their kind here instead (LEG, MRK, DYN, GRD).");
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
        w.Tooltip("What you recorded for a quest (click it to edit): %XP, %Lum, Coins, Trinkets, Medallions, Yellow/Red/Blue powder, MMDs (D), at Level.\n"
                  + "Trackers: a Legendary quest's luminance, a marker's location, a stamp's area, a guardian's next turn-in.");

        DateTime now = _c.UtcNow;
        bool canTrack = _c.Book.Loaded;
        int start = _questPage * QuestPageSize;
        for (int i = start; i < rows.Count && i < start + QuestPageSize; i++)
            DrawQuestRow(w, rows[i], flags, now, canTrack);
        if (rows.Count == 0)
            w.TextDisabled(_qTracked ? "No tracked quests match. Tick * on a quest to track it." : "No rows match.");
        w.EndChild();

        if (selected) DrawSelection(w);
        if (qb != null) DrawQuestBonus(w, qb);
    }

    /// <summary>One list row: a quest (track, done, timer, solves, rewards) or a tracker (kind, status, its own columns).</summary>
    private void DrawQuestRow(UiWindow w, QuestRow r, QuestFlagStore flags, DateTime now, bool canTrack)
    {
        if (r.Kind == QuestKind.Quest)
        {
            Quest q = r.Q!;
            string flag = q.Flag;
            bool tracked = _c.Book.IsTracked(flag);
            w.ToggleButton("*", "q.t." + flag, tracked, () => { if (canTrack) _c.Book.ToggleTracked(flag); }, small: true);
            w.SameLine(28f);
            DoneMark(w, r.Done);
            w.SameLine(72f);
            w.Selectable(Cut(q.Label, 44), "q.r." + flag, IsSelected(r), () => SelectRow(r), 290f);
            w.Tooltip(flag);
            w.SameLine(370f);
            TimerText(w, q, flags, now, r.Done);
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
            return;
        }

        // A tracker: its kind tag where the * is, then its mark, label and columns.
        w.TextColored(UiColors.Gold, r.Kind switch
        {
            QuestKind.Legendary => "LEG",
            QuestKind.Marker => "MRK",
            QuestKind.Dynamic => "DYN",
            _ => "GRD",
        });
        w.SameLine(28f);
        TrackerMark(w, r);
        w.SameLine(72f);
        w.Selectable(Cut(r.Label, 44), "q.r." + r.Key, IsSelected(r), () => SelectRow(r), 290f);
        string tip = TrackerTooltip(r);
        if (tip.Length > 0) w.Tooltip(Cut(tip, 300));
        switch (r.Kind)
        {
            case QuestKind.Legendary:
            {
                LegendaryQuest l = r.L!;
                TimeSpan? left = l.TimeLeft(flags, now);
                w.SameLine(370f);
                if (left == null) w.TextColored(UiColors.Grey, "not done");
                else if (left.Value <= TimeSpan.Zero) w.TextColored(UiColors.Green, "ready");
                else w.TextColored(UiColors.Yellow, TimeText.Friendly(left.Value));
                if (l.Luminance > 0)
                {
                    w.SameLine(530f);
                    w.TextDisabled(TimeText.Number(l.Luminance) + " lum");
                }
                break;
            }
            case QuestKind.Guardian:
            {
                IltGuardianRow g = r.G!;
                w.SameLine(370f);
                w.TextColored(g.Ready ? UiColors.Green : g.Status == "Unknown" ? UiColors.Grey : UiColors.Yellow, Cut(g.Status, 14));
                w.SameLine(470f);
                w.Text("#" + g.Count);
                if (g.Eta.Length > 0)
                {
                    w.SameLine(530f);
                    w.TextDisabled(Cut(g.Eta, 18));
                }
                break;
            }
            default:
                // Markers and stamps: where they are.
                if (r.Group.Length > 0)
                {
                    w.SameLine(530f);
                    w.TextDisabled(Cut(r.Group, 18));
                }
                break;
        }
    }

    /// <summary>A tracker's done column: have / need, found / --, or a guardian's ready / wait.</summary>
    private static void TrackerMark(UiWindow w, QuestRow r)
    {
        switch (r.Kind)
        {
            case QuestKind.Marker:
                DoneMark(w, r.Done);
                break;
            case QuestKind.Guardian:
                // Done = turned in and on cooldown; not done = a turn-in is ready (or unknown).
                w.TextColored(r.Done ? UiColors.Yellow : UiColors.Green, r.Done ? "wait" : "ready");
                break;
            default:
                w.TextColored(r.Done ? UiColors.Green : UiColors.Red, r.Done ? "have" : "need");
                break;
        }
    }

    /// <summary>A tracker row's hover text: its notes, hint or where it comes from.</summary>
    private static string TrackerTooltip(QuestRow r) => r.Kind switch
    {
        QuestKind.Legendary => r.L!.Note.Length > 0 ? $"{r.Group}: {r.L.Note}" : r.Group,
        QuestKind.Marker => r.M!.Hint,
        QuestKind.Dynamic => string.Join("  -  ", Parts(r.D!.Area, r.D.Place, r.D.Reward.Length > 0 ? "gives " + r.D.Reward : "", r.D.Note)),
        QuestKind.Guardian => $"{r.G!.Npc}: {(r.G.Exact ? "from /myquests (exact)" : "from /qb wait stamps (count and timer are estimates)")}",
        _ => "",
    };

    /// <summary>The non-empty strings, in order.</summary>
    private static List<string> Parts(params string[] items)
    {
        var list = new List<string>(items.Length);
        foreach (string s in items) if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
        return list;
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

    // ── Tracker status (above the list in Trackers only / one tracker kind) ──

    /// <summary>
    /// The trackers' status (what each old tab showed at its top), for the kinds the Show filter
    /// lists: John's Legendary counter, markers found and the next one, stamps had, and the Temple
    /// guardians' switches, last answer and hand-in.
    /// </summary>
    private void DrawTrackerSummary(UiWindow w, List<QuestKind> kinds)
    {
        QuestFlagStore flags = _c.Flags;
        bool Shows(QuestKind k) => kinds.Contains(k) && (_qKind == null || _qKind == k);

        if (Shows(QuestKind.Legendary))
        {
            DateTime now = _c.UtcNow;
            int have = 0, ready = 0;
            foreach (LegendaryQuest q in LegendaryList.All)
            {
                TimeSpan? left = q.TimeLeft(flags, now);
                if (left == null) continue;
                have++;
                if (left.Value <= TimeSpan.Zero) ready++;
            }
            // John's own counter when the server has it; it counts every key quest, not just these.
            int? john = LegendaryList.JohnCount(flags);
            int shown = john ?? have;
            w.ProgressBar(Math.Min(1f, (float)shown / LegendaryList.JohnTarget), -1f, 0f,
                john != null
                    ? $"John: {john} / {LegendaryList.JohnTarget} legendary quests ({ready} ready to run again)"
                    : $"{have} / {LegendaryList.JohnTarget} toward John, {ready} ready to run again");
            w.Tooltip("John in Arwic (33.6N 56.7E) rewards 30 different Legendary Key quests: 500,000 luminance and a 25-use Legendary Key.\n"
                      + "Bar: his counter (legendaryquestcounter_0913) when /myquests has it, else the Legendary quests you've done.");
        }

        if (Shows(QuestKind.Marker))
        {
            int found = 0;
            Marker? next = null;
            foreach (Marker m in MarkerList.All)
            {
                if (m.IsFound(flags)) found++;
                else next ??= m;
            }
            w.ProgressBar(MarkerList.All.Count > 0 ? (float)found / MarkerList.All.Count : 0f, -1f, 0f, $"{found} / {MarkerList.All.Count} markers found");
            if (next != null)
            {
                Marker n = next;
                w.TextDisabled("Next marker:");
                w.SameLine();
                w.Selectable($"#{next.Number} {next.Name} ({next.Location})", "m.next", false, () => SelectRow(MarkerRow(n, flags, 0)));
                w.Tooltip("Opens it below the list (directions, RynthNav).");
            }
        }

        if (Shows(QuestKind.Dynamic))
        {
            List<DynamicStamp> all = DynamicStampList.All;
            int have = 0;
            foreach (DynamicStamp s in all) if (s.Have(flags)) have++;
            int unlisted = DynamicStampList.Unlisted(flags).Count;
            w.ProgressBar(all.Count == 0 ? 0f : (float)have / all.Count, -1f, 0f,
                $"{have} / {all.Count} listed Dynamic stamps" + (unlisted > 0 ? $" (+{unlisted} not in the list)" : ""));
            w.Tooltip("Dynamic stamps are one-time quest flags (Dynamic_1_...) from handing items to NPCs on Infinite Leaftide.\n"
                      + "List: the players' ExtraDyn sheet. Click a stamp for RynthNav's route to it and the Parchment hand-in."
                      + (unlisted > 0 ? "\nStamps not in the sheet show as quests with Not in list." : ""));
        }

        if (Shows(QuestKind.Guardian)) DrawGuardianControls(w);
    }

    /// <summary>
    /// The Temple guardians' controls (RynthILT's Guardian window): chat detection, auto hand-in and
    /// buy-if-missing, which run inside RynthILT; the last answer with Hand in / Stop; and a refresh.
    /// </summary>
    private void DrawGuardianControls(UiWindow w)
    {
        IltGuardianView g = _c.Guardian;
        if (!g.Loaded)
        {
            w.TextDisabled(_c.IltData.Connected
                ? "Temple guardians: this RynthILT is older than 0.2.20 (no guardian data for Oracle)."
                : "Temple guardians: RynthILT isn't loaded (it runs the riddles, hand-ins and turn-in tracker).");
            return;
        }
        int ready = 0;
        foreach (IltGuardianRow r in g.Rows) if (r.Ready) ready++;
        w.TextColored(UiColors.Gold, "Temple guardians");
        w.SameLine();
        w.TextDisabled($"{ready} of {g.Rows.Count} attribute turn-ins ready"
                       + (g.SyncedUtc == DateTime.MinValue ? ", /qb not read this session" : $", /qb read {g.SyncedUtc.ToLocalTime():HH:mm}"));
        w.SameLine();
        w.SmallButton("Refresh", "gd.refresh", () => _c.GuardianCommand(IltGuardianView.RefreshVerb()));
        w.Tooltip("RynthILT re-reads /myquests and /qb list for the turn-ins (the server allows each once a minute).");

        w.Checkbox("Detect from chat", "gd.chat", g.Chat, v => _c.GuardianCommand(IltGuardianView.SwitchVerb("chat", v)));
        w.Tooltip("Translate the guardians' tells as they arrive and show the answer here.");
        w.SameLine();
        w.Checkbox("Auto hand-in", "gd.auto", g.Auto, v => _c.GuardianCommand(IltGuardianView.SwitchVerb("auto", v)));
        w.Tooltip("After a chat answer, RynthILT gives one of the item to the guardian automatically.");
        w.SameLine();
        w.Checkbox("Buy if missing", "gd.buy", g.Buy, v => _c.GuardianCommand(IltGuardianView.SwitchVerb("buy", v)));
        w.Tooltip("Temple guardian: when you carry none, RynthILT buys one from the open or nearest vendor first.");

        bool recent = g.Answer.Length > 0 && g.AnswerAgeSec >= 0 && g.AnswerAgeSec < GuardianAnswerShownSec;
        if (recent || g.Running)
        {
            w.TextColored(UiColors.Green, g.Answer);
            if (g.Heard.Length > 0) w.Tooltip("Heard: " + Cut(g.Heard, 300));
            w.SameLine();
            w.TextDisabled("-> " + g.Npc);
            w.SameLine();
            if (g.Running) w.SmallButton("Stop", "gd.stop", () => _c.GuardianCommand(IltGuardianView.StopVerb()));
            else w.SmallButton("Hand in", "gd.handin", () => _c.GuardianCommand(IltGuardianView.HandInVerb()));
        }
        else w.TextDisabled("No guardian answer yet.");
        w.TextDisabled("Hand-in: " + g.Status + (_c.GuardianMessage.Length > 0 ? "  -  " + _c.GuardianMessage : ""));
    }

    // ── The opened row ──────────────────────────────────────────────────────

    /// <summary>Something is opened below the list.</summary>
    private bool HasSelection() => _selKind switch
    {
        QuestKind.Quest => _questSel.Length > 0,
        QuestKind.Dynamic => _dynNav != null,
        _ => _selKey.Length > 0,
    };

    private bool IsSelected(QuestRow r) => r.Kind switch
    {
        QuestKind.Quest => _selKind == QuestKind.Quest && r.Q!.Flag == _questSel,
        QuestKind.Dynamic => _selKind == QuestKind.Dynamic && ReferenceEquals(r.D, _dynNav),
        _ => _selKind == r.Kind && _selKey == r.Key,
    };

    /// <summary>
    /// Clicking a row opens it below the list (a second click closes it). A Dynamic Stamp also asks
    /// RynthNav for the route at once, as its tab did.
    /// </summary>
    private void SelectRow(QuestRow r)
    {
        bool same = IsSelected(r);
        _questSel = "";
        _dynNav = null;
        _selKey = "";
        _questStopIdx = 0;
        _questNavStatus = "";
        if (same) return;
        _selKind = r.Kind;
        switch (r.Kind)
        {
            case QuestKind.Quest:
                SelectQuest(r.Q!);
                break;
            case QuestKind.Dynamic:
                _selKey = r.Key;
                SelectDynamicStamp(r.D!, r.Done);
                break;
            default:
                _selKey = r.Key;
                break;
        }
    }

    private void SelectQuest(Quest q)
    {
        _questSel = q.Flag;
        _rewardMsg = "";
        LoadRewardEdit(q.Flag);
    }

    /// <summary>The opened row's panel: a quest, a Dynamic Stamp's nav box, a Legendary quest, a marker or a guardian.</summary>
    private void DrawSelection(UiWindow w)
    {
        switch (_selKind)
        {
            case QuestKind.Quest:
                DrawSelectedQuest(w);
                break;
            case QuestKind.Dynamic:
                DrawDynamicNavBox(w);
                break;
            case QuestKind.Legendary:
                LegendaryQuest? l = LegendaryList.All.Find(x => "lg:" + x.Name == _selKey);
                if (l != null) DrawSelectedLegendary(w, l);
                break;
            case QuestKind.Marker:
                Marker? m = MarkerList.All.Find(x => "mk:" + x.Number == _selKey);
                if (m != null) DrawSelectedMarker(w, m);
                break;
            case QuestKind.Guardian:
                IltGuardianRow? g = null;
                foreach (IltGuardianRow r in _c.Guardian.Rows)
                    if ("gd:" + r.Attribute == _selKey) { g = r; break; }
                DrawSelectedGuardian(w, g);
                break;
        }
    }

    private void DrawSelectedLegendary(UiWindow w, LegendaryQuest l)
    {
        QuestFlagStore flags = _c.Flags;
        string status = l.Status(flags, _c.UtcNow);
        w.SeparatorText(Cut(l.Label, 60));
        w.TextDisabled("Area");
        w.SameLine(90f);
        w.Text(l.Group);
        w.TextDisabled("Flags");
        w.SameLine(90f);
        w.Text(Cut(string.Join(", ", l.Flags), 70));
        w.TextDisabled("Status");
        w.SameLine(90f);
        w.TextColored(status == "missing" ? UiColors.Red : status == "ready" ? UiColors.Green : UiColors.Yellow, status);
        if (l.Luminance > 0)
        {
            w.SameLine(260f);
            w.TextDisabled(TimeText.Number(l.Luminance) + " luminance");
        }
        if (l.Note.Length > 0) w.TextWrapped(Cut(l.Note, 600));
        DrawGoTo(w, StopsFor("lg:" + l.Name, () => NavTargets.Stops(l.Note)));
        w.Button("Print in chat", "lg.sel.print", () => PrintLegendary(l, status));
        w.Tooltip("The quest, its flags and status, the notes, wiki and video links.");
        w.SameLine();
        w.Button("Close", "lg.sel.close", () => _selKey = "");
    }

    private void DrawSelectedMarker(UiWindow w, Marker m)
    {
        bool found = m.IsFound(_c.Flags);
        w.SeparatorText(Cut($"Marker {m.Number}: {m.Name}", 60));
        w.TextDisabled("Location");
        w.SameLine(90f);
        w.Text(m.Location);
        w.TextDisabled("Status");
        w.SameLine(90f);
        w.TextColored(found ? UiColors.Green : UiColors.Grey, found ? "found" : "not found yet");
        if (m.Hint.Length > 0) w.TextWrapped(Cut(m.Hint, 600));
        // Coordinates in the directions, else the location's name for RynthNav's Atlas.
        DrawGoTo(w, StopsFor("mk:" + m.Number, () =>
        {
            List<NavTargets.Stop> stops = NavTargets.Stops(m.Hint);
            if (stops.Count == 0 && m.Location.Length > 0) stops.Add(NavTargets.Place(m.Location));
            return stops;
        }));
        w.Button("Print in chat", "m.sel.print", () => PrintMarker(m));
        w.SameLine();
        w.Button("Close", "m.sel.close", () => _selKey = "");
    }

    private void DrawSelectedGuardian(UiWindow w, IltGuardianRow? g)
    {
        if (g != null)
        {
            w.SeparatorText("Temple guardian: " + g.Attribute);
            w.TextDisabled("NPC");
            w.SameLine(90f);
            w.Text(g.Npc);
            w.TextDisabled("Turn-ins");
            w.SameLine(90f);
            w.Text(g.Count.ToString());
            w.TextDisabled("Status");
            w.SameLine(90f);
            w.TextColored(g.Ready ? UiColors.Green : g.Status == "Unknown" ? UiColors.Grey : UiColors.Yellow, g.Status);
            if (g.Eta.Length > 0)
            {
                w.SameLine(260f);
                w.TextDisabled("Next: " + g.Eta);
            }
            w.TextDisabled(g.Exact ? "From /myquests (exact)." : $"From /qb wait stamps: the count and timer are estimates (cooldown taken as {_c.Guardian.CooldownHours} h).");
        }
        else w.SeparatorText("Temple guardians");
        DrawGuardianControls(w);
        w.Button("Close", "gd.sel.close", () => _selKey = "");
    }

    // ── Go to: the coordinates in the opened row's text, through RynthNav ──

    private string _questStopsFor = "";
    private List<NavTargets.Stop> _questStops = new();
    private int _questStopIdx;
    private string _questNavStatus = "";

    /// <summary>The opened row's stops, worked out once per row (<paramref name="key"/>).</summary>
    private List<NavTargets.Stop> StopsFor(string key, Func<List<NavTargets.Stop>> build)
    {
        if (_questStopsFor != key)
        {
            _questStopsFor = key;
            _questStops = build();
        }
        return _questStops;
    }

    /// <summary>
    /// Go to: one place is shown with Go / Route / Arrow; several get a picker first (picking one
    /// plans its route, as clicking a Dynamic Stamp does). Nothing without any.
    /// </summary>
    private void DrawGoTo(UiWindow w, List<NavTargets.Stop> stops)
    {
        if (stops.Count == 0) return;
        _questStopIdx = Math.Clamp(_questStopIdx, 0, stops.Count - 1);
        w.SeparatorText(stops.Count == 1 ? "Go to" : $"Go to ({stops.Count} places in these notes)");
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
            w.Tooltip("The places the notes give coordinates for, in their order. Picking one plans RynthNav's route to it.");
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

    // ── The opened quest and its rewards ──────────────────────────────────

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
        DrawGoTo(w, StopsFor("q:" + q.Flag, () => NavTargets.Stops(q.Info + " - " + q.Hint)));

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
            if (MatchesAll(s, terms)) shown.Add(s);
        string counts = terms.Length > 0 ? $"{shown.Count} of {items.Count}" : items.Count.ToString();
        if (!w.CollapsingHeader($"{label} ({counts})", id)) return;
        for (int i = 0; i < shown.Count && i < QbShownMax; i++) w.TextColored(color, shown[i]);
        if (shown.Count > QbShownMax) w.TextDisabled($"... and {shown.Count - QbShownMax} more. Narrow them with Search.");
        if (shown.Count == 0) w.TextDisabled(items.Count == 0 ? "None." : "None match the search.");
    }

    /// <summary>Every search word is in <paramref name="hay"/> (any case); no words = everything matches.</summary>
    private static bool MatchesAll(string hay, string[] terms)
    {
        foreach (string t in terms)
            if (hay.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    // ── Rows ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The filtered, sorted rows of every listed kind; rebuilt when the filter or data changed (or
    /// every 5 s for the timer sort).
    /// </summary>
    private List<QuestRow> QuestRows(List<QuestKind> kinds)
    {
        string key = $"{_questSearch}|{_qDone}{_qNotDone}{_qRepeatable}{_qOneTime}{_qNew}{_qServer}{_qTracked}{_qKill}|{_questSort}"
                     + $"|{_qTrackers}|{_qKind}|{_dynArea}|{string.Join(",", kinds)}"
                     + $"|{_c.Flags.Revision}|{_c.Catalog.Revision}|{(_qTracked ? _c.Book.Revision : 0)}|{_c.GuardianRevision}";
        bool timeSorted = _questSort == 4;
        if (key == _questKey && (!timeSorted || _c.NowMs - _questBuiltMs < 5000)) return _questRows;
        _questKey = key;
        _questBuiltMs = _c.NowMs;

        QuestFlagStore flags = _c.Flags;
        string[] terms = _questSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _questRows.Clear();

        // The Dynamic stamps the list matched: shown as stamp rows, not again as unknown quest flags.
        var stampFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (kinds.Contains(QuestKind.Dynamic))
            foreach (DynamicStamp s in DynamicStampList.All)
                if (s.MatchedFlag(flags) is string f) stampFlags.Add(f);

        if (KindListed(QuestKind.Quest, kinds))
        {
            foreach (Quest q in _c.Catalog.Quests)
            {
                if (q.IsNew && stampFlags.Contains(q.Flag)) continue;
                if (_qNew && !q.IsNew) continue;
                if (_qServer && q.Server.Length == 0) continue;
                if (_qTracked && !_c.Book.IsTracked(q.Flag)) continue;
                if (_qKill && !(flags.TryGet(q.Flag, out QuestFlag kf) && QuestTimer.IsKillTask(kf))) continue;
                bool done = q.IsComplete(flags);
                if (_qDone != _qNotDone && done != _qDone) continue;
                if (_qRepeatable != _qOneTime && q.IsRepeatable(flags) != _qRepeatable) continue;
                if (!MatchesAll(q.Flag + " " + q.DisplayName, terms)) continue;
                _questRows.Add(new QuestRow { Kind = QuestKind.Quest, Key = q.Flag, Label = q.DisplayName, Done = done, Q = q });
            }
        }

        // The quest-only filters (not in the list, server pack, kill tasks) leave the trackers out.
        if (!_qNew && !_qServer && !_qKill)
        {
            if (KindListed(QuestKind.Legendary, kinds))
                for (int i = 0; i < LegendaryList.All.Count; i++)
                {
                    LegendaryQuest l = LegendaryList.All[i];
                    var r = new QuestRow { Kind = QuestKind.Legendary, Key = "lg:" + l.Name, Label = l.Label, Group = l.Group, Order = i, Done = l.Have(flags), L = l };
                    bool tracked = l.Flags.Length > 0 && _c.Book.IsTracked(l.Flags[0]);
                    if (KeepTracker(r, repeatable: true, tracked, $"{l.Label} {l.Group} {l.Note} {string.Join(" ", l.Flags)}", terms)) _questRows.Add(r);
                }

            if (KindListed(QuestKind.Marker, kinds))
                for (int i = 0; i < MarkerList.All.Count; i++)
                {
                    QuestRow r = MarkerRow(MarkerList.All[i], flags, i);
                    if (KeepTracker(r, repeatable: false, tracked: false, $"{r.Label} {r.M!.Location} {r.M.Hint}", terms)) _questRows.Add(r);
                }

            if (KindListed(QuestKind.Dynamic, kinds))
                for (int i = 0; i < DynamicStampList.All.Count; i++)
                {
                    DynamicStamp s = DynamicStampList.All[i];
                    if (_dynArea > 0 && (_dynArea > DynamicStampList.Areas.Count || DynamicStampList.Areas[_dynArea - 1] != s.Area)) continue;
                    var r = new QuestRow { Kind = QuestKind.Dynamic, Key = "dy:" + s.Stamp, Label = s.Label, Group = s.Area, Order = i, Done = s.Have(flags), D = s };
                    if (KeepTracker(r, repeatable: false, tracked: false, $"{s.Npc} {s.Place} {s.Reward} {s.Area} {s.Stamp} {s.Note}", terms)) _questRows.Add(r);
                }

            if (KindListed(QuestKind.Guardian, kinds))
                for (int i = 0; i < _c.Guardian.Rows.Count; i++)
                {
                    IltGuardianRow g = _c.Guardian.Rows[i];
                    var r = new QuestRow { Kind = QuestKind.Guardian, Key = "gd:" + g.Attribute, Label = g.Attribute + " (" + g.Npc + ")", Group = g.Npc, Order = i, Done = !g.Ready, G = g };
                    if (KeepTracker(r, repeatable: true, tracked: false, $"{g.Attribute} {g.Npc} {g.Status}", terms)) _questRows.Add(r);
                }
        }

        DateTime now = _c.UtcNow;
        switch (_questSort)
        {
            case 1:
                _questRows.Sort((a, b) => Then(string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase), a, b));
                break;
            case 2:
                _questRows.Sort((a, b) => Then(b.Done.CompareTo(a.Done), a, b));
                break;
            case 3:
                _questRows.Sort((a, b) => Then(a.Done.CompareTo(b.Done), a, b));
                break;
            case 4:
                _questRows.Sort((a, b) => Then(RowReadyKey(a, flags, now).CompareTo(RowReadyKey(b, flags, now)), a, b));
                break;
            case 5:
                _questRows.Sort((a, b) => Then(RowSolves(b, flags).CompareTo(RowSolves(a, flags)), a, b));
                break;
            default:
                _questRows.Sort((a, b) => Then(0, a, b));
                break;
        }
        return _questRows;

        // Ties: by kind (quests first), then quests by flag and trackers in their own list's order.
        static int Then(int c, QuestRow a, QuestRow b)
        {
            if (c != 0) return c;
            if (a.Kind != b.Kind) return a.Kind.CompareTo(b.Kind);
            return a.Kind == QuestKind.Quest ? string.CompareOrdinal(a.Key, b.Key) : a.Order.CompareTo(b.Order);
        }
    }

    /// <summary>A marker's row (the list, and the summary's "Next marker").</summary>
    private static QuestRow MarkerRow(Marker m, QuestFlagStore flags, int order)
        => new() { Kind = QuestKind.Marker, Key = "mk:" + m.Number, Label = $"#{m.Number} {m.Name}", Group = m.Location, Order = order, Done = m.IsFound(flags), M = m };

    /// <summary>The filters a tracker row answers to: Tracked, Done / Not done, Repeatable / One-time and Search.</summary>
    private bool KeepTracker(QuestRow r, bool repeatable, bool tracked, string hay, string[] terms)
    {
        if (_qTracked && !tracked) return false;
        if (_qDone != _qNotDone && r.Done != _qDone) return false;
        if (_qRepeatable != _qOneTime && repeatable != _qRepeatable) return false;
        return MatchesAll(hay, terms);
    }

    /// <summary>Ready soonest: held repeatable quests and Legendary quests by time left, ready guardians first; the rest after.</summary>
    private static double RowReadyKey(QuestRow r, QuestFlagStore flags, DateTime now)
    {
        switch (r.Kind)
        {
            case QuestKind.Quest:
                return ReadyKey(r.Q!, flags, now);
            case QuestKind.Legendary:
                TimeSpan? left = r.L!.TimeLeft(flags, now);
                return left == null ? double.MaxValue : Math.Max(0, left.Value.TotalSeconds);
            case QuestKind.Guardian:
                return r.G!.Ready ? 0 : double.MaxValue;
            default:
                return double.MaxValue;
        }
    }

    /// <summary>Most solves: a repeatable quest's solves, a guardian's turn-ins.</summary>
    private static int RowSolves(QuestRow r, QuestFlagStore flags) => r.Kind switch
    {
        QuestKind.Quest => Solves(r.Q!, flags),
        QuestKind.Guardian => r.G!.Count,
        _ => 0,
    };

    /// <summary>Held repeatables by time left (ready ones first); everything else after them.</summary>
    private static double ReadyKey(Quest q, QuestFlagStore flags, DateTime now) =>
        flags.TryGet(q.Flag, out _) && (q.IsRepeatable(flags) || flags.TryGet(q.Flag, out QuestFlag f) && QuestTimer.IsKillTask(f))
            ? QuestTimer.SortKey(q, flags, now)
            : double.MaxValue;

    private static int Solves(Quest q, QuestFlagStore flags) =>
        flags.TryGet(q.Flag, out QuestFlag f) && q.IsRepeatable(flags) ? f.Solves : 0;
}
