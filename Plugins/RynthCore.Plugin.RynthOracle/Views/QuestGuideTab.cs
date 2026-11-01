// The Quests tab's guide view (2026-11-01, Silentkelpie's layout): the quests on the left with how many of
// their stamps you hold, the opened quest on the right: its timer, where it starts, its steps with their
// coordinates (Go) and the stamps each gives (ticked when you hold them), its loot and rewards, and "Run this
// quest" (QuestRunner: RynthNav walks the steps, RynthAi picks up the quest's items). On Infinite Leaftide the
// list holds the wiki's quests (RynthILT reads leaftidewiki.com: QuestGuideClient) with the master quest list's
// own, and the Dynamic Stamps by area; elsewhere the master list's quests. Tabs: All / Story / Dailies /
// Weekly, and on ILT QB / ILT Wiki / Stamps. "Flag list" switches back to the full flag list (QuestsTab.cs).
using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>The guide view's tabs.</summary>
internal enum GuideTab { All, Story, Dailies, Weekly, Qb, Wiki, Stamps }

internal sealed partial class OracleView
{
    private static readonly string[] GuideTabNames = { "All", "Story", "Dailies", "Weekly", "QB", "ILT Wiki", "Stamps" };
    private const int GuidePageSize = 60;
    private const int GuideLabelChars = 34;
    private const int ColText = 0;   // ImGuiCol_Text

    private GuideTab _gTab;
    private string _gSearch = "";
    private int _gPage;
    private string _gSel = "";
    private string _gMsg = "";
    private readonly List<GuideRow> _gRows = new();
    private string _gRowsKey = "";

    // The stamps the character holds (GuideStamp keys): /myquests flags, the /qb list, this session's stamps.
    private HashSet<string> _held = new(StringComparer.Ordinal);
    private string _heldKey = "";

    private sealed class GuideRow
    {
        public string Key = "";
        public string Label = "";
        public string Status = "";
        public uint Color = UiColors.Grey;
        public bool Done;
        public GuideQuest? W;
        public Quest? Q;
        public string Area = "";
    }

    /// <summary>The tabs this server has: QB, ILT Wiki and Stamps only on Infinite Leaftide. Internal for the tests.</summary>
    internal List<GuideTab> GuideTabs()
    {
        var tabs = new List<GuideTab> { GuideTab.All, GuideTab.Story, GuideTab.Dailies, GuideTab.Weekly };
        if (_c.Server.IsIlt) { tabs.Add(GuideTab.Qb); tabs.Add(GuideTab.Wiki); tabs.Add(GuideTab.Stamps); }
        return tabs;
    }

    /// <summary>Opens a guide tab (and the guide view). Internal for the tests and /ro.</summary>
    internal void ShowGuide(GuideTab tab, string? select = null)
    {
        _c.Settings.QuestGuideView = true;
        _gTab = tab;
        _gPage = 0;
        if (select != null) _gSel = select;
        Tab = TabQuests;
    }

    /// <summary>The Guide / Flag list switch at the top of the Quests tab.</summary>
    private void DrawQuestViewSwitch(UiWindow w)
    {
        bool guide = _c.Settings.QuestGuideView;
        w.ToggleButton("Guide", "q.view.guide", guide, () => SetGuideView(true), small: true);
        w.Tooltip("The quests with their steps: the list on the left, the opened quest on the right.");
        w.SameLine();
        w.ToggleButton("Flag list", "q.view.flags", !guide, () => SetGuideView(false), small: true);
        w.Tooltip("Every quest flag you hold, with the trackers, filters, timers and rewards.");
    }

    private void SetGuideView(bool guide)
    {
        if (_c.Settings.QuestGuideView == guide) return;
        _c.Settings.QuestGuideView = guide;
        _c.Settings.Save();
    }

    // ── Held stamps ─────────────────────────────────────────────────────────

