// The Quests tab's Kill Tasks list (2026-11-21, Silentkelpie: "add a tab to track kill tasks ... you should be
// able to track progress by looking at quests in the client"). Infinite Leaftide only. The list is the wiki's
// kill tasks (the Quest Summary box's "Kill Task = Yes", 150 pages) with each one's progress:
//  - the kills: the task's counter flag in /myquests ("KillTaskDrudgeLurkers_0507: 37 of 100"), else this
//    session's kill line in chat ("You have killed 37 Drudge Lurkers! You must kill 100 ...");
//  - "Turn in" once the kills are all made, "Done 5h" while its timer runs, "Ready" when it can be had again;
// and the kill counters /myquests holds that no wiki task names (so nothing the server counts is left out).
// The opened task is the wiki quest view (its steps, Go, Run this quest) with the kill count on top.
using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

internal sealed partial class OracleView
{
    /// <summary>Where a kill task stands; the order the list shows them in.</summary>
    internal enum KillState { TurnIn, Counting, Ready, NotStarted, Waiting, Completed }

    /// <summary>A kill task's progress: the kills and the goal, where the count came from, and its state.</summary>
    internal sealed class KillProgress
    {
        public KillState State;
        public int Kills;
        public int Goal;
        /// <summary>The /myquests counter flag, or null.</summary>
        public QuestFlag? Counter;
        /// <summary>The count came from this session's kill line in chat (newer than /myquests).</summary>
        public bool FromChat;
        public string Status = "";
        public uint Color = UiColors.Grey;
    }

    /// <summary>The Kill Tasks list is on screen (the plugin reads /myquests again after kills while it is).</summary>
    public bool KillTasksShowing => Tab == TabQuests && _c.Settings.QuestGuideView && _gTab == GuideTab.KillTasks && _c.Server.IsIlt;

    /// <summary>
    /// The task's counter flag: one of its stamps that /myquests holds with a solve limit over 1 (a kill
    /// counter, "KillTaskDrudgeLurkers_0507" 37 of 100); else, for the few pages that list no stamps, a kill
    /// counter flag that names the task's creature.
    /// </summary>
    internal QuestFlag? KillCounter(GuideQuest q)
    {
        foreach (string st in q.AllStamps())
            if (StampFlag(st) is QuestFlag f && f.MaxSolves > 1) return f;
        string key = q.KillCreatureKey;
        if (key.Length < 5) return null;
        foreach (QuestFlag f in _c.Flags.Flags.Values)
            if (QuestTimer.IsKillTask(f) && GuideStamp.Key(f.Key).Contains(key, StringComparison.Ordinal)) return f;
        return null;
    }

    /// <summary>A wiki kill task's progress (internal for the tests).</summary>
    internal KillProgress KillProgressOf(GuideQuest q)
    {
        var p = new KillProgress { Goal = q.KillTarget };
        GuideRow row = WikiRow(q);
        QuestFlag? counter = KillCounter(q);
        p.Counter = counter;
        if (counter != null)
        {
            p.Kills = counter.Solves;
            if (counter.MaxSolves > 1) p.Goal = counter.MaxSolves;
        }
        // A kill line newer than the last /myquests read wins (it moves with every kill).
        if (_c.Kills.For(q.KillCreatureKey) is KillCount k && k.AtUtc > _c.Flags.LastReadUtc && (k.Goal > 0 || k.Kills > 0))
        {
            p.Kills = k.Kills;
            if (k.Goal > 0) p.Goal = k.Goal;
            p.FromChat = true;
        }

        TimeSpan wait = WikiWait(q);
        bool counting = (counter != null || p.FromChat) && p.Goal > 0 && p.Kills < p.Goal;
        bool allKilled = (counter != null || p.FromChat) && p.Goal > 0 && p.Kills >= p.Goal;
        if (counting && !(row.Done && wait > TimeSpan.Zero))
        {
            p.State = KillState.Counting;
            p.Status = $"{p.Kills} / {p.Goal}";
            p.Color = UiColors.Yellow;
        }
        else if (allKilled && wait <= TimeSpan.Zero && !row.Done)
        {
            p.State = KillState.TurnIn;
            p.Status = "Turn in";
            p.Color = UiColors.Gold;
        }
        else if (row.Done && wait > TimeSpan.Zero)
        {
            p.State = KillState.Waiting;
            p.Status = "Done " + Short(wait);
            p.Color = UiColors.Yellow;
        }
        else if (row.Done && q.RepeatSec == 0)
        {
            p.State = KillState.Completed;
            p.Status = "Completed";
            p.Color = UiColors.Green;
        }
        else if (row.Status == "Ready" || row.Done || counter != null)
        {
            p.State = KillState.Ready;
            p.Status = "Ready";
            p.Color = UiColors.Green;
        }
        else
        {
            p.State = KillState.NotStarted;
            p.Status = p.Goal > 0 ? $"0 / {p.Goal}" : "Not started";
            p.Color = UiColors.Grey;
        }
        return p;
    }

