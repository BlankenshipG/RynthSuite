// BountyTracker.cs - Every ACECustom bounty and this character's progress, for the engine's Bounties window.
//
// The server keeps the counts (PropertyString BountyProgress is server-only), so the tracker reads
// "/bounty list", which reports every active bounty on the server - wherever the player stands -
// with the player's kills, cooldown or QB requirement for each. The read is a quiet capture (the
// reply is eaten from chat) and runs only while the engine's Bounties window polls the export:
//   * when the window opens (and the list is old or kills happened while it was closed),
//   * a moment after each of this character's kills,
//   * after a bounty chat event ("Bounty complete!", "Bounty unlocked:", "Next bounty ..."),
//   * every PeriodicRefreshMinutes as a backstop, and on Refresh / "/ilt bounty refresh".
// Every read, whatever asked for it, waits for MinRefreshSeconds (at least 5 s) since the last
// one, and none is sent while the window is closed (RynthILT review, 2026-10-09).
// Lines the player prints themselves ("/bounty list", "/bounty") update the list too, and are
// never eaten: a /bounty the player types (NoteTypedCommand) turns the capture it overlaps into
// a shown one, and a typed "/bounty list" stands in for the tracker's next read.
// Cooldowns count down locally from the read time; when one ends the bounty shows 0 of N kills,
// which is what the server says too (an award resets the count before the cooldown starts).
//
// Everything runs on the plugin's game thread: OnTick, chat, kills and the engine's export polls.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.Plugin.RynthILT.Bounty;

/// <summary>Persisted per character (bounties.json): the window's options and the last list read.</summary>
internal sealed class BountyState
{
    public bool RefreshOnKills = true;
    public int MinRefreshSeconds = 10;
    public int PeriodicRefreshMinutes = 5;
    public bool HideQbLocked;
    public bool SortByProgress;
    public List<BountyEntry> Entries = new();
    public DateTime ListedUtc;
    public bool NoneAnywhere;
    public string LastEvent = string.Empty;
    public DateTime LastEventUtc;
}

/// <summary>One reward of one bounty area and where this character stands on it.</summary>
internal sealed class BountyEntry
{
    public string Where = string.Empty;
    public long Amount;
    public string Item = string.Empty;
    public int KillsRequired;
    public int CooldownSeconds;
    /// <summary>A <see cref="BountyStatus"/> value.</summary>
    public int Status;
    public int Kills;
    public DateTime UnlockAtUtc;
    public long QbRequired;
    public long QbHave;
    /// <summary>Last time the kills went up or an award reset them (row highlight).</summary>
    public DateTime ChangedUtc;
}

/// <summary>
/// RynthPluginGetBountiesJson payload. Mirrored by RynthCore.Engine UI/Data/BountiesSnapshotJson.cs;
/// either side may add fields (unknown ones are skipped, missing ones keep their defaults).
/// </summary>
internal sealed class BountyBridgePayload
{
    [JsonPropertyName("unsupported")] public bool Unsupported;
    [JsonPropertyName("reading")] public bool Reading;
    [JsonPropertyName("status")] public string Status = string.Empty;
    /// <summary>Seconds since the last list read; -1 = never.</summary>
    [JsonPropertyName("listedAgoSec")] public int ListedAgoSec = -1;
    [JsonPropertyName("noneAnywhere")] public bool NoneAnywhere;
    [JsonPropertyName("lastEvent")] public string LastEvent = string.Empty;
    [JsonPropertyName("lastEventAgoSec")] public int LastEventAgoSec = -1;
    [JsonPropertyName("refreshOnKills")] public bool RefreshOnKills;
    [JsonPropertyName("minRefreshSeconds")] public int MinRefreshSeconds;
    [JsonPropertyName("periodicRefreshMinutes")] public int PeriodicRefreshMinutes;
    [JsonPropertyName("hideQbLocked")] public bool HideQbLocked;
    [JsonPropertyName("sortByProgress")] public bool SortByProgress;
    /// <summary>Rows not shown because of HideQbLocked.</summary>
    [JsonPropertyName("hiddenCount")] public int HiddenCount;
    [JsonPropertyName("rows")] public List<BountyRow> Rows = new();
}