    private HashSet<string> HeldStamps()
    {
        string key = $"{_c.Flags.Revision}|{_c.Ilt.Revision}|{_c.SessionStampRevision}";
        if (key == _heldKey) return _held;
        _heldKey = key;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string f in _c.Flags.Flags.Keys) set.Add(GuideStamp.Key(f));
        if (_c.Server.IsIlt)
            foreach (string f in _c.Ilt.QbCompletedSet) set.Add(GuideStamp.Key(f));
        foreach (string s in _c.SessionStamps) set.Add(s);
        _held = set;
        return set;
    }

    private bool StampHeld(string stamp) => HeldStamps().Contains(GuideStamp.Key(stamp));

    /// <summary>The flag a stamp is in /myquests (any spelling of it), or null.</summary>
    private QuestFlag? StampFlag(string stamp)
    {
        if (_c.Flags.TryGet(stamp, out QuestFlag f)) return f;
        string k = GuideStamp.Key(stamp);
        foreach (KeyValuePair<string, QuestFlag> kv in _c.Flags.Flags)
            if (GuideStamp.Key(kv.Key) == k) return kv.Value;
        return null;
    }

    // ── The view ────────────────────────────────────────────────────────────

    private void DrawGuide(UiWindow w)
    {
        List<GuideTab> tabs = GuideTabs();
        if (!tabs.Contains(_gTab)) _gTab = GuideTab.All;
        foreach (GuideTab t in tabs)
        {
            GuideTab tab = t;
            w.ToggleButton(GuideTabNames[(int)t], "g.tab." + (int)t, _gTab == t, () => { _gTab = tab; _gPage = 0; });
            w.SameLine();
        }
        w.Checkbox("Hide collected", "g.hide", _c.Settings.GuideHideCollected, v =>
        {
            _c.Settings.GuideHideCollected = v;
            _c.Settings.Save();
            _gPage = 0;
        });
        w.Tooltip("Leave out the quests whose stamps you all hold (and stamps you have).");
        w.SameLine();
        w.Button("Refresh", "g.refresh", () => _gMsg = GuideRefresh());
        w.Tooltip("Reads your quest flags again (/myquests)" + (_c.Server.IsIlt ? ", and the wiki's quests (RynthILT)." : "."));

        w.InputText("##g.search", "g.search", _gSearch, 64, s => { _gSearch = s; _gPage = 0; });
        w.Tooltip("Words to find: a quest's name, NPC, place, items or stamps.");
        w.SameLine();
        DrawGuideSource(w);
        if (_gMsg.Length > 0) w.TextDisabled(_gMsg);

        List<GuideRow> rows = GuideRows();
        float total = w.ContentWidth > 0f ? w.ContentWidth : 680f;
        float leftW = Math.Clamp(total * 0.38f, 210f, 360f);

        w.BeginChild("g.list", leftW, 0f, border: true);
        if (rows.Count > GuidePageSize)
        {
            _gPage = Pager(w, "g.page", _gPage, rows.Count, GuidePageSize, p => _gPage = p);
        }
        else _gPage = 0;
        int start = _gPage * GuidePageSize;
        float statusX = Math.Max(120f, leftW - 86f);
        int chars = Math.Max(14, Math.Min(GuideLabelChars, (int)((statusX - 16f) / 7f)));
        for (int i = start; i < rows.Count && i < start + GuidePageSize; i++)
        {
            GuideRow r = rows[i];
            string key = r.Key;
            w.Selectable(Cut(r.Label, chars), "g.r." + key, key == _gSel, () => _gSel = key, statusX - 12f);
            if (r.Label.Length > chars) w.Tooltip(r.Label);
            if (r.Status.Length > 0)
            {
                w.SameLine(statusX);
                w.TextColored(r.Color, r.Status);
            }
        }
        if (rows.Count == 0)
            w.TextDisabled(_gTab == GuideTab.Wiki && _c.Guide.Guide.Quests.Count == 0 ? "No wiki quests yet (see above)." : "Nothing matches.");
        w.EndChild();

        w.SameLine();
        w.BeginChild("g.detail", 0f, 0f, border: true);
        GuideRow? sel = rows.Find(r => r.Key == _gSel) ?? FindGuideRow(_gSel);
        if (sel == null) w.TextDisabled("Pick a quest on the left.");
        else if (sel.W != null) DrawWikiQuest(w, sel.W);
        else if (sel.Q != null) DrawGuideCatalogQuest(w, sel.Q);
        else if (sel.Area.Length > 0) DrawStampArea(w, sel.Area);
        w.EndChild();
    }

    /// <summary>Where the wiki's quests come from, one line (ILT); the master list's count elsewhere.</summary>
    private void DrawGuideSource(UiWindow w)
    {
        if (!_c.Server.IsIlt)
        {
            w.TextDisabled($"{_c.Catalog.KnownCount} quests in the quest list");
            return;
        }
        QuestGuideClient g = _c.Guide;
        if (!g.Available)
        {
            w.TextDisabled("Wiki quests: RynthILT isn't serving them (update RynthILT).");
            return;
        }
        string text = g.Running ? "Wiki: reading... " + g.Status
            : g.Guide.Quests.Count > 0 ? $"Wiki: {g.Guide.Quests.Count} quests, read {g.Guide.UpdatedUtc.ToLocalTime():MM-dd HH:mm}"
            : g.LoginSaved ? "Wiki: " + g.Status : "Wiki: no login yet";
        w.TextDisabled(Cut(text, 70));
        w.Tooltip(g.Status + (g.LoginSaved ? "" : "\nThe wiki needs a login: make a bot password on leaftidewiki.com (Special:BotPasswords) and type it into RynthILT's settings > Quest guide. Or import a Special:Export file there."));
        if (!g.LoginSaved && g.Guide.Quests.Count == 0)
        {
            w.SameLine();
            w.SmallButton("Wiki login...", "g.wikilogin", () => _gMsg = g.Command("settings") ? "RynthILT's settings are open: Quest guide." : "RynthILT didn't answer.");
        }
    }

    private string GuideRefresh()
    {
        string msg = _c.RequestQuests() ? "Reading your quest flags..." : "Quest flags: not now (the server allows one /myquests a minute).";
        if (_c.Server.IsIlt && _c.Guide.Available && _c.Guide.LoginSaved && !_c.Guide.Running)
            msg += _c.Guide.Command("refresh") ? " Reading the wiki too." : "";
        return msg;
    }

    // ── Rows ────────────────────────────────────────────────────────────────

    private GuideRow? FindGuideRow(string key)
    {
        if (key.StartsWith("w:", StringComparison.Ordinal) && _c.Guide.Guide.Find(key.Substring(2)) is GuideQuest q) return WikiRow(q);
        if (key.StartsWith("q:", StringComparison.Ordinal) && _c.Catalog.TryGet(key.Substring(2), out Quest cq)) return CatalogRow(cq);
        return null;
    }

    private List<GuideRow> GuideRows()
    {
        HeldStamps();
        string key = $"{_gTab}|{_gSearch}|{_c.Settings.GuideHideCollected}|{_c.Server.IsIlt}|{_c.Guide.Revision}|{_c.Catalog.Revision}|{_heldKey}|{_c.UtcNow:yyyyMMddHHmm}";
        if (key == _gRowsKey) return _gRows;
        _gRowsKey = key;
        _gRows.Clear();
        string[] terms = _gSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool hide = _c.Settings.GuideHideCollected;

        if (_gTab == GuideTab.Stamps)
        {
            foreach (string area in DynamicStampList.Areas)
            {
                int have = 0, all = 0;
                string hay = area;
                foreach (DynamicStamp s in DynamicStampList.All)
                {
                    if (s.Area != area) continue;
                    all++;
                    if (s.Have(_c.Flags, _c.Ilt.QbStampSet)) have++;
                    hay += " " + s.Npc + " " + s.Place + " " + s.Reward;
                }
                if (all == 0 || !MatchesAll(hay, terms)) continue;
                bool done = have == all;
                if (hide && done) continue;
                _gRows.Add(new GuideRow
                {
                    Key = "a:" + area, Label = area, Area = area, Done = done,
                    Status = done ? "Completed" : $"{have} / {all}", Color = done ? UiColors.Green : have > 0 ? UiColors.Yellow : UiColors.Grey,
                });
            }
            return _gRows;
        }

        var wikiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_c.Server.IsIlt)
            foreach (GuideQuest q in _c.Guide.Guide.Quests)
            {
                wikiNames.Add(q.Title);
                if (!WikiInTab(q, _gTab)) continue;
                if (!MatchesAll(WikiHay(q), terms)) continue;
                GuideRow r = WikiRow(q);
                if (hide && r.Done) continue;
                _gRows.Add(r);
            }

        if (_gTab is GuideTab.All or GuideTab.Story or GuideTab.Dailies or GuideTab.Weekly)
            foreach (Quest q in _c.Catalog.Quests)
            {
                if (q.IsNew || wikiNames.Contains(q.DisplayName)) continue;
                long rep = CatalogRepeatSec(q);
                bool inTab = _gTab switch
                {
                    GuideTab.Story => rep == 0,
                    GuideTab.Dailies => rep > 0 && rep <= 26 * 3600,
                    GuideTab.Weekly => rep > 26 * 3600 && rep <= 31L * 86400,
                    _ => true,
                };
                if (!inTab || !MatchesAll(q.Flag + " " + q.DisplayName, terms)) continue;
                GuideRow r = CatalogRow(q);
                if (hide && r.Done) continue;
                _gRows.Add(r);
            }

        // The wiki's quests first (they have steps), then the quest list's; each by name.
        _gRows.Sort((a, b) => (a.W == null).CompareTo(b.W == null) is int k && k != 0 ? k
                               : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return _gRows;
    }

    private static bool WikiInTab(GuideQuest q, GuideTab tab) => tab switch
    {
        GuideTab.Story => q.IsOneTime,
        GuideTab.Dailies => q.IsDaily,
        GuideTab.Weekly => q.IsWeekly,
        GuideTab.Qb => q.Qb > 0,
        _ => true,
    };

    private static string WikiHay(GuideQuest q) =>
        $"{q.Title} {q.StartsWith} {q.StartsAt} {q.Type} {string.Join(" ", q.Loot)} {string.Join(" ", q.AllStamps())} {string.Join(" ", q.Categories)}";

    /// <summary>A catalog quest's repeat timer: the server's (its flag's), else the list's; 0 one-time.</summary>
    private long CatalogRepeatSec(Quest q)
    {
        if (_c.Flags.TryGet(q.Flag, out QuestFlag f) && f.RepeatTime > TimeSpan.Zero) return (long)f.RepeatTime.TotalSeconds;
        if (q.MinDelta > 0) return q.MinDelta;
        return q.IsRepeatable(_c.Flags) ? 86400 : 0;
    }

    private GuideRow WikiRow(GuideQuest q)
    {
        int held = 0, all = 0;
        foreach (string st in q.AllStamps())
        {
            all++;
            if (StampHeld(st)) held++;
        }
        var r = new GuideRow { Key = "w:" + q.Title, Label = q.Title, W = q };
        if (all == 0) { r.Status = ""; return r; }
        r.Done = held == all;
        if (!r.Done)
        {
            r.Status = $"{held} / {all}";
            r.Color = held > 0 ? UiColors.Yellow : UiColors.Grey;
            return r;
        }
        TimeSpan wait = WikiWait(q);
        if (q.RepeatSec != 0 && wait > TimeSpan.Zero) { r.Status = "Done " + Short(wait); r.Color = UiColors.Yellow; }
        else if (q.RepeatSec != 0 && wait == TimeSpan.Zero && AnyStampFlag(q)) { r.Status = "Ready"; r.Color = UiColors.Green; }
        else { r.Status = "Completed"; r.Color = UiColors.Green; }
        return r;
    }

    private bool AnyStampFlag(GuideQuest q)
    {
        foreach (string st in q.AllStamps()) if (StampFlag(st) != null) return true;
        return false;
    }

    /// <summary>How long until the quest's stamps can be had again (the longest wait on any of them), zero when ready.</summary>
    private TimeSpan WikiWait(GuideQuest q)
    {
        TimeSpan wait = TimeSpan.Zero;
        foreach (string st in q.AllStamps())
            if (StampFlag(st) is QuestFlag f && f.RepeatTime > TimeSpan.Zero)
            {
                TimeSpan left = f.NextAvailableTime(_c.UtcNow);
                if (left > wait) wait = left;
            }
        return wait;
    }

    private GuideRow CatalogRow(Quest q)
    {
        bool done = q.IsComplete(_c.Flags);
        var r = new GuideRow { Key = "q:" + q.Flag, Label = q.DisplayName, Q = q, Done = done };
        if (!done) return r;
        if (_c.Flags.TryGet(q.Flag, out QuestFlag f) && f.RepeatTime > TimeSpan.Zero)
        {
            TimeSpan left = f.NextAvailableTime(_c.UtcNow);
            r.Status = left > TimeSpan.Zero ? "Done " + Short(left) : "Ready";
            r.Color = left > TimeSpan.Zero ? UiColors.Yellow : UiColors.Green;
        }
        else { r.Status = "Completed"; r.Color = UiColors.Green; }
        return r;
    }

    /// <summary>"3d 4h", "5h 12m", "9m".</summary>
    internal static string Short(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))}m";
    }

    /// <summary>"Daily (every 20h 00m)", "Weekly (every 7d 0h)", "One-time", or the wiki's own words.</summary>
    internal static string RepeatText(GuideQuest q)
    {
        if (q.RepeatSec == 0) return q.Repeat.Length > 0 ? "One-time (" + q.Repeat + ")" : "One-time";
        if (q.RepeatSec < 0) return "Repeats: " + q.Repeat;
        string every = Short(TimeSpan.FromSeconds(q.RepeatSec));
        return q.IsDaily ? $"Daily (every {every})" : q.IsWeekly ? $"Weekly (every {every})" : $"Repeats every {every}";
    }

    // ── The opened wiki quest ───────────────────────────────────────────────

    private void DrawWikiQuest(UiWindow w, GuideQuest q)
    {
        QuestRunner? run = _c.Runner;
        bool runningThis = run?.Quest?.Title == q.Title;
        w.TextColored(UiColors.Gold, q.Title);
        TimeSpan wait = WikiWait(q);
        w.TextDisabled(RepeatText(q) + (wait > TimeSpan.Zero ? "  -  again in " + Short(wait) : ""));
        var facts = new List<string>();
        if (q.Level.Length > 0) facts.Add("Level " + q.Level);
        if (q.RecommendedLevel.Length > 0) facts.Add("rec. " + q.RecommendedLevel);
        if (q.Type.Length > 0) facts.Add(q.Type);
        if (q.Qb > 0) facts.Add("QB " + q.Qb.ToString(CultureInfo.InvariantCulture));
        if (q.TimeToComplete.Length > 0) facts.Add(q.TimeToComplete);
        if (facts.Count > 0) w.TextDisabled(string.Join("  |  ", facts));

        // Run this quest / its controls.
        if (!runningThis)
        {
            w.Button("Run this quest", "g.run", () =>
            {
                _c.Runner?.Start(q, StampHeld, loot: true);
                _gMsg = "";
            });
            w.Tooltip("RynthNav walks to each step you haven't been stamped for, in order; the step's instructions go to chat\n"
                      + "when you get there. A stamp moves it on (or Next). RynthAi picks up the quest's items meanwhile.");
            if (q.Loot.Count > 0)
            {
                w.SameLine();
                w.Button($"Loot map ({q.Loot.Count})", "g.loot", () =>
                {
                    int n = _c.Runner?.AddLoot(q) ?? 0;
                    _gMsg = n > 0 ? $"RynthAi picks up {n} quest item(s) this session: {string.Join(", ", q.Loot)} (/ra questloot list)."
                                  : "RynthAi has them already, or isn't loaded.";
                });
                w.Tooltip("Have RynthAi pick up this quest's items (by name) on top of its loot profile, this session:\n" + string.Join("\n", q.Loot));
            }
        }
        else
        {
            w.Button("Stop", "g.stop", () => _c.Runner?.Stop());
            w.SameLine();
            w.Button("Next", "g.next", () => _c.Runner?.Next());
            w.Tooltip("This step is done (or skip it): on to the next place or step.");
            w.SameLine();
            w.Button("Back", "g.back", () => _c.Runner?.Back());
            w.SameLine();
            w.Button("Again", "g.again", () => _c.Runner?.Again());
            w.Tooltip("Ask RynthNav to go to this step's place again.");
            if (run!.Note.Length > 0) w.TextColored(UiColors.Yellow, run.Note);
        }
        w.SameLine();
        w.SmallButton("Wiki page", "g.url", () => _c.Print(q.Url));
        w.Tooltip("Prints the wiki page's address in chat.");

        if (q.StartsWith.Length > 0 || q.StartsAt.Length > 0)
        {
            w.Text("Starts" + (q.StartsWith.Length > 0 ? " with " + q.StartsWith : "") + (q.StartsAt.Length > 0 ? " at " + q.StartsAt : ""));
            if (q.StartsAt.Length > 0)
            {
                w.SameLine();
                string place = q.StartsAt;
                w.SmallButton("Go", "g.start.go", () => _gMsg = SendRynthNav("go", new[] { place }));
                w.Tooltip("RynthNav goes to " + place + " (a place on its Atlas).");
            }
        }
        if (q.KillTask.Length > 0) w.TextDisabled("Kill task: " + q.KillTask);
        if (q.Overview.Length > 0) w.TextWrapped(q.Overview);

        w.SeparatorText(q.Steps.Count > 0 ? "Steps" : "Steps (the page has no walkthrough)");
        for (int i = 0; i < q.Steps.Count; i++) DrawWikiStep(w, q, i, runningThis ? run!.Step : -1);
        if (q.ExtraStamps.Count > 0)
        {
            w.TextDisabled("Also stamps:");
            foreach (string st in q.ExtraStamps) StampLine(w, st);
        }
        if (q.Loot.Count > 0)
        {
            w.SeparatorText("Loot");
            w.TextWrapped(string.Join(", ", q.Loot));
        }
        if (q.Rewards.Count > 0 && w.CollapsingHeader("Rewards", "g.rewards"))
            foreach (string r in q.Rewards)
                if (r.EndsWith(':')) w.Text(r); else w.BulletText(r);
    }

    private void DrawWikiStep(UiWindow w, GuideQuest q, int i, int current)
    {
        GuideStep s = q.Steps[i];
        bool done = s.Stamps.Count > 0 && s.Stamps.TrueForAll(StampHeld);
        uint color = i == current ? UiColors.Yellow : done ? UiColors.Green : 0;
        if (color != 0) w.PushStyleColor(ColText, color);
        w.TextWrapped($"{i + 1}. {s.Text}");
        if (color != 0) w.PopStyleColor();
        for (int k = 0; k < s.Coords.Count; k++)
        {
            string c = s.Coords[k];
            if (k > 0) w.SameLine();
            w.SmallButton("Go " + c, $"g.s{i}.c{k}", () => _gMsg = SendRynthNav("go", new[] { c }));
            w.Tooltip("RynthNav plans the way to " + c + " and walks it.");
        }
        if (s.Coords.Count > 0 && _c.Runner?.Quest?.Title == q.Title)
        {
            w.SameLine();
            int step = i;
            w.SmallButton("Run from here", $"g.s{i}.from", () => _c.Runner?.Start(q, StampHeld, loot: true, fromStep: step));
        }
        foreach (string n in s.Notes)
        {
            w.PushStyleColor(ColText, UiColors.Grey);
            w.TextWrapped("   " + n);
            w.PopStyleColor();
        }
        foreach (string st in s.Stamps) StampLine(w, st);
    }

    private void StampLine(UiWindow w, string stamp)
    {
        if (StampHeld(stamp)) w.TextColored(UiColors.Green, "   [x] You've stamped " + stamp + "!");
        else w.TextDisabled("   [ ] Stamp: " + stamp);
    }

    // ── The opened master-list quest ────────────────────────────────────────

    private void DrawGuideCatalogQuest(UiWindow w, Quest q)
    {
        w.TextColored(UiColors.Gold, q.DisplayName);
        w.TextDisabled(q.Flag + (q.Server.Length > 0 ? "  (" + q.Server + ")" : ""));
        string status = q.Status(_c.Flags, _c.UtcNow);
        if (status.Length > 0) w.Text(status);
        long rep = CatalogRepeatSec(q);
        w.TextDisabled(rep == 0 ? "One-time" : "Repeats every " + Short(TimeSpan.FromSeconds(rep)));
        bool tracked = _c.Book.IsTracked(q.Flag);
        string flag = q.Flag;
        w.Button(tracked ? "Untrack" : "Track", "g.q.track", () => { if (_c.Book.Loaded) _c.Book.ToggleTracked(flag); });
        w.Tooltip("List it in the Quest Timers window.");
        w.SameLine();
        w.SmallButton("Details in chat", "g.q.print", () => _c.PrintQuest(q));
        if (q.Info.Length > 0) w.TextWrapped(q.Info);
        if (q.Hint.Length > 0) w.TextWrapped(q.Hint);
        List<NavTargets.Stop> stops = NavTargets.Stops(q.Info + " - " + q.Hint);
        if (stops.Count > 0)
        {
            w.SeparatorText("Places");
            for (int i = 0; i < stops.Count; i++)
            {
                NavTargets.Stop st = stops[i];
                w.SmallButton("Go", "g.q.go." + i, () => _gMsg = SendRynthNav("go", new[] { st.Target }));
                w.SameLine();
                w.Text(Cut(st.Display, 70));
            }
        }
        if (q.Url.Length > 0) w.SmallButton("Wiki page", "g.q.url", () => _c.Print(q.Url));
    }

    // ── The opened Dynamic Stamps area ──────────────────────────────────────

    private void DrawStampArea(UiWindow w, string area)
    {
        w.TextColored(UiColors.Gold, "Dynamic Stamps: " + area);
        int i = 0;
        bool hide = _c.Settings.GuideHideCollected;
        foreach (DynamicStamp s in DynamicStampList.All)
        {
            if (s.Area != area) continue;
            bool have = s.Have(_c.Flags, _c.Ilt.QbStampSet);
            int n = i++;
            if (hide && have) continue;
            w.TextColored(have ? UiColors.Green : UiColors.Grey, have ? "[x]" : "[ ]");
            w.SameLine();
            w.Text(Cut(s.Label + (s.Place.Length > 0 && s.Place != s.Label ? " - " + s.Place : ""), 70));
            string tip = s.Stamp + (s.Reward.Length > 0 ? "\nReward: " + s.Reward : "") + (s.Note.Length > 0 ? "\n" + s.Note : "");
            w.Tooltip(tip);
            List<string> nav = s.NavCandidates();
            if (nav.Count > 0)
            {
                w.SameLine();
                w.SmallButton("Go", "g.a.go." + n, () => _gMsg = SendRynthNav("go", nav));
            }
        }
    }
}