    /// <summary>The Kill Tasks list: the wiki's kill tasks, then the counters only /myquests knows. Each by state, then name.</summary>
    private void KillTaskRows(string[] terms, bool hide)
    {
        var order = new Dictionary<string, (int State, string Label)>(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GuideQuest q in _c.Guide.Guide.Quests)
        {
            if (!_c.Guide.Guide.IsKillTask(q)) continue;
            KillProgress p = KillProgressOf(q);
            if (p.Counter != null) named.Add(p.Counter.Key);
            foreach (string st in q.AllStamps()) named.Add(st);
            if (!MatchesAll(WikiHay(q), terms)) continue;
            bool done = p.State is KillState.Waiting or KillState.Completed;
            if (hide && done) continue;
            var r = new GuideRow { Key = "w:" + q.Title, Label = q.Title, W = q, Done = done, Status = p.Status, Color = p.Color };
            _gRows.Add(r);
            order[r.Key] = ((int)p.State, r.Label);
        }

        // Counters the server keeps that no wiki kill task names.
        var namedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string n in named) namedKeys.Add(GuideStamp.Key(n));
        foreach (QuestFlag f in _c.Flags.Flags.Values)
        {
            if (!QuestTimer.IsKillTask(f) || namedKeys.Contains(GuideStamp.Key(f.Key))) continue;
            if (!MatchesAll(f.Key + " " + f.Description, terms)) continue;
            QuestTimerState st = QuestTimer.State(f, true, _c.UtcNow, out string text);
            KillState ks = st switch
            {
                QuestTimerState.InProgress => KillState.Counting,
                QuestTimerState.Waiting => KillState.Waiting,
                QuestTimerState.Done => KillState.Completed,
                _ => KillState.Ready,
            };
            bool done = ks is KillState.Waiting or KillState.Completed;
            if (hide && done) continue;
            var r = new GuideRow
            {
                Key = "k:" + f.Key, Label = FlagLabel(f.Key), Flag = f, Done = done,
                Status = ks == KillState.Counting ? $"{f.Solves} / {f.MaxSolves}" : ks == KillState.Waiting ? "Done " + text : ks == KillState.Completed ? "Completed" : "Ready",
                Color = ks switch { KillState.Counting or KillState.Waiting => UiColors.Yellow, KillState.Completed or KillState.Ready => UiColors.Green, _ => UiColors.Grey },
            };
            _gRows.Add(r);
            order[r.Key] = ((int)ks, r.Label);
        }

        _gRows.Sort((a, b) =>
        {
            (int sa, string la) = order[a.Key];
            (int sb, string lb) = order[b.Key];
            return sa != sb ? sa.CompareTo(sb) : string.Compare(la, lb, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>"KillTaskDrudgeLurkers_0507" -> "Kill Task Drudge Lurkers 0507".</summary>
    internal static string FlagLabel(string flag)
    {
        var sb = new System.Text.StringBuilder(flag.Length + 8);
        for (int i = 0; i < flag.Length; i++)
        {
            char c = flag[i];
            if (c == '_') { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c) && (char.IsLower(flag[i - 1]) || char.IsDigit(flag[i - 1])) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            else if (i > 0 && char.IsDigit(c) && char.IsLetter(flag[i - 1]) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>A line over the list: how many tasks are counting, to turn in and ready.</summary>
    private void DrawKillSummary(UiWindow w)
    {
        int counting = 0, turnIn = 0, ready = 0;
        foreach (GuideQuest q in _c.Guide.Guide.Quests)
        {
            if (!_c.Guide.Guide.IsKillTask(q)) continue;
            switch (KillProgressOf(q).State)
            {
                case KillState.Counting: counting++; break;
                case KillState.TurnIn: turnIn++; break;
                case KillState.Ready: ready++; break;
            }
        }
        string read = _c.Flags.LastReadUtc == DateTime.MinValue ? "not read yet" : "read " + Short(_c.UtcNow - _c.Flags.LastReadUtc) + " ago";
        w.TextDisabled($"Kill tasks: {counting} counting, {turnIn} to turn in, {ready} ready  |  /myquests {read}");
        w.Tooltip("The kills come from your quest flags (/myquests): each task's counter flag. A kill line in chat\n"
                  + "(\"You have killed 37 Drudge Lurkers! You must kill 100 ...\") shows at once, and with this list open\n"
                  + "the flags are read again after kills (at most once a minute: the server's limit). Refresh reads them now.");
    }

    /// <summary>The kill count over an opened wiki kill task.</summary>
    private void DrawKillLine(UiWindow w, GuideQuest q)
    {
        KillProgress p = KillProgressOf(q);
        string count = p.Counter != null || p.FromChat
            ? $"Kills: {p.Kills} / {(p.Goal > 0 ? p.Goal.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?")}"
              + (p.Goal > p.Kills ? $"  ({p.Goal - p.Kills} left)" : "")
            : p.Goal > 0 ? $"Kills: not started (the task is {p.Goal} kills)" : "Kills: not started";
        w.TextColored(p.Color, count + "  -  " + p.Status);
        string from = p.FromChat ? "from the last kill line in chat"
            : p.Counter != null ? $"from /myquests: {p.Counter.Key} ({p.Counter.Solves} of {p.Counter.MaxSolves})"
            : "no counter flag in /myquests yet (talk to " + (q.StartsWith.Length > 0 ? q.StartsWith : "the task's NPC") + " to start it)";
        w.TextDisabled(from);
    }

    /// <summary>A kill counter only /myquests knows.</summary>
    private void DrawKillFlag(UiWindow w, QuestFlag f)
    {
        w.TextColored(UiColors.Gold, FlagLabel(f.Key));
        QuestTimerState st = QuestTimer.State(f, true, _c.UtcNow, out string text);
        w.Text($"Kills: {f.Solves} / {f.MaxSolves}" + (f.Solves < f.MaxSolves ? $"  ({f.MaxSolves - f.Solves} left)" : ""));
        w.TextDisabled("State: " + (st == QuestTimerState.Waiting ? "again in " + text : text));
        if (f.RepeatTime > TimeSpan.Zero) w.TextDisabled("Timer: " + Short(f.RepeatTime));
        if (f.Description.Length > 0) w.TextWrapped(f.Description);
        w.TextDisabled("Flag " + f.Key + " - no wiki kill task names this counter.");
    }
}