/// <summary>One row of <see cref="BountyBridgePayload"/>, with the status as of the poll.</summary>
internal sealed class BountyRow
{
    [JsonPropertyName("where")] public string Where = string.Empty;
    [JsonPropertyName("amount")] public long Amount;
    [JsonPropertyName("item")] public string Item = string.Empty;
    [JsonPropertyName("killsRequired")] public int KillsRequired;
    [JsonPropertyName("cooldownSeconds")] public int CooldownSeconds;
    /// <summary>A <see cref="BountyStatus"/> value; an ended cooldown is already Counting.</summary>
    [JsonPropertyName("status")] public int Status;
    [JsonPropertyName("kills")] public int Kills;
    [JsonPropertyName("unlockInSec")] public int UnlockInSec;
    [JsonPropertyName("qbRequired")] public long QbRequired;
    [JsonPropertyName("qbHave")] public long QbHave;
    /// <summary>Kills went up (or an award reset them) in the last 15 seconds.</summary>
    [JsonPropertyName("changed")] public bool Changed;
}

internal sealed class BountyTracker
{
    /// <summary>The capture command; "/bounty all" is the same list.</summary>
    public const string ListCommand = "/bounty list";
    /// <summary>Lowest allowed MinRefreshSeconds (the server is asked at most this often after kills).</summary>
    public const int MinRefreshFloorSeconds = 5;
    public const int MinRefreshCeilingSeconds = 300;
    public const int PeriodicCeilingMinutes = 60;
    public const string FileName = "bounties.json";

    /// <summary>Wait after a kill so a burst of kills (and the server's own award lines) lands in one read.</summary>
    private const long KillDebounceMs = 2000;
    /// <summary>Wait after a bounty chat event before re-reading.</summary>
    private const long EventDebounceMs = 1500;
    /// <summary>The window counts as open while the engine polled the export this recently (it polls every second).</summary>
    private const long WindowPollWindowMs = 3000;
    /// <summary>A capture with no reply line by then is abandoned.</summary>
    private const long FirstLineTimeoutMs = 10000;
    /// <summary>A capture whose footer never came ends this long after its last line.</summary>
    private const long IdleEndMs = 1500;
    /// <summary>The engine may pass our own /bounty list back through the chat bar hook this soon after sending it.</summary>
    private const long OwnEchoMs = 2000;
    /// <summary>Longer chat lines are never parsed (no bounty line is near this; bounds the regex work).</summary>
    private const int MaxLineLength = 512;
    private const long SaveDebounceMs = 5000;
    /// <summary>A list older than this is re-read when the window opens.</summary>
    private static readonly TimeSpan OpenRefreshAge = TimeSpan.FromSeconds(30);
    /// <summary>How long a row stays highlighted after its kills went up.</summary>
    private static readonly TimeSpan ChangedHighlight = TimeSpan.FromSeconds(15);

    private readonly Action<string> _send;
    private readonly Action<string> _chat;
    private readonly Action<string> _log;
    private readonly string _filePath;
    private readonly Func<long> _clock;
    private BountyState _s;

    // Scheduling.
    private long _dueAtMs;       // 0 = no read scheduled
    private long _lastSentMs;    // 0 = never this session
    private long _lastPollMs;    // last engine poll of the export; 0 = never
    private bool _wasOpen;
    private bool _stale;         // kills or bounty events happened while the window was closed

    // The quiet "/bounty list" capture.
    private bool _capActive;
    private bool _capShown;      // the player asked too (NoteTypedCommand): parse, never eat
    private bool _expectEcho;    // our /bounty list may come back through the chat bar hook
    private long _capSentMs;
    private long _capLastLineMs; // 0 = no reply line yet
    private readonly List<BountyListLine> _capLines = new();

    private bool _unsupported;
    private string _status = string.Empty;
    private bool _dirty;
    private long _lastSaveMs;

