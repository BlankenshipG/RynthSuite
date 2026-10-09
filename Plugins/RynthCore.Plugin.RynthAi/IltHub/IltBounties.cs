// IltBounties.cs - ILT Hub "Bounties" window: every ACECustom bounty and this character's progress.
//
// The server keeps the counts (PropertyString BountyProgress is server-only), so the tracker reads
// "/bounty list", which reports every active bounty on the server - wherever the player stands -
// with the player's kills, cooldown or QB requirement for each. The read is a quiet capture
// (IltChatCapture eats the reply) and runs only while the window is open:
//   * when the window opens (and the list is old or kills happened while it was closed),
//   * a moment after each of this character's kills (at most every MinRefreshSeconds),
//   * after a bounty chat event ("Bounty complete!", "Bounty unlocked:", "Next bounty ..."),
//   * every PeriodicRefreshMinutes as a backstop, and on Refresh / "/ra bounty refresh".
// Lines the player prints themselves ("/bounty list", "/bounty") update the window too.
// Cooldowns count down locally from the read time; when one ends the bounty shows 0 of N kills,
// which is what the server says too (an award resets the count before the cooldown starts).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal sealed class IltBounties : IIltFeature
{
    /// <summary>The capture command; "/bounty all" is the same list.</summary>
    public const string ListCommand = "/bounty list";
    /// <summary>Lowest allowed MinRefreshSeconds (the server is asked at most this often after kills).</summary>
    public const int MinRefreshFloorSeconds = 5;

    /// <summary>Wait after a kill so a burst of kills (and the server's own award lines) lands in one read.</summary>
    private const long KillDebounceMs = 2000;
    /// <summary>Wait after a bounty chat event before re-reading.</summary>
    private const long EventDebounceMs = 1500;
    /// <summary>A list older than this is re-read when the window opens.</summary>
    private static readonly TimeSpan OpenRefreshAge = TimeSpan.FromSeconds(30);
    /// <summary>How long a row stays highlighted after its kills went up.</summary>
    private static readonly TimeSpan ChangedHighlight = TimeSpan.FromSeconds(15);

    private readonly IltHubContext _ctx;

    // Pump-thread scheduling state.
    private long _dueAtMs;       // 0 = no read scheduled
    private long _lastSentMs;    // 0 = never this session
    private bool _wasOpen;
    private bool _stale;         // kills or bounty events happened while the window was closed

    // Written on the pump thread, read by the render thread.
    private volatile IltBountyEntry[] _view = Array.Empty<IltBountyEntry>();
    private volatile bool _unsupported;
    private volatile bool _refreshing;
    private volatile string _status = string.Empty;

    public IltBounties(IltHubContext ctx)
    {
        _ctx = ctx;
        RebuildView();
    }

    private IltBountyState S => _ctx.State.Bounty;

    /// <summary>True once the server answered "Unknown command: bounty" (no bounty system on this server).</summary>
    public bool Unsupported => _unsupported;

    /// <summary>Current rows (render-thread safe copies, server order).</summary>
    public IReadOnlyList<IltBountyEntry> Snapshot => _view;

    private bool WindowOpen => _ctx.State.Character.BountiesWindowOpen
                               && _ctx.Options.IsIltLikeWorld && _ctx.Options.AnyFeatureOn;

    // ── Scheduling (pump thread) ────────────────────────────────────────────

    /// <summary>Schedules a "/bounty list" read; the earliest pending request wins.</summary>
    private void Schedule(long nowMs, long delayMs, bool respectMinInterval)
    {
        long due = nowMs + delayMs;
        if (respectMinInterval && _lastSentMs != 0)
            due = Math.Max(due, _lastSentMs + S.MinRefreshSeconds * 1000L);
        if (_dueAtMs == 0 || due < _dueAtMs) _dueAtMs = due;
    }

    /// <summary>Manual refresh (button, "/ra bounty refresh"): ignores the interval and retries an unsupported server.</summary>
    public string RequestRefresh()
    {
        _unsupported = false;
        Schedule(IltHubContext.NowMs, 0, respectMinInterval: false);
        // A closed window does not tick reads, so a manual request sends now.
        if (!WindowOpen) Send(IltHubContext.NowMs);
        return "Reading /bounty list...";
    }

    /// <summary>One of this character's kills (RynthAiPlugin.OnKillNotification). Pump thread.</summary>
    public void OnKill()
    {
        if (!S.RefreshOnKills) return;
        if (WindowOpen) Schedule(IltHubContext.NowMs, KillDebounceMs, respectMinInterval: true);
        else _stale = true;
    }

    private void Send(long nowMs)
    {
        _dueAtMs = 0;
        if (_ctx.Capture.IsPending(ListCommand)) return;
        _lastSentMs = nowMs;
        _refreshing = true;
        RynthLog.Trace(LogCat.IltBounties, "reading /bounty list");
        _ctx.Capture.Enqueue(new IltChatRequest
        {
            Command = ListCommand,
            IsResponseLine = IsListReplyLine,
            IsTerminator = IltBountyParse.IsListTerminator,
            IdleEndMs = 1500,
            FirstLineTimeoutMs = 10000,
            Eat = true,
            OnComplete = OnListComplete,
        });
    }

    private static bool IsListReplyLine(string text)
        => text == IltBountyParse.ListHeader || IltBountyParse.TryParseListLine(text, out _);

    private void OnListComplete(IltChatResult r)
    {
        _refreshing = false;
        if (r.NotSent) { _status = "this client cannot send chat commands"; return; }
        if (r.UnknownCommand)
        {
            _unsupported = true;
            _status = "this server has no /bounty command";
            RynthLog.Write(LogCat.IltBounties, "[IltBounties] server has no /bounty command; automatic reads stopped.");
            return;
        }
        if (r.TimedOut) { _status = "no reply to /bounty list"; return; }

        var lines = new List<IltBountyListLine>();
        foreach (string l in r.Lines)
            if (IltBountyParse.TryParseListLine(l, out var parsed)) lines.Add(parsed);
        bool complete = r.TerminatorLine != null;
        bool none = complete && IltBountyParse.IsNoBountiesAnywhere(r.TerminatorLine!);
        ApplyList(lines, complete, none, DateTime.UtcNow);
        _status = complete ? string.Empty : "partial reply";
        RynthLog.Trace(LogCat.IltBounties, $"/bounty list: {lines.Count} reward(s), complete={complete}, none={none}");
    }

    // ── Applying server output (pump thread; internal for the host tests) ──

    /// <summary>
    /// Applies parsed "/bounty list" lines. A complete reply (footer seen) replaces the list in
    /// server order; a partial one only updates or appends the rewards it names.
    /// </summary>
    internal void ApplyList(IReadOnlyList<IltBountyListLine> lines, bool complete, bool noneAnywhere, DateTime nowUtc)
    {
        var old = new Dictionary<string, IltBountyEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in S.Entries)
            old[IltBountyParse.Key(e.Where, e.Amount, e.Item, e.KillsRequired)] = e;

        var fresh = new List<IltBountyEntry>(lines.Count);
        foreach (var l in lines)
        {
            string key = IltBountyParse.Key(l.Where, l.Amount, l.Item, l.KillsRequired);
            old.TryGetValue(key, out var prev);
            var e = new IltBountyEntry
            {
                Where = l.Where,
                Amount = l.Amount,
                Item = l.Item,
                KillsRequired = l.KillsRequired,
                CooldownSeconds = l.CooldownSeconds,
                ChangedUtc = prev?.ChangedUtc ?? default,
            };
            ApplyStatus(e, l.Status, prev, nowUtc);
            fresh.Add(e);
        }

        if (complete)
        {
            S.Entries = fresh;
        }
        else
        {
            foreach (var e in fresh)
            {
                int i = S.Entries.FindIndex(x => string.Equals(IltBountyParse.Key(x.Where, x.Amount, x.Item, x.KillsRequired),
                                                               IltBountyParse.Key(e.Where, e.Amount, e.Item, e.KillsRequired),
                                                               StringComparison.OrdinalIgnoreCase));
                if (i >= 0) S.Entries[i] = e; else S.Entries.Add(e);
            }
        }
        if (complete || lines.Count > 0) S.ListedUtc = nowUtc;
        if (complete) S.NoneAnywhere = noneAnywhere;
        RebuildView();
    }

    /// <summary>
    /// Applies a "/bounty" (where you stand) line to the one listed reward it can only mean.
    /// Returns false when no reward or more than one matches.
    /// </summary>
    internal bool ApplyHere(IltBountyHereLine h, DateTime nowUtc)
    {
        IltBountyEntry? match = null;
        foreach (var e in S.Entries)
        {
            if (e.Amount != h.Amount || !string.Equals(e.Item, h.Item, StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Status.KillsRequired > 0 && e.KillsRequired != h.Status.KillsRequired) continue;
            bool allZones = string.Equals(e.Where, "All zones", StringComparison.OrdinalIgnoreCase);
            bool scopeOk = h.Scope.Length == 0 ? !allZones
                : h.Scope.Equals("all zones", StringComparison.OrdinalIgnoreCase) ? allZones
                : string.Equals(e.Where, h.Scope, StringComparison.OrdinalIgnoreCase);
            if (!scopeOk) continue;
            if (match != null) return false;   // ambiguous: two areas pay the same reward
            match = e;
        }
        if (match == null) return false;

        var prev = new IltBountyEntry { Status = match.Status, Kills = match.Kills };
        ApplyStatus(match, h.Status, prev, nowUtc);
        RebuildView();
        return true;
    }

    /// <summary>Copies a parsed status onto <paramref name="e"/> and stamps ChangedUtc when kills went up or an award reset them.</summary>
    private static void ApplyStatus(IltBountyEntry e, IltBountyStatusInfo st, IltBountyEntry? prev, DateTime nowUtc)
    {
        e.Status = (int)st.Status;
        e.Kills = st.Status == IltBountyStatus.Counting ? st.Kills : 0;
        e.UnlockAtUtc = st.Status == IltBountyStatus.Cooldown ? nowUtc.AddSeconds(st.WaitSeconds) : default;
        e.QbRequired = st.QbRequired;
        e.QbHave = st.QbHave;
        if (prev == null) return;
        bool wasCounting = prev.Status == (int)IltBountyStatus.Counting;
        bool gained = wasCounting && st.Status == IltBountyStatus.Counting && st.Kills > prev.Kills;
        bool awarded = wasCounting && prev.Kills > 0
                       && (st.Status == IltBountyStatus.Cooldown || (st.Status == IltBountyStatus.Counting && st.Kills < prev.Kills));
        if (gained || awarded) e.ChangedUtc = nowUtc;
    }

    /// <summary>Copies the persisted list for the render thread.</summary>
    private void RebuildView()
    {
        _view = S.Entries.Select(e => new IltBountyEntry
        {
            Where = e.Where, Amount = e.Amount, Item = e.Item, KillsRequired = e.KillsRequired,
            CooldownSeconds = e.CooldownSeconds, Status = e.Status, Kills = e.Kills, UnlockAtUtc = e.UnlockAtUtc,
            QbRequired = e.QbRequired, QbHave = e.QbHave, ChangedUtc = e.ChangedUtc,
        }).ToArray();
    }

    /// <summary>The status to show now: a cooldown that has ended is "0 of N kills".</summary>
    internal static (IltBountyStatus Status, int Kills) Effective(IltBountyEntry e, DateTime nowUtc)
    {
        var st = (IltBountyStatus)e.Status;
        if (st == IltBountyStatus.Cooldown && nowUtc >= e.UnlockAtUtc) return (IltBountyStatus.Counting, 0);
        return (st, st == IltBountyStatus.Counting ? e.Kills : 0);
    }

    /// <summary>One-line chat text for an entry ("/ra bounty list").</summary>
    internal static string Describe(IltBountyEntry e, DateTime nowUtc)
    {
        var (st, kills) = Effective(e, nowUtc);
        string reward = $"{IltParse.N0(e.Amount)} {e.Item}";
        return st switch
        {
            IltBountyStatus.Counting => $"{e.Where}: {kills:N0}/{e.KillsRequired:N0} kills -> {reward}",
            IltBountyStatus.Cooldown => $"{e.Where}: unlocks in {IltParse.Duration(e.UnlockAtUtc - nowUtc)}, then {e.KillsRequired:N0} kills -> {reward}",
            _ => $"{e.Where}: needs {IltParse.N0(e.QbRequired)} QB (you have {IltParse.N0(e.QbHave)}) -> {reward}",
        };
    }

    /// <summary>"/ra bounty list": the tracked bounties in chat.</summary>
    public void PrintList()
    {
        var rows = _view;
        if (_unsupported) { _ctx.Chat("[Bounties] This server has no /bounty command."); return; }
        if (rows.Length == 0)
        {
            _ctx.Chat(S.NoneAnywhere ? "[Bounties] No bounties are active anywhere right now."
                : "[Bounties] Nothing read yet. Type /ra bounty refresh (or open the window with /ra bounty).");
            return;
        }
        var now = DateTime.UtcNow;
        string age = S.ListedUtc == default ? "never" : IltParse.Duration(now - S.ListedUtc) + " ago";
        _ctx.Chat($"[Bounties] {rows.Length} bounty reward(s), read {age}:");
        foreach (var e in rows) _ctx.Chat("[Bounties]   " + Describe(e, now));
    }

    // ── IIltFeature ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        bool open = WindowOpen;
        if (!open) { _wasOpen = false; _dueAtMs = 0; return; }

        if (!_wasOpen)
        {
            _wasOpen = true;
            if (_stale || _lastSentMs == 0 || DateTime.UtcNow - S.ListedUtc > OpenRefreshAge)
                Schedule(nowMs, 0, respectMinInterval: false);
            _stale = false;
        }

        if (S.PeriodicRefreshMinutes > 0 && _lastSentMs != 0 && nowMs - _lastSentMs >= S.PeriodicRefreshMinutes * 60_000L)
            Schedule(nowMs, 0, respectMinInterval: false);

        if (_dueAtMs != 0 && nowMs >= _dueAtMs && !_unsupported) Send(nowMs);
    }

    public bool OnChat(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return false;

        // The player's own "/bounty list" / "/bounty" output; our captured reply is applied in OnListComplete.
        if (IltBountyParse.TryParseListLine(t, out var listLine))
        {
            if (!_ctx.Capture.IsPending(ListCommand)) ApplyList(new[] { listLine }, complete: false, noneAnywhere: false, DateTime.UtcNow);
            return false;
        }
        if (IltBountyParse.TryParseHereLine(t, out var here))
        {
            ApplyHere(here, DateTime.UtcNow);
            return false;
        }

        if (IltBountyParse.TryParseEvent(t, out var kind, out _, out _, out _))
        {
            S.LastEvent = t;
            S.LastEventUtc = DateTime.UtcNow;
            RynthLog.Trace(LogCat.IltBounties, $"event {kind}: {t}");
            if (kind != IltBountyEventKind.Held)
            {
                if (WindowOpen) Schedule(IltHubContext.NowMs, EventDebounceMs, respectMinInterval: false);
                else _stale = true;
            }
        }
        return false;
    }

    public void OnLogout()
    {
        _dueAtMs = 0;
        _wasOpen = false;
        _refreshing = false;
    }

    // ── Window body (render thread) ─────────────────────────────────────────

    private static readonly Vector4 BarChanged = new(0.15f, 0.85f, 0.90f, 0.85f);
    private static readonly Vector4 BarNormal = new(0.25f, 0.60f, 0.35f, 0.85f);

    public void Render()
    {
        var s = S;
        var now = DateTime.UtcNow;

        if (ImGui.SmallButton("Refresh##bounty")) _ctx.Post(() => RequestRefresh());
        ImGui.SameLine();
        string read = _refreshing ? "reading /bounty list..."
            : _status.Length > 0 ? _status
            : s.ListedUtc == default ? "not read yet"
            : "updated " + IltParse.Duration(now - s.ListedUtc) + " ago";
        ImGui.TextDisabled(read);
        ImGui.SameLine();
        if (ImGui.SmallButton("Options##bounty")) ImGui.OpenPopup("##bountyopts");
        RenderOptionsPopup(s);

        string filter = s.Filter;
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputTextWithHint("##bountyfilter", "filter by area or reward", ref filter, 64u)) s.Filter = filter;
        ImGui.SameLine();
        bool hideQb = s.HideQbLocked;
        if (ImGui.Checkbox("Hide QB-locked", ref hideQb)) s.HideQbLocked = hideQb;
        ImGui.SameLine();
        bool byProgress = s.SortByProgress;
        if (ImGui.Checkbox("Closest first", ref byProgress)) s.SortByProgress = byProgress;

        if (s.LastEvent.Length > 0)
        {
            ImGui.TextColored(LegacyDashboardRenderer.ColAmber, s.LastEvent);
            ImGui.SameLine();
            ImGui.TextDisabled("(" + IltParse.Duration(now - s.LastEventUtc) + " ago)");
        }
        ImGui.Separator();

        if (_unsupported)
        {
            ImGui.TextWrapped("This server has no /bounty command, so there is nothing to track. Press Refresh to check again.");
            return;
        }

        var rows = _view;
        if (rows.Length == 0)
        {
            ImGui.TextWrapped(s.NoneAnywhere ? "No bounties are active anywhere right now."
                : _refreshing ? "Reading the server's bounties..."
                : "No bounties read yet. Press Refresh.");
            return;
        }

        IEnumerable<IltBountyEntry> shown = rows;
        if (filter.Length > 0)
            shown = shown.Where(e => e.Where.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                     || e.Item.Contains(filter, StringComparison.OrdinalIgnoreCase));
        if (hideQb) shown = shown.Where(e => e.Status != (int)IltBountyStatus.NeedsQb);
        if (byProgress) shown = shown.OrderBy(e => SortKey(e, now));

        if (ImGui.BeginTable("##iltbounties", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV
                                                 | ImGuiTableFlags.Resizable, new Vector2(0, -ImGui.GetTextLineHeightWithSpacing() * 1.3f)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Area");
            ImGui.TableSetupColumn("Reward");
            ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthFixed, 190);
            ImGui.TableHeadersRow();
            foreach (var e in shown)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(e.Where);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{IltParse.N0(e.Amount)} {e.Item}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Every {e.KillsRequired:N0} kill(s)"
                                     + (e.CooldownSeconds > 0 ? $", at most every {IltParse.Duration(TimeSpan.FromSeconds(e.CooldownSeconds))}" : ""));
                ImGui.TableNextColumn();
                RenderProgress(e, now);
            }
            ImGui.EndTable();
        }
        ImGui.TextDisabled("Kills count in the bounty's own area only (XP/lum mobs you did the most damage to).");
    }

    private static void RenderProgress(IltBountyEntry e, DateTime now)
    {
        var (st, kills) = Effective(e, now);
        switch (st)
        {
            case IltBountyStatus.Counting:
                float frac = e.KillsRequired > 0 ? Math.Clamp(kills / (float)e.KillsRequired, 0f, 1f) : 0f;
                bool changed = e.ChangedUtc != default && now - e.ChangedUtc < ChangedHighlight;
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, changed ? BarChanged : BarNormal);
                ImGui.ProgressBar(frac, new Vector2(-1, 0), $"{kills:N0} / {e.KillsRequired:N0}");
                ImGui.PopStyleColor();
                break;
            case IltBountyStatus.Cooldown:
                ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "unlocks in " + IltParse.Duration(e.UnlockAtUtc - now));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Kills don't count until then; after that {e.KillsRequired:N0} kill(s).");
                break;
            default:
                ImGui.TextDisabled($"needs {IltParse.Compact(e.QbRequired)} QB");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Needs {IltParse.N0(e.QbRequired)} QB; you have {IltParse.N0(e.QbHave)}.");
                break;
        }
    }

    /// <summary>"Closest first" order: counting by fraction left, then cooldowns by time left, then QB-locked.</summary>
    private static double SortKey(IltBountyEntry e, DateTime now)
    {
        var (st, kills) = Effective(e, now);
        return st switch
        {
            IltBountyStatus.Counting => e.KillsRequired > 0 ? 1.0 - kills / (double)e.KillsRequired : 1.0,
            IltBountyStatus.Cooldown => 2.0 + Math.Min((e.UnlockAtUtc - now).TotalSeconds, 1e9) / 1e9,
            _ => 4.0,
        };
    }

    private void RenderOptionsPopup(IltBountyState s)
    {
        if (!ImGui.BeginPopup("##bountyopts")) return;
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Bounty tracking");
        bool onKills = s.RefreshOnKills;
        if (ImGui.Checkbox("Update after my kills", ref onKills)) s.RefreshOnKills = onKills;
        int minSec = s.MinRefreshSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("at most every (s)##bountymin", ref minSec, MinRefreshFloorSeconds, 120))
            s.MinRefreshSeconds = Math.Clamp(minSec, MinRefreshFloorSeconds, 300);
        int periodic = s.PeriodicRefreshMinutes;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("re-read every (min, 0 = off)##bountyper", ref periodic, 0, 60))
            s.PeriodicRefreshMinutes = Math.Clamp(periodic, 0, 60);
        ImGui.TextDisabled("Reads run only while this window is open;\nthe /bounty list reply is hidden from chat.");
        ImGui.EndPopup();
    }
}