    /// <param name="send">Sends a chat command to the server (Host.InvokeChatParser).</param>
    /// <param name="chat">Prints a line to the player's chat window.</param>
    /// <param name="log">Writes to the RynthCore log.</param>
    /// <param name="filePath">bounties.json in the character folder; empty = not persisted.</param>
    /// <param name="clock">Milliseconds clock; Environment.TickCount64 unless a test supplies one.</param>
    public BountyTracker(Action<string> send, Action<string> chat, Action<string> log, string filePath, Func<long>? clock = null)
    {
        _send = send;
        _chat = chat;
        _log = log;
        _filePath = filePath ?? string.Empty;
        _clock = clock ?? (() => Environment.TickCount64);
        _s = Load();
    }

    private long NowMs => _clock();

    /// <summary>The persisted state (host tests).</summary>
    internal BountyState State => _s;

    /// <summary>True once the server answered "Unknown command: bounty" (no bounty system on this server).</summary>
    public bool Unsupported => _unsupported;

    /// <summary>True while a "/bounty list" reply is being captured.</summary>
    public bool Reading => _capActive;

    /// <summary>True while the engine's Bounties window is polling the export.</summary>
    public bool WindowOpen => _lastPollMs != 0 && NowMs - _lastPollMs < WindowPollWindowMs;

    // ── Scheduling ──────────────────────────────────────────────────────────

    /// <summary>
    /// Schedules a "/bounty list" read; the earliest pending request wins. Every read waits for
    /// MinRefreshSeconds since the last one; <paramref name="respectMinInterval"/> false (a manual
    /// refresh) waits only for the MinRefreshFloorSeconds floor.
    /// </summary>
    private void Schedule(long nowMs, long delayMs, bool respectMinInterval)
    {
        long due = nowMs + delayMs;
        if (_lastSentMs != 0)
            due = Math.Max(due, _lastSentMs + (respectMinInterval ? _s.MinRefreshSeconds : MinRefreshFloorSeconds) * 1000L);
        if (_dueAtMs == 0 || due < _dueAtMs) _dueAtMs = due;
    }

    /// <summary>
    /// Manual refresh (window button, "/ilt bounty refresh"): retries an unsupported server and
    /// reads as soon as the 5 s floor since the last read allows. Never sends with the window
    /// closed: the next open reads instead.
    /// </summary>
    public string RequestRefresh()
    {
        _unsupported = false;
        if (!WindowOpen)
        {
            _stale = true;
            return "The list is read while the Bounties window is open (the ILT button, or /rc bounties).";
        }
        long now = NowMs;
        Schedule(now, 0, respectMinInterval: false);
        return "Reading /bounty list...";
    }

    /// <summary>One of this character's kills (RynthILTPlugin.OnKillNotification).</summary>
    public void OnKill()
    {
        if (!_s.RefreshOnKills || _unsupported) return;
        if (WindowOpen) Schedule(NowMs, KillDebounceMs, respectMinInterval: true);
        else _stale = true;
    }

    private void Send(long nowMs)
    {
        if (_capActive) { _dueAtMs = 0; return; }
        // The invariants, whatever scheduled this: the window is open, and the last read was at
        // least MinRefreshFloorSeconds ago (else the read waits until then).
        if (!WindowOpen) { _dueAtMs = 0; return; }
        if (_lastSentMs != 0 && nowMs - _lastSentMs < MinRefreshFloorSeconds * 1000L)
        {
            _dueAtMs = _lastSentMs + MinRefreshFloorSeconds * 1000L;
            return;
        }
        _dueAtMs = 0;
        _lastSentMs = nowMs;
        StartCapture(nowMs, shown: false);
        _expectEcho = true;
        try { _send(ListCommand); }
        catch (Exception ex)
        {
            _capActive = false;
            _expectEcho = false;
            _status = "could not send /bounty list";
            _log($"[RynthILT] Bounties: sending {ListCommand} failed: {ex.Message}");
        }
    }

    private void StartCapture(long nowMs, bool shown)
    {
        _capActive = true;
        _capShown = shown;
        _capSentMs = nowMs;
        _capLastLineMs = 0;
        _capLines.Clear();
    }

    /// <summary>
    /// A "/bounty ..." the player typed (the plugin's OnChatBarEnter, passed on on the pump).
    /// Its reply is never eaten: an overlapping capture of ours becomes a shown one, and a typed
    /// "/bounty list" (or "/bounty all") starts a shown capture that counts as the next read, so
    /// the tracker doesn't send its own on top. Our own command coming back through the chat
    /// bar hook right after we sent it is recognised and ignored.
    /// </summary>
    public void NoteTypedCommand(string text)
    {
        string t = (text ?? string.Empty).Trim();
        long now = NowMs;
        if (_expectEcho && now - _capSentMs <= OwnEchoMs && string.Equals(t, ListCommand, StringComparison.OrdinalIgnoreCase))
        {
            _expectEcho = false;
            return;
        }
        _expectEcho = false;
        if (_capActive) { _capShown = true; return; }
        if (!IsListCommand(t)) return;
        _lastSentMs = now;
        _dueAtMs = 0;
        StartCapture(now, shown: true);
    }

    /// <summary>"/bounty list" or "/bounty all" (or with ACE's '@' command prefix).</summary>
    internal static bool IsListCommand(string text)
    {
        string[] parts = (text ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
               && (parts[0].Equals("/bounty", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("@bounty", StringComparison.OrdinalIgnoreCase))
               && (parts[1].Equals("list", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("all", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True for a chat-bar line that is a /bounty command (what the plugin passes to NoteTypedCommand).</summary>
    public static bool IsBountyCommand(string? text)
    {
        string t = (text ?? string.Empty).TrimStart();
        if (!t.StartsWith("/bounty", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("@bounty", StringComparison.OrdinalIgnoreCase))
            return false;
        return t.Length == 7 || t[7] == ' ';   // "/bounty" or "/bounty ...", not "/bountyhunter"
    }

    public void Tick()
    {
        long now = NowMs;
        if (_capActive)
        {
            if (_capLastLineMs == 0 && now - _capSentMs >= FirstLineTimeoutMs)
            {
                _capActive = false;
                _expectEcho = false;
                if (!_capShown) _status = "no reply to /bounty list";
            }
            else if (_capLastLineMs != 0 && now - _capLastLineMs >= IdleEndMs)
            {
                FinishCapture(terminator: null);
            }
        }

        if (_dirty && now - _lastSaveMs >= SaveDebounceMs) Save();

        bool open = WindowOpen;
        if (!open) { _wasOpen = false; _dueAtMs = 0; return; }

        if (!_wasOpen)
        {
            _wasOpen = true;
            if (_stale || _lastSentMs == 0 || DateTime.UtcNow - _s.ListedUtc > OpenRefreshAge)
                Schedule(now, 0, respectMinInterval: true);
            _stale = false;
        }

        if (_s.PeriodicRefreshMinutes > 0 && _lastSentMs != 0 && now - _lastSentMs >= _s.PeriodicRefreshMinutes * 60_000L)
            Schedule(now, 0, respectMinInterval: true);

        if (_dueAtMs != 0 && now >= _dueAtMs && !_unsupported) Send(now);
    }

    private void FinishCapture(string? terminator)
    {
        _capActive = false;
        _expectEcho = false;
        bool complete = terminator != null;
        bool none = complete && BountyParse.IsNoBountiesAnywhere(terminator!);
        ApplyList(_capLines, complete, none, DateTime.UtcNow);
        _status = complete ? string.Empty : "partial reply";
        _capLines.Clear();
    }

    // ── Chat ────────────────────────────────────────────────────────────────

    /// <summary>
    /// One chat line. Returns true when it belongs to our own "/bounty list" read and should be
    /// eaten; everything else (the player's own bounty output, award lines) stays in chat.
    /// </summary>
    public bool OnChat(string text)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0 || t.Length > MaxLineLength) return false;
        long now = NowMs;

        if (_capActive)
        {
            // A shown capture (the player asked too) parses the same lines but eats none.
            bool eat = !_capShown;
            if (BountyParse.IsUnknownCommand(t))
            {
                _capActive = false;
                _expectEcho = false;
                _capLines.Clear();
                _unsupported = true;
                _status = "this server has no /bounty command";
                _log("[RynthILT] Bounties: server has no /bounty command; automatic reads stopped.");
                return eat;
            }
            if (t == BountyParse.ListHeader) { _capLastLineMs = now; return eat; }
            if (BountyParse.TryParseListLine(t, out var captured)) { _capLines.Add(captured); _capLastLineMs = now; return eat; }
            if (BountyParse.IsListTerminator(t)) { FinishCapture(t); return eat; }
        }

        if (BountyParse.TryParseListLine(t, out var listLine))
        {
            ApplyList(new[] { listLine }, complete: false, noneAnywhere: false, DateTime.UtcNow);
            return false;
        }
        if (BountyParse.TryParseHereLine(t, out var here))
        {
            ApplyHere(here, DateTime.UtcNow);
            return false;
        }

        if (BountyParse.TryParseEvent(t, out var kind, out _, out _, out _))
        {
            _s.LastEvent = t;
            _s.LastEventUtc = DateTime.UtcNow;
            _dirty = true;
            if (kind != BountyEventKind.Held)
            {
                // Awards can come every kill (a 1-kill bounty with no cooldown): the interval holds.
                if (WindowOpen) Schedule(now, EventDebounceMs, respectMinInterval: true);
                else _stale = true;
            }
        }
        return false;
    }

    // ── Applying server output (internal for the host tests) ────────────────

    /// <summary>
    /// Applies parsed "/bounty list" lines. A complete reply (footer seen) replaces the list in
    /// server order; a partial one only updates or appends the rewards it names.
    /// </summary>
    internal void ApplyList(IReadOnlyList<BountyListLine> lines, bool complete, bool noneAnywhere, DateTime nowUtc)
    {
        var old = new Dictionary<string, BountyEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _s.Entries)
            old[BountyParse.Key(e.Where, e.Amount, e.Item, e.KillsRequired)] = e;

        var fresh = new List<BountyEntry>(lines.Count);
        foreach (var l in lines)
        {
            old.TryGetValue(BountyParse.Key(l.Where, l.Amount, l.Item, l.KillsRequired), out var prev);
            var e = new BountyEntry
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
            _s.Entries = fresh;
        }
        else
        {
            foreach (var e in fresh)
            {
                string key = BountyParse.Key(e.Where, e.Amount, e.Item, e.KillsRequired);
                int i = _s.Entries.FindIndex(x => string.Equals(BountyParse.Key(x.Where, x.Amount, x.Item, x.KillsRequired),
                                                                key, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) _s.Entries[i] = e; else _s.Entries.Add(e);
            }
        }
        if (complete || lines.Count > 0) _s.ListedUtc = nowUtc;
        if (complete) _s.NoneAnywhere = noneAnywhere;
        _dirty = true;
    }

    /// <summary>
    /// Applies a "/bounty" (where you stand) line to the one listed reward it can only mean.
    /// Returns false when no reward or more than one matches.
    /// </summary>
    internal bool ApplyHere(BountyHereLine h, DateTime nowUtc)
    {
        BountyEntry? match = null;
        foreach (var e in _s.Entries)
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

        var prev = new BountyEntry { Status = match.Status, Kills = match.Kills };
        ApplyStatus(match, h.Status, prev, nowUtc);
        _dirty = true;
        return true;
    }

    /// <summary>Copies a parsed status onto <paramref name="e"/> and stamps ChangedUtc when kills went up or an award reset them.</summary>
    private static void ApplyStatus(BountyEntry e, BountyStatusInfo st, BountyEntry? prev, DateTime nowUtc)
    {
        e.Status = (int)st.Status;
        e.Kills = st.Status == BountyStatus.Counting ? st.Kills : 0;
        e.UnlockAtUtc = st.Status == BountyStatus.Cooldown ? nowUtc.AddSeconds(st.WaitSeconds) : default;
        e.QbRequired = st.QbRequired;
        e.QbHave = st.QbHave;
        if (prev == null) return;
        bool wasCounting = prev.Status == (int)BountyStatus.Counting;
        bool gained = wasCounting && st.Status == BountyStatus.Counting && st.Kills > prev.Kills;
        bool awarded = wasCounting && prev.Kills > 0
                       && (st.Status == BountyStatus.Cooldown || (st.Status == BountyStatus.Counting && st.Kills < prev.Kills));
        if (gained || awarded) e.ChangedUtc = nowUtc;
    }

    /// <summary>The status to show now: a cooldown that has ended is "0 of N kills".</summary>
    internal static (BountyStatus Status, int Kills) Effective(BountyEntry e, DateTime nowUtc)
    {
        var st = (BountyStatus)e.Status;
        if (st == BountyStatus.Cooldown && nowUtc >= e.UnlockAtUtc) return (BountyStatus.Counting, 0);
        return (st, st == BountyStatus.Counting ? e.Kills : 0);
    }

    /// <summary>"Closest first" order: counting by fraction left, then cooldowns by time left, then QB-locked.</summary>
    private static double SortKey(BountyEntry e, DateTime now)
    {
        var (st, kills) = Effective(e, now);
        return st switch
        {
            BountyStatus.Counting => e.KillsRequired > 0 ? 1.0 - kills / (double)e.KillsRequired : 1.0,
            BountyStatus.Cooldown => 2.0 + Math.Min((e.UnlockAtUtc - now).TotalSeconds, 1e9) / 1e9,
            _ => 4.0,
        };
    }

    // ── Engine bridge ───────────────────────────────────────────────────────

    /// <summary>RynthPluginGetBountiesJson. Each call also marks the window open for the read schedule.</summary>
    public string BuildJson()
    {
        _lastPollMs = NowMs;
        var now = DateTime.UtcNow;
        var p = new BountyBridgePayload
        {
            Unsupported = _unsupported,
            Reading = _capActive,
            Status = _status,
            ListedAgoSec = _s.ListedUtc == default ? -1 : AgoSeconds(now - _s.ListedUtc),
            NoneAnywhere = _s.NoneAnywhere,
            LastEvent = _s.LastEvent,
            LastEventAgoSec = _s.LastEventUtc == default ? -1 : AgoSeconds(now - _s.LastEventUtc),
            RefreshOnKills = _s.RefreshOnKills,
            MinRefreshSeconds = _s.MinRefreshSeconds,
            PeriodicRefreshMinutes = _s.PeriodicRefreshMinutes,
            HideQbLocked = _s.HideQbLocked,
            SortByProgress = _s.SortByProgress,
        };

        IEnumerable<BountyEntry> shown = _s.Entries;
        if (_s.HideQbLocked)
        {
            p.HiddenCount = _s.Entries.Count(e => e.Status == (int)BountyStatus.NeedsQb);
            shown = shown.Where(e => e.Status != (int)BountyStatus.NeedsQb);
        }
        if (_s.SortByProgress) shown = shown.OrderBy(e => SortKey(e, now));
        foreach (var e in shown)
        {
            var (st, kills) = Effective(e, now);
            p.Rows.Add(new BountyRow
            {
                Where = e.Where,
                Amount = e.Amount,
                Item = e.Item,
                KillsRequired = e.KillsRequired,
                CooldownSeconds = e.CooldownSeconds,
                Status = (int)st,
                Kills = kills,
                UnlockInSec = st == BountyStatus.Cooldown ? AgoSeconds(e.UnlockAtUtc - now) : 0,
                QbRequired = e.QbRequired,
                QbHave = e.QbHave,
                Changed = e.ChangedUtc != default && now - e.ChangedUtc < ChangedHighlight,
            });
        }
        return JsonSerializer.Serialize(p, RynthILTJsonContext.Default.BountyBridgePayload);
    }

    private static int AgoSeconds(TimeSpan span) => (int)Math.Clamp(span.TotalSeconds, 0, int.MaxValue);

    /// <summary>
    /// RynthPluginBountyCommand: "refresh", or "set {option} {value}" with option one of
    /// refreshonkills, minrefreshseconds, periodicminutes, hideqblocked, sortbyprogress.
    /// </summary>
    public string Command(string text)
    {
        string[] parts = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;
        switch (parts[0].ToLowerInvariant())
        {
            case "refresh":
            case "read":
                return RequestRefresh();
            case "set" when parts.Length >= 3:
                return SetOption(parts[1], parts[2]);
            default:
                return $"unknown bounty command '{text}'";
        }
    }

    private string SetOption(string name, string value)
    {
        bool on = value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                  || value.Equals("on", StringComparison.OrdinalIgnoreCase);
        int.TryParse(value, out int n);
        switch (name.ToLowerInvariant())
        {
            case "refreshonkills": _s.RefreshOnKills = on; break;
            case "minrefreshseconds": _s.MinRefreshSeconds = Math.Clamp(n, MinRefreshFloorSeconds, MinRefreshCeilingSeconds); break;
            case "periodicminutes": _s.PeriodicRefreshMinutes = Math.Clamp(n, 0, PeriodicCeilingMinutes); break;
            case "hideqblocked": _s.HideQbLocked = on; break;
            case "sortbyprogress": _s.SortByProgress = on; break;
            default: return $"unknown bounty option '{name}'";
        }
        _dirty = true;
        return string.Empty;
    }

    // ── Chat command ────────────────────────────────────────────────────────

    /// <summary>"/ilt bounty [refresh|list]".</summary>
    public void HandleChatCommand(string[] parts)
    {
        string verb = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "list";
        switch (verb)
        {
            case "refresh":
            case "read":
                _chat("[Bounties] " + RequestRefresh());
                break;
            case "list":
            case "print":
            case "show":
                PrintList();
                _chat("[Bounties] The Bounties window: the ILT button on the RynthCore bar, or /rc bounties.");
                break;
            default:
                _chat("[Bounties] /ilt bounty [list|refresh] - list the tracked bounties, or re-read /bounty list (while the window is open).");
                break;
        }
    }

    /// <summary>One-line chat text for an entry.</summary>
    internal static string Describe(BountyEntry e, DateTime nowUtc)
    {
        var (st, kills) = Effective(e, nowUtc);
        string reward = $"{e.Amount:N0} {e.Item}";
        return st switch
        {
            BountyStatus.Counting => $"{e.Where}: {kills:N0}/{e.KillsRequired:N0} kills -> {reward}",
            BountyStatus.Cooldown => $"{e.Where}: unlocks in {BountyParse.Duration(e.UnlockAtUtc - nowUtc)}, then {e.KillsRequired:N0} kills -> {reward}",
            _ => $"{e.Where}: needs {e.QbRequired:N0} QB (you have {e.QbHave:N0}) -> {reward}",
        };
    }

    /// <summary>"/ilt bounty list": the tracked bounties in chat.</summary>
    public void PrintList()
    {
        if (_unsupported) { _chat("[Bounties] This server has no /bounty command."); return; }
        if (_s.Entries.Count == 0)
        {
            _chat(_s.NoneAnywhere ? "[Bounties] No bounties are active anywhere right now."
                : "[Bounties] Nothing read yet. Open the Bounties window (the ILT button, or /rc bounties).");
            return;
        }
        var now = DateTime.UtcNow;
        string age = _s.ListedUtc == default ? "never" : BountyParse.Duration(now - _s.ListedUtc) + " ago";
        _chat($"[Bounties] {_s.Entries.Count} bounty reward(s), read {age}:");
        foreach (var e in _s.Entries) _chat("[Bounties]   " + Describe(e, now));
    }

    // ── Persistence ─────────────────────────────────────────────────────────

    private BountyState Load()
    {
        var s = new BountyState();
        try
        {
            if (_filePath.Length > 0 && File.Exists(_filePath))
                s = JsonSerializer.Deserialize(File.ReadAllText(_filePath), RynthILTJsonContext.Default.BountyState) ?? new BountyState();
        }
        catch (Exception ex)
        {
            _log($"[RynthILT] Bounties: {_filePath} unreadable, starting empty: {ex.Message}");
            s = new BountyState();
        }
        s.Entries ??= new List<BountyEntry>();
        s.Entries.RemoveAll(e => e == null);
        s.LastEvent ??= string.Empty;
        s.MinRefreshSeconds = Math.Clamp(s.MinRefreshSeconds, MinRefreshFloorSeconds, MinRefreshCeilingSeconds);
        s.PeriodicRefreshMinutes = Math.Clamp(s.PeriodicRefreshMinutes, 0, PeriodicCeilingMinutes);
        return s;
    }

    /// <summary>Writes bounties.json when something changed (also called at logout).</summary>
    public void Save()
    {
        _lastSaveMs = NowMs;
        if (!_dirty || _filePath.Length == 0) { _dirty = false; return; }
        _dirty = false;
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_s, RynthILTJsonContext.Default.BountyState));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"[RynthILT] Bounties: saving {_filePath} failed: {ex.Message}");
        }
    }
}
